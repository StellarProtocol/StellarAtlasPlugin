using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Stellar.Atlas;

// Magical creatures: the server's per-line status (AtlasClient.Creatures.cs) merged with what this player sees live
// (EntityScanner.Creatures.cs) and the player's own line. Main thread only. Produces
//   • Rows — one per creature whose SHOW chip is on (MAGICAL CREATURES panel + guide), sorted: on your line / seen live
//     first, then other alive lines by distance, then absent;
//   • map flags — one per (creature, group of alive lines within ClusterRadius) + an absent fixed-schedule boarlet at its
//     spot, with a tile STATE (here / other lines / absent + badge text) that the icon pass draws without a map rebuild.
//
// ⚠️ Map rebuild budget (Auto-Gather-and-Interaction.md map flags: MapFlagShowListChanged = 80–280 ms): the flag SET is published to
// MapMarkers only when it materially changes (a flag added / removed, or moved > ClusterRadius) and at most once per
// PublishGap for creature-only changes (a chip toggle publishes at once). Your line changing, HP, badge or time only
// change the tile STATE → re-skin in place, never a rebuild. Positions are sticky within ClusterRadius so a creature
// wandering a few metres never changes the set.
//
// Strings (lines "2 · 6 · 9", badges "2·6·9", times "14:00") are rebuilt only when their inputs change.
internal sealed class CreatureModel
{
    public const float ClusterRadius = 30f;
    private const float PublishGap = 60f;
    private const long  FlagBase   = 700000000L;   // map flag n = FlagBase + idx × 1000 + lowest line of the group (0 = absent spot); server points < 600 000 000, live-only 600 000 000+

    public enum Kind : byte { Live, Here, Player, Other, Absent }

    public struct Row
    {
        public int Idx;
        public Kind Kind;
        public bool HasPos;
        public float X, Y, Z, Dist;
        public int Dir, HpPct, Line;      // Line = the shown line (Here / Player / single Other)
        public int LineCount;             // alive lines (Other)
        public string Lines, Spot;        // "2 · 6 · 9" (Other) · server spot name
        public long NextSpawnAt, SeenAt;  // unix ms (Absent / Player)
        public long Uuid;                 // Live
        public bool Fighting;             // Live: HP below max
    }

    public enum TileKind : byte { Hidden, Here, Other, Absent }

    public sealed class Tile { public int Idx; public TileKind Kind; public string Badge = ""; public string Key = ""; public bool Time; }

    public struct Flag { public long N; public int Idx; public float X, Y, Z; }

    public readonly Row[] Out = new Row[Creatures.Count];
    public int Count { get; private set; }
    public bool AnyAlive { get; private set; }
    public int Version { get; private set; }

    public AtlasClient.CreatureStatus? Status { get; private set; }

    // published map flag set + live tile states (keyed by n)
    public readonly List<Flag> Published = new();
    public ulong PublishedSig { get; private set; }
    public int PublishVersion { get; private set; }
    private float _publishedAt = -1000f;
    private bool _forcePublish = true;
    private readonly List<Flag> _cand = new();
    private readonly Dictionary<long, Tile> _tiles = new();
    private readonly Dictionary<long, Tile> _candTiles = new();
    private readonly Stack<Tile> _tilePool = new();

    // per-creature caches
    private readonly ulong[] _linesKey = new ulong[Creatures.Count];
    private readonly string[] _linesText = new string[Creatures.Count];
    private readonly Dictionary<ulong, string> _badgeCache = new();
    private readonly Dictionary<long, string> _times = new();
    private readonly StringBuilder _sb = new(32);
    private readonly int[] _lineBuf = new int[256];
    private readonly bool[] _used = new bool[256];

    public void SetStatus(AtlasClient.CreatureStatus s) { Status = s; }

    public void Clear() { Count = 0; AnyAlive = false; Version++; }

    // Chip toggled / login / scene change: the next flag set goes out without waiting PublishGap.
    public void ForcePublish() => _forcePublish = true;

    // ── rows (2 Hz while the window is shown or a creature guide is active) ─────────────────────────────────────────
    public void Recompute(bool hasPlayer, float px, float py, float pz, int line, int mask, Dictionary<long, LiveCreature> live)
    {
        int n = 0;
        bool anyAlive = false, changed = false;
        for (int idx = 0; idx < Creatures.Count; idx++)
        {
            if ((mask & Creatures.Bit(idx)) == 0) continue;
            Row r = default;
            r.Idx = idx; r.Lines = ""; r.Spot = "";
            Build(ref r, line, live);
            if (r.HasPos && hasPlayer)
            {
                r.Dist = NearestModel.Dist(r.X, r.Y, r.Z, true, px, py, pz);
                r.Dir = NearestModel.Compass(r.X - px, r.Z - pz);
            }
            else r.Dist = float.MaxValue;
            if (r.Kind != Kind.Absent) anyAlive = true;
            // insertion sort by (kind group, distance)
            int k = n;
            while (k > 0 && Before(in r, in Out[k - 1])) { Out[k] = Out[k - 1]; k--; }
            Out[k] = r;
            n++;
        }
        for (int i = 0; i < n && !changed; i++) changed = i >= Count || Differs(in Out[i], in _prev[i]);
        if (changed || n != Count) { Version++; Array.Copy(Out, _prev, n); }
        Count = n;
        AnyAlive = anyAlive;
    }

    private readonly Row[] _prev = new Row[Creatures.Count];

    private static int Group(Kind k) => k switch { Kind.Live or Kind.Here => 0, Kind.Other or Kind.Player => 1, _ => 2 };
    private static bool Before(in Row a, in Row b) => Group(a.Kind) != Group(b.Kind) ? Group(a.Kind) < Group(b.Kind) : a.Dist < b.Dist;

    private static bool Differs(in Row a, in Row b) =>
        a.Idx != b.Idx || a.Kind != b.Kind || (int)a.Dist != (int)b.Dist || a.Dir != b.Dir || a.HpPct != b.HpPct || a.Line != b.Line
        || !ReferenceEquals(a.Lines, b.Lines) || !ReferenceEquals(a.Spot, b.Spot) || a.NextSpawnAt != b.NextSpawnAt || a.SeenAt != b.SeenAt
        || a.Fighting != b.Fighting;

    private void Build(ref Row r, int line, Dictionary<long, LiveCreature> live)
    {
        // 1) in view right now (not killed) → live position + HP
        LiveCreature? best = null;
        foreach (var c in live.Values)
            if (c.Idx == r.Idx && c.HasPos && !c.Dead && (best == null || c.Dist < best.Dist)) best = c;
        if (best != null)
        {
            r.Kind = Kind.Live; r.HasPos = true; r.X = best.X; r.Y = best.Y; r.Z = best.Z; r.HpPct = best.HpPct;
            r.Uuid = best.Uuid; r.Line = line; r.Fighting = best.MaxHp > 0 && best.Hp < best.MaxHp;
            return;
        }
        var info = Status?.ByIdx[r.Idx];
        bool untracked = Status != null && !Status.Tracked;
        AtlasClient.CreatureLine? mine = null, nearestOther = null, newestPlayer = null;
        int others = 0;
        if (info != null)
            foreach (var l in info.Lines)
            {
                if (!l.Alive) continue;
                if (l.Line == line && line > 0) { mine = l; continue; }
                others++;
                if (nearestOther == null || (l.HasPos && !nearestOther.HasPos)) nearestOther = l;
                if (l.Player && (newestPlayer == null || l.UpdatedAt > newestPlayer.UpdatedAt)) newestPlayer = l;
            }
        if (mine != null)
        {
            r.Kind = untracked && mine.Player ? Kind.Player : Kind.Here;
            Fill(ref r, mine);
            return;
        }
        if (others > 0)
        {
            if (untracked && newestPlayer != null) { r.Kind = Kind.Player; Fill(ref r, newestPlayer); return; }
            r.Kind = Kind.Other;
            r.LineCount = others;
            r.Lines = LinesText(r.Idx, info!, line);
            Fill(ref r, nearestOther!);
            if (others > 1) { r.Line = 0; r.HpPct = -1; r.Spot = ""; }
            return;
        }
        r.Kind = Kind.Absent;
        r.HpPct = -1;
        r.NextSpawnAt = info?.NextSpawnAt ?? 0;
        if (Creatures.FixedSpot(r.Idx, out float fx, out float fy, out float fz)) { r.HasPos = true; r.X = fx; r.Y = fy; r.Z = fz; }
    }

    private static void Fill(ref Row r, AtlasClient.CreatureLine l)
    {
        r.Line = l.Line; r.Spot = l.Spot; r.SeenAt = l.UpdatedAt;
        r.HpPct = l.MaxHp > 0 ? (int)Math.Clamp(l.Hp * 100 / l.MaxHp, 0, 100) : -1;
        if (l.HasPos) { r.HasPos = true; r.X = l.X; r.Y = l.Y; r.Z = l.Z; }
        else if (Creatures.FixedSpot(r.Idx, out float fx, out float fy, out float fz)) { r.HasPos = true; r.X = fx; r.Y = fy; r.Z = fz; }
    }

    // "2 · 6 · 9" — the creature's alive lines other than yours, ascending; rebuilt only when that set changes.
    private string LinesText(int idx, AtlasClient.CreatureInfo info, int line)
    {
        int n = SortedAlive(info, line, null);
        ulong key = 1469598103934665603UL;
        for (int i = 0; i < n; i++) key = (key ^ (ulong)_lineBuf[i]) * 1099511628211UL;
        if (key == _linesKey[idx] && _linesText[idx] != null) return _linesText[idx];
        var sb = _sb.Clear();
        for (int i = 0; i < n; i++) { if (i > 0) sb.Append(" · "); sb.Append(_lineBuf[i].ToString(CultureInfo.InvariantCulture)); }
        _linesKey[idx] = key;
        return _linesText[idx] = sb.ToString();
    }

    // Alive lines (excluding `skip`; restricted to `only` positions when given) sorted into _lineBuf; returns the count.
    private int SortedAlive(AtlasClient.CreatureInfo info, int skip, bool[]? only)
    {
        int n = 0;
        for (int i = 0; i < info.Lines.Length && n < _lineBuf.Length; i++)
        {
            var l = info.Lines[i];
            if (!l.Alive || l.Line == skip || (only != null && !only[i])) continue;
            int k = n++;
            while (k > 0 && _lineBuf[k - 1] > l.Line) { _lineBuf[k] = _lineBuf[k - 1]; k--; }
            _lineBuf[k] = l.Line;
        }
        return n;
    }

    // ── map flags (1 Hz in Asteria Plains while a creature chip is on) ──────────────────────────────────────────────
    // Rebuilds the candidate set + tile states; publishes the set when it materially changed (rules above). Returns true
    // when a new set was published (the caller re-stages / re-selects the map).
    public bool UpdateFlags(int line, int mask, Dictionary<long, LiveCreature> live, float now)
    {
        _cand.Clear();
        foreach (var t in _candTiles.Values) _tilePool.Push(t);
        _candTiles.Clear();
        for (int idx = 0; idx < Creatures.Count; idx++)
        {
            if ((mask & Creatures.Bit(idx)) == 0) continue;
            int before = _cand.Count;
            LiveCreature? lc = null;
            foreach (var c in live.Values) if (c.Idx == idx && c.HasPos && !c.Dead) { lc = c; break; }
            var info = Status?.ByIdx[idx];
            if (info != null) Clusters(idx, info, line, lc);
            if (lc != null && !Covered(before, lc.X, lc.Z))
                AddFlag(idx, line > 0 ? line : 999, lc.X, lc.Y, lc.Z, TileKind.Here, line > 0 ? BadgeOne(line) : "");
            if (_cand.Count == before && Creatures.HasSchedule(idx) && Creatures.FixedSpot(idx, out float fx, out float fy, out float fz))
                AddFlag(idx, 0, fx, fy, fz, TileKind.Absent, info != null && info.NextSpawnAt > 0 ? TimeText(info.NextSpawnAt) : "", time: true);
        }
        // tile states for what is on the map now; a published flag that is no longer a candidate hides in place
        ulong sig = 1469598103934665603UL;
        foreach (var f in _cand)
        {
            sig = (sig ^ (ulong)f.N) * 1099511628211UL;
            sig = (sig ^ (ulong)(long)MathF.Round(f.X)) * 1099511628211UL;
            sig = (sig ^ (ulong)(long)MathF.Round(f.Z)) * 1099511628211UL;
        }
        foreach (var kv in _tiles)
        {
            var dst = kv.Value;
            var kind = _candTiles.TryGetValue(kv.Key, out var src) ? src.Kind : TileKind.Hidden;
            string badge = src?.Badge ?? "";
            if (kind == dst.Kind && ReferenceEquals(badge, dst.Badge)) continue;   // unchanged: no new key string
            dst.Kind = kind; dst.Badge = badge; dst.Time = src?.Time ?? false;
            dst.Key = TileKey(dst);
        }
        if (sig == PublishedSig && !_forcePublish) return false;
        if (!_forcePublish && now - _publishedAt < PublishGap) return false;
        _forcePublish = false;
        _publishedAt = now;
        PublishedSig = sig;
        Published.Clear();
        Published.AddRange(_cand);
        _tiles.Clear();
        foreach (var kv in _candTiles)
            _tiles[kv.Key] = new Tile { Idx = kv.Value.Idx, Kind = kv.Value.Kind, Badge = kv.Value.Badge, Time = kv.Value.Time, Key = TileKey(kv.Value) };
        PublishVersion++;
        return true;
    }

    // The tile to draw for flag n (null = not one of ours).
    public Tile? TileFor(long n) => _tiles.TryGetValue(n, out var t) ? t : null;

    public static bool IsCreatureN(long n) => n >= FlagBase && n < FlagBase + Creatures.Count * 1000L;

    private readonly bool[] _inCluster = new bool[256];

    // Greedy clusters of the alive lines with a position (≤ ClusterRadius on x/z from the cluster's first line).
    private void Clusters(int idx, AtlasClient.CreatureInfo info, int line, LiveCreature? lc)
    {
        int m = Math.Min(info.Lines.Length, _used.Length);
        Array.Clear(_used, 0, m);
        for (int i = 0; i < m; i++)
        {
            var a = info.Lines[i];
            if (_used[i] || !a.Alive) continue;
            float ax = a.HasPos ? a.X : 0f, ay = a.Y, az = a.HasPos ? a.Z : 0f;
            if (!a.HasPos && !Creatures.FixedSpot(idx, out ax, out ay, out az)) continue;
            Array.Clear(_inCluster, 0, m);
            bool mine = false;
            for (int j = i; j < m; j++)
            {
                var b = info.Lines[j];
                if (_used[j] || !b.Alive) continue;
                float bx = b.HasPos ? b.X : ax, bz = b.HasPos ? b.Z : az;
                if (Sq(bx - ax) + Sq(bz - az) > ClusterRadius * ClusterRadius) continue;
                _used[j] = true; _inCluster[j] = true;
                if (b.Line == line && line > 0) mine = true;
            }
            if (mine && lc != null) { ax = lc.X; ay = lc.Y; az = lc.Z; }   // in view: the exact live position
            int cnt = SortedAlive(info, -1, _inCluster);
            AddFlag(idx, _lineBuf[0], ax, ay, az, mine ? TileKind.Here : TileKind.Other, Badge(cnt));
        }
    }

    private bool Covered(int from, float x, float z)
    {
        for (int i = from; i < _cand.Count; i++)
            if (Sq(_cand[i].X - x) + Sq(_cand[i].Z - z) <= ClusterRadius * ClusterRadius) return true;
        return false;
    }

    private void AddFlag(int idx, int slot, float x, float y, float z, TileKind kind, string badge, bool time = false)
    {
        long n = FlagBase + idx * 1000L + Math.Clamp(slot, 0, 999);
        while (_candTiles.ContainsKey(n)) n++;
        // sticky position: a published flag that moved ≤ ClusterRadius keeps its published spot (no rebuild)
        foreach (var f in Published)
            if (f.N == n && Sq(f.X - x) + Sq(f.Z - z) <= ClusterRadius * ClusterRadius) { x = f.X; y = f.Y; z = f.Z; break; }
        _cand.Add(new Flag { N = n, Idx = idx, X = x, Y = y, Z = z });
        var t = _tilePool.Count > 0 ? _tilePool.Pop() : new Tile();
        t.Idx = idx; t.Kind = kind; t.Badge = badge; t.Time = time;
        _candTiles[n] = t;
    }

    // "2·6·9" for the first `cnt` sorted lines in _lineBuf (or one line), cached by the line set.
    private string Badge(int cnt)
    {
        ulong key = 1469598103934665603UL;
        for (int i = 0; i < cnt; i++) key = (key ^ (ulong)_lineBuf[i]) * 1099511628211UL;
        if (_badgeCache.TryGetValue(key, out var s)) return s;
        var sb = _sb.Clear();
        for (int i = 0; i < cnt; i++) { if (i > 0) sb.Append('·'); sb.Append(_lineBuf[i].ToString(CultureInfo.InvariantCulture)); }
        s = sb.ToString();
        if (_badgeCache.Count > 256) _badgeCache.Clear();
        _badgeCache[key] = s;
        return s;
    }

    private string BadgeOne(int line) { _lineBuf[0] = line; return Badge(1); }

    // Local "HH:mm" of a unix-ms time, cached per value (two boarlets → two entries; a schedule moves a few times a day).
    public string TimeText(long ms)
    {
        if (_times.TryGetValue(ms, out var s)) return s;
        if (_times.Count > 32) _times.Clear();
        s = DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
        _times[ms] = s;
        return s;
    }

    private static string TileKey(Tile t) => t.Kind == TileKind.Hidden ? "" : t.Idx.ToString(CultureInfo.InvariantCulture) + "|" + (int)t.Kind + "|" + t.Badge;

    private static float Sq(float v) => v * v;
}
