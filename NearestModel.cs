using System;
using System.Collections.Generic;

namespace Stellar.Atlas;

// Merges the server's points for the current scene with the live entities the game streams, and keeps the 8 nearest
// SHOWN ones (NEAREST panel + guide). Main thread only; no allocation per recompute — fixed scratch arrays, insertion
// into a bounded top-K, strings come from caches.
//
// Live merge: a live entity supersedes the server row for the same physical thing (same uuid, or — a questlog seed
// without a uuid yet — same chip within SupersedeRadius on (x, z)) → the row shows as "Seen live" with the live
// position. Opened chests (this character) and stale server points are never shown.
internal sealed class NearestModel
{
    public const int Rows = 8;
    private const int MaxCand = 48;
    private const float SupersedeRadius = 5f;   // = the server's ATLAS_MATCH_RADIUS_M

    public enum Src : byte { Live, MapData, Unverified }

    public struct Row
    {
        public Chip Chip;
        public long Uuid, ServerId;
        public int Cfg;
        public string Label;
        public float X, Y, Z;
        public bool HasY;
        public Src Src;
        public float Dist;
        public int Dir;          // 0 N, 1 NE, 2 E … 7 NW
    }

    private readonly GameLua _lua;
    public readonly Row[] Out = new Row[Rows];
    public int Count { get; private set; }
    public int Version { get; private set; }     // bumps when the visible rows change (UI re-reads cheaply)

    private AtlasPoint[] _points = Array.Empty<AtlasPoint>();
    private readonly HashSet<long> _pointUuids = new();
    private readonly AtlasPoint?[] _sp = new AtlasPoint?[MaxCand];
    private readonly float[] _sd = new float[MaxCand];
    private readonly LiveEnt?[] _lp = new LiveEnt?[MaxCand];
    private readonly float[] _ld = new float[MaxCand];
    private int _openedVersion = -1;

    public int ChestsOnMap { get; private set; }
    public int BooksOnMap { get; private set; }
    public int Nearby { get; private set; }

    public NearestModel(GameLua lua) => _lua = lua;

    public void SetPoints(AtlasPoint[] points)
    {
        _points = points;
        _pointUuids.Clear();
        foreach (var p in points) if (p.Uuid != 0) _pointUuids.Add(p.Uuid);
        _openedVersion = -1;   // re-mark hidden on the next recompute
    }

    public AtlasPoint[] Points => _points;

    // Stale / opened-by-this-character flags, re-marked only when the opened set changes (cheap otherwise).
    public void RefreshHidden(OpenedStore opened)
    {
        if (_openedVersion == opened.Version) return;
        _openedVersion = opened.Version;
        foreach (var p in _points) p.Hidden = p.Stale || opened.Matches(p);
    }

    // A live entity the server doesn't know yet (no row with its uuid, no uuid-less seed of its chip within 5 m).
    public bool IsLiveOnly(LiveEnt e) => IsLiveOnly(e.Uuid, e.Chip, e.X, e.Z);
    public bool IsLiveOnly(long uuid, Chip chip, float x, float z) => !_pointUuids.Contains(uuid) && !NearSeed(chip, x, z);

    public void Clear() { SetPoints(Array.Empty<AtlasPoint>()); Count = 0; Version++; ChestsOnMap = 0; BooksOnMap = 0; Nearby = 0; }

    // ~2 Hz while the window is open (and whenever the guide needs a fresh "next nearest").
    public void Recompute(float px, float py, float pz, int mask, Dictionary<long, LiveEnt> live, OpenedStore opened)
    {
        RefreshHidden(opened);

        // 1) nearest live entities that are shown
        int ln = 0, nearby = 0, liveOnlyChests = 0;
        foreach (var e in live.Values)
        {
            if (!e.Tracked || !e.HasPos || opened.HasUuid(e.Uuid)) continue;
            if (AtlasGroups.IsChest(e.Chip) && IsLiveOnly(e)) liveOnlyChests++;
            if ((mask & (1 << (int)e.Chip)) == 0) continue;
            nearby++;
            ln = Insert(_lp, _ld, ln, e, Dist(e.X, e.Y, e.Z, true, px, py, pz));
        }

        // 2) nearest server points that are shown and not superseded by a live entity
        int sn = 0, chests = 0, books = 0;
        foreach (var p in _points)
        {
            if (p.Hidden) continue;
            if (AtlasGroups.IsChest(p.Chip)) chests++;
            else if (p.Chip == Chip.Books) books++;
            if ((mask & (1 << (int)p.Chip)) == 0) continue;
            float d = Dist(p.X, p.Y, p.Z, p.HasY, px, py, pz);
            if (sn == MaxCand && d >= _sd[MaxCand - 1]) continue;
            if (Superseded(p, live)) continue;
            sn = Insert(_sp, _sd, sn, p, d);
        }
        ChestsOnMap = chests + liveOnlyChests;
        BooksOnMap = books;
        Nearby = nearby;

        // 3) merge the two sorted lists into the 8 rows
        int i = 0, j = 0, n = 0;
        bool changed = false;
        while (n < Rows && (i < ln || j < sn))
        {
            Row r = default;
            if (j >= sn || (i < ln && _ld[i] <= _sd[j]))
            {
                var e = _lp[i]!;
                r.Chip = e.Chip; r.Uuid = e.Uuid; r.Cfg = e.Cfg; r.X = e.X; r.Y = e.Y; r.Z = e.Z; r.HasY = true;
                r.Src = Src.Live; r.Dist = _ld[i]; r.Label = LiveName(e);
                i++;
            }
            else
            {
                var p = _sp[j]!;
                r.Chip = p.Chip; r.Uuid = p.Uuid; r.ServerId = p.Id; r.Cfg = p.ConfigId; r.X = p.X; r.Y = p.Y; r.Z = p.Z; r.HasY = p.HasY;
                r.Src = p.IsUnverified ? Src.Unverified : Src.MapData; r.Dist = _sd[j]; r.Label = ServerName(p);
                j++;
            }
            r.Dir = Compass(r.X - px, r.Z - pz);
            if (!changed)
            {
                ref var o = ref Out[n];
                changed = n >= Count || o.Uuid != r.Uuid || o.ServerId != r.ServerId || (int)o.Dist != (int)r.Dist
                          || o.Dir != r.Dir || o.Src != r.Src || !ReferenceEquals(o.Label, r.Label);
            }
            Out[n++] = r;
        }
        if (changed || n != Count) Version++;
        Count = n;
        Array.Clear(_lp, 0, ln);
        Array.Clear(_sp, 0, sn);
    }

    private bool Superseded(AtlasPoint p, Dictionary<long, LiveEnt> live)
    {
        if (p.Uuid != 0) return live.TryGetValue(p.Uuid, out var e) && e.Tracked && e.HasPos;
        foreach (var l in live.Values)
        {
            if (!l.Tracked || !l.HasPos || l.Chip != p.Chip) continue;
            float dx = l.X - p.X, dz = l.Z - p.Z;
            if (dx * dx + dz * dz <= SupersedeRadius * SupersedeRadius) return true;
        }
        return false;
    }

    // A live chest standing on a uuid-less seed is the same chest (not a second one) — chest count only.
    private bool NearSeed(Chip chip, float x, float z)
    {
        foreach (var p in _points)
        {
            if (p.Uuid != 0 || p.Chip != chip) continue;
            float dx = x - p.X, dz = z - p.Z;
            if (dx * dx + dz * dz <= SupersedeRadius * SupersedeRadius) return true;
        }
        return false;
    }

    // Sorted insert into a bounded top-K; returns the new length.
    private static int Insert<T>(T?[] items, float[] dists, int n, T item, float d) where T : class
    {
        if (n == MaxCand && d >= dists[n - 1]) return n;
        int k = n < MaxCand ? n : n - 1;
        while (k > 0 && dists[k - 1] > d) { items[k] = items[k - 1]; dists[k] = dists[k - 1]; k--; }
        items[k] = item; dists[k] = d;
        return n < MaxCand ? n + 1 : n;
    }

    // 3D when the point has a height; questlog seeds have none → horizontal distance.
    public static float Dist(float x, float y, float z, bool hasY, float px, float py, float pz)
    {
        float dx = x - px, dz = z - pz, dy = hasY ? y - py : 0f;
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    // +Z = north, +X = east (Unity world axes; NOT yet checked against the game's map compass in-game).
    public static int Compass(float dx, float dz)
    {
        if (dx * dx + dz * dz < 0.01f) return 0;
        float deg = MathF.Atan2(dx, dz) * (180f / MathF.PI);
        if (deg < 0f) deg += 360f;
        return (int)MathF.Floor((deg + 22.5f) / 45f) & 7;
    }

    // Names come from the game's CollectionTable (localized by the game); cached per config id by GameLua.
    public string LiveName(LiveEnt e)
    {
        var info = _lua.Cfg(e.Cfg);
        return info.Name.Length > 0 ? info.Name : "";
    }

    public string ServerName(AtlasPoint p)
    {
        if (p.ConfigId > 0 && AtlasGroups.HasCollectionConfig(p.Chip))
        {
            var info = _lua.Cfg(p.ConfigId);
            if (info.Name.Length > 0) return info.Name;
        }
        return p.Label;
    }
}
