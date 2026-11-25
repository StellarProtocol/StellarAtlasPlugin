using System;
using System.Collections.Generic;
using System.Text;

namespace Stellar.Atlas;

// Magical creatures (approved prototype _scratch\prototypes\atlas-creatures.tpl.html): the per-line status from the Atlas
// server + what this player sees live + the player's line → the MAGICAL CREATURES panel (Plugin.Window.Creatures.cs),
// the game guide marker and round head tiles on the game's big map / minimap (MapMarkers.CreatureTiles.cs).
//
// Scheduling (Plugin-Performance-Rules.md §7 — everything ≤ 1 Hz except what rides existing ticks):
//   1 Hz   line read (one constant Lua chunk, compiled helper) while in Asteria Plains or the window is shown; server id
//          once per world entry; status GET every FetchEvery s while (Asteria Plains or window shown) AND ≥ 1 creature
//          chip on (own HTTP gate — never ahead of / behind a chest fetch); sightings; map flag state
//   2 Hz   the creature part of the collection walk (EntityScanner.Creatures.cs, Asteria Plains only); panel rows while
//          the window is shown or a creature is guided (CreatureModel.Recompute — no allocation)
//
// Sightings go out only after the shared-map notice was acknowledged (SharingActive), like chest reports. A creature
// killed — or that left view while the player stood within 50 m of it — is reported gone (CreatureSightings.cs).
public sealed partial class Plugin
{
    private const float FetchEvery = 30f;
    private const float ServerRetry = 5f;

    private readonly SessionLua _session;
    private readonly CreatureModel _cre = new();
    private readonly CreatureSightings _sightings;

    private int _line;                       // current line (0 = unknown)
    private bool _lineLogged;
    private string _creServer = "";
    private bool _serverResolved;
    private float _serverTryAt = -100f;
    private bool _creFetchInFlight, _creOffline, _creHasData;
    private float _creFetchAt = -1000f;

    private int CreatureMask => _mask & Creatures.AllBits;
    // Lines are per scene: "your line" only means something in Asteria Plains. Elsewhere every alive line is just "Line N".
    // _scene IS the scene the player is in (Lua Z.StageMgr, read with the line at 1 Hz and on a table-scene change —
    // Plugin.Tick.cs CheckScene), never the big map's browsed scene.
    private bool InCreatureScene => _scene == Creatures.Scene;
    private int CreLine => InCreatureScene ? _line : 0;
    private bool CreaturesShown => CreatureMask != 0;

    // Unix ms for "updated X s ago" / "in 1 h 12 m": the framework's interpolated server clock (never ZServerTime,
    // Server-Clock-and-Ping-HUD.md), the PC clock before it has synced (0).
    private long NowMs()
    {
        long ms = 0;
        try { ms = _services.CombatSnapshot.ServerNowMs; } catch { }
        return ms > 0 ? ms : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    // 1 Hz (SlowTick, world-ready).
    private void TickCreatures(float now)
    {
        ReadLine();   // every second: the line + the actual scene (one constant Lua chunk)
        bool inScene = InCreatureScene;
        _scanner.CreaturesOn = inScene;
        if (!CharReady) return;
        if (!_serverResolved && now >= _serverTryAt) ResolveServer(now);

        bool want = CreaturesShown && (inScene || Shown);
        if (want && _creServer.Length > 0 && !_creFetchInFlight && now - _creFetchAt >= FetchEvery) FetchCreatures(now);

        if (SharingActive && _creServer.Length > 0)
        {
            long nowMs = NowMs();
            if (inScene) foreach (var c in _scanner.LiveCreatures.Values) _sightings.Observe(c, _line, nowMs);
            _sightings.Tick(_creServer);   // also after leaving the scene: a queued "gone" still goes out
        }
        // the guided creature was defeated in view → its row turns Absent and the guide stops (prototype 2c)
        if (_guide.Active && _guide.Cre != 0 && (_guide.Line == 0 || _guide.Line == CreLine))   // only the guided line's kill
            foreach (var c in _scanner.LiveCreatures.Values)
                if (c.Dead && c.Idx == _guide.Cre - 1) { StopGuide(clearMarker: true); break; }

        // map flags: tile states every second, the flag SET only on material change (≤ 1 / min unless a chip toggled)
        if (_creHasData || _scanner.LiveCreatures.Count > 0 || _cre.Published.Count > 0)
            if (_cre.UpdateFlags(CreLine, CreatureMask, _scanner.LiveCreatures, now))
            {
                _map.CreaturesChanged();
                _services.Log.Info($"[Atlas] creature map flags: {_cre.Published.Count} published (line {CreLine})");
            }
    }

    private void ReadLine()
    {
        int line = _session.ReadLine(out int actual);
        NoteActualScene(actual);
        if (line == _line) return;
        if (!_lineLogged || line != 0)
            _services.Log.Info($"[Atlas] line: {line} (sceneline_data.PlayerLineId{(_lineLogged ? "" : ", first read")})");
        _lineLogged = true;
        _line = line;
    }

    // Table scene moved / world back (Plugin.Tick.cs CheckScene): read the actual scene now. The line of that read is
    // dropped — OnSceneChange zeroes it and the next 1 Hz tick reads it, so a stale line of the old scene never shows.
    // Returns the actual scene, 0 when unknown (Lua not ready).
    private int RefreshActualScene() { _session.ReadLine(out int actual); NoteActualScene(actual); return actual; }

    // The single place a scene change starts (Plugin.Tick.cs OnSceneChange): only ever from the actual scene.
    private void NoteActualScene(int actual)
    {
        if (actual <= 0 || actual == _scene) return;
        OnSceneChange(actual);
    }

    private void FetchCreatures(float now)
    {
        _creFetchInFlight = true;
        _creFetchAt = now;
        string server = _creServer;
        _client.FetchCreatures(server, res =>
        {
            _creFetchInFlight = false;
            if (res.Server != _creServer) return;
            if (!res.Ok)
            {
                if (!_creOffline) _services.Log.Info($"[Atlas] creatures: status unavailable (server '{server}') — showing {(_creHasData ? "the last copy" : "nothing yet")}");
                _creOffline = true;
                return;
            }
            bool first = !_creHasData;
            _creOffline = false;
            _creHasData = true;
            _cre.SetStatus(res);
            if (first)
            {
                _cre.ForcePublish();
                int alive = 0;
                foreach (var ci in res.ByIdx) if (ci != null) foreach (var l in ci.Lines) if (l.Alive) alive++;
                _services.Log.Info($"[Atlas] creatures: server '{server}' tracked={res.Tracked} stale={res.Stale} alive lines={alive}");
            }
        });
    }

    // Row recompute (ScanTick, 2 Hz) while the window is shown or a creature is guided.
    private void RecomputeCreatures()
    {
        if (!CharReady) return;
        // Away from Asteria Plains the player's coordinates belong to another map: no distance / compass (rows sort by
        // line status only) and no "your line".
        bool dist = InCreatureScene && _scanner.HasPlayerPos;
        var p = _scanner.PlayerPos;
        _cre.Recompute(dist, p.x, p.y, p.z, CreLine, CreatureMask, _scanner.LiveCreatures);
    }

    // ── server id (once per world entry) ──────────────────────────────────────────────────────────────────────────
    // server_data values per region, from the "[Atlas] server:" log (SEA = zoneId 8001; JP still unknown):
    // keyed by NowSelectData.zoneId (or the description). The config override `CreatureServerId` (settings section,
    // e.g. "sea") always wins; an unknown region sends a slug of the description (the server answers tracked:false
    // or 404 for a name it doesn't know; sightings then build that server's player-only list).
    private static readonly Dictionary<int, string> ServerByZone = new()
    {
        { 8001, "sea" },   // in-game 2026-11-24: description 'haoplay-sea…' zoneId=8001 id=1.0 NowSelectServerId=1
        // TODO(in-game): JP zoneId → "jp"
    };
    private static readonly Dictionary<string, string> ServerByDescription = new(StringComparer.OrdinalIgnoreCase)
    {
    };

    private void ResolveServer(float now)
    {
        _serverTryAt = now + ServerRetry;
        var s = _session.ReadServer();
        if (!s.Known) return;   // server_data not filled yet → again in 5 s
        _serverResolved = true;
        string id = ResolveCreatureServer(in s);
        _services.Log.Info($"[Atlas] server: description='{s.Description}' zoneId={s.ZoneId} id={s.Id} NowSelectServerId={s.SelectedId}" +
                           $" → creature server '{(id.Length > 0 ? id : "(none)")}'");
        if (id == _creServer) return;
        _creServer = id;
        _creHasData = false;
        _creFetchAt = -1000f;
    }

    // Isolated on purpose (fill the tables above after the in-game log). Result matches ^[a-z][a-z0-9-]{0,15}$ or is "".
    private string ResolveCreatureServer(in SessionLua.ServerInfo s)
    {
        string cfg = Slug(_cfg.Get<string>("CreatureServerId", "") ?? "");
        if (cfg.Length > 0) return cfg;
        if (s.ZoneId != 0 && ServerByZone.TryGetValue(s.ZoneId, out var byZone)) return byZone;
        if (s.Description.Length > 0 && ServerByDescription.TryGetValue(s.Description, out var byDesc)) return byDesc;
        return Slug(s.Description);
    }

    // Lower-case slug: a-z / 0-9 kept, any other run → one '-', must start with a letter, ≤ 16 chars, no trailing '-'.
    private static string Slug(string text)
    {
        var sb = new StringBuilder(16);
        foreach (char ch0 in text.Trim().ToLowerInvariant())
        {
            if (sb.Length >= 16) break;
            char ch = ch0;
            bool ok = (ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9');
            if (!ok) { if (sb.Length > 0 && sb[^1] != '-') sb.Append('-'); continue; }
            if (sb.Length == 0 && ch >= '0' && ch <= '9') continue;   // must start with a letter
            sb.Append(ch);
        }
        while (sb.Length > 0 && sb[^1] == '-') sb.Length--;
        return sb.ToString();
    }

    // ── entity walk + lifecycle hooks ────────────────────────────────────────────────────────────────────────────────
    // Raised inside the 2 Hz walk (O(1)): killed → gone; left view while the player stood within 50 m of it → gone
    // (despawned); further away it just left view range.
    private void OnCreatureLeft(LiveCreature c, bool killed)
    {
        bool near = false;
        if (!killed && c.HasPos && _scanner.HasPlayerPos)
        {
            float dx = c.X - _scanner.PlayerPos.x, dz = c.Z - _scanner.PlayerPos.z;
            near = dx * dx + dz * dz <= CreatureSightings.GoneRadius * CreatureSightings.GoneRadius;
        }
        if ((killed || near) && SharingActive && _creServer.Length > 0) _sightings.Gone(c, _line);
        if (killed && _guide.Active && _guide.Cre == c.Idx + 1 && (_guide.Line == 0 || _guide.Line == CreLine)) StopGuide(clearMarker: true);
    }

    private void OnCreaturesLogout()
    {
        _line = 0;
        _serverResolved = false;
        _serverTryAt = -100f;
        _session.OnLogout();
        _sightings.ResetSeen();
        _cre.Clear();
        _cre.ForcePublish();
    }

    private void OnCreatureChipToggled()
    {
        _cre.ForcePublish();
        if (CreaturesShown && _creServer.Length > 0 && !_creHasData) _creFetchAt = -1000f;   // fetch on the next tick
    }

    // ── guide ─────────────────────────────────────────────────────────────────────────────────────────────────────
    private void GuideToCreature(in CreatureModel.Row r)
    {
        if (!r.HasPos) return;
        // Goal scene = Asteria Plains even from another map (the PositionShare goal takes the scene id). Away from it there is
        // no physics for the ground raycast → the server / fixed-spot y is used as is.
        bool away = !InCreatureScene;
        _guide = new GuideState { Active = true, Cre = r.Idx + 1, Scene = Creatures.Scene, Uuid = r.Uuid, X = r.X, Y = r.Y, Z = r.Z,
                                  HasY = away || r.Y != 0f };
        if (!_guide.HasY) TryGroundHeight(_services.Framework.TimeNow);
        SetMarker();
    }

    private bool IsCreatureGuided(in CreatureModel.Row r) => _guide.Active && !_guide.Arrived && _guide.Cre == r.Idx + 1;

    // Where the guided creature is now: in view → its live position; else the panel row's (server line / fixed spot).
    private bool CreatureTarget(out float x, out float y, out float z)
    {
        int idx = _guide.Cre - 1;
        LiveCreature? best = null;
        // Guided to one line (lines dialog): the live creature counts only when that line is the player's own line in
        // Asteria Plains; otherwise the server's position for that line (or the fixed spot), never another line's.
        if (_guide.Line > 0 && _guide.Line != CreLine)
            return LinePos(idx, _guide.Line, out x, out y, out z);
        foreach (var c in _scanner.LiveCreatures.Values)
            if (c.Idx == idx && c.HasPos && !c.Dead && (best == null || c.Dist < best.Dist)) best = c;
        if (best != null) { x = best.X; y = best.Y; z = best.Z; return true; }
        if (_guide.Line > 0) return LinePos(idx, _guide.Line, out x, out y, out z);
        for (int i = 0; i < _cre.Count; i++)
        {
            ref var r = ref _cre.Out[i];
            if (r.Idx != idx || !r.HasPos) continue;
            x = r.X; y = r.Y; z = r.Z;
            return true;
        }
        x = y = z = 0f;
        return false;
    }
}
