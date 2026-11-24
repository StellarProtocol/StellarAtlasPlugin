using System;
using Stellar.Abstractions.Domain;

namespace Stellar.Atlas;

// Scheduling. Everything runs off IFramework.Every, which rides the plugin Update tick (world-gated by the framework), and
// each callback also self-gates on Phase == World && IsWorldActive && !Loading before touching the game
// (Game-Phases-and-Tick-Gating.md; Plugin-Performance-Rules.md §3 "never bind game types before world-ready").
//   5 Hz  character / scene id / player position / guide arrival       (three field reads + one bound pos read);
//         map markers: a re-check every 2 s, a push only when the shown set changed, ≤ 1 map rebuild per 5 s
//         (MapMarkers.cs)
//   2 Hz  live collection walk (always in world: opened chests must be caught while the window is closed) and, only
//         while the window is shown or a guide is active, the NEAREST recompute; the map icon pass (one Lua number
//         read unless the game just rebuilt its map flags)
//   1 Hz  report batches, 10-min points refresh, the header's "copy from N min ago" age
public sealed partial class Plugin
{
    private const float RefreshSeconds = 600f;   // re-fetch the scene's points every ~10 min

    private IDisposable? _tickFast, _tickScan, _tickSlow;

    // scene + server state
    private int _scene;
    private bool _fetchInFlight;
    private float _lastFetchAt = -1f;
    private AtlasClient.FetchState _fetchState = AtlasClient.FetchState.NoData;
    private bool _fetchedOnce;
    private float _queueLogAt = -100f;             // a fetch for the current scene came back (synced or not)
    private long _fetchedUnix;

    private void StartTicks()
    {
        _tickFast = _services.Framework.Every(TimeSpan.FromMilliseconds(200), FastTick);
        _tickScan = _services.Framework.Every(TimeSpan.FromMilliseconds(500), ScanTick);
        _tickSlow = _services.Framework.Every(TimeSpan.FromSeconds(1), SlowTick);
        _services.Framework.Update += OnFrame;
    }

    // Every plugin tick: only the map-rebuild check (one Lua number read) and, right after a rebuild, the short reskin
    // burst (MapMarkers.Icons.cs FrameTick) — everything else here runs at ≤ 5 Hz.
    private void OnFrame(float _)
    {
        if (!WorldReady() || _scene <= 0) return;
        _map.FrameTick(_services.Framework.TimeNow);
    }

    private void StopTicks()
    {
        try { _services.Framework.Update -= OnFrame; } catch { }
        _tickFast?.Dispose(); _tickScan?.Dispose(); _tickSlow?.Dispose();
        _tickFast = _tickScan = _tickSlow = null;
    }

    private bool WorldReady()
    {
        var cs = _services.ClientState;
        return cs.Phase == GamePhase.World && cs.IsWorldActive && (cs.UiState & GameUIState.Loading) == 0;
    }

    // Name warm-up (in-game 2026-11-23: "slice detail: name lookup of 1 config ids (first 52016) 8.2 ms" — a fixed
    // first-use cost of the game's CollectionTable, not per id). Paid ONCE per session while the loading screen still
    // covers it: plugin ticks only run while IsWorldActive, and the Loading bit is usually still up right after the
    // scene is entered (Game-Phases-and-Tick-Gating.md §2 / effect-filter finding) — so this tick is the earliest a
    // plugin can do it. If Lua isn't usable then, it runs on the first world-ready tick before any other Atlas work.
    // Resolves every known chest config id in ONE call; the names stay cached for the session. Books use the server
    // label (no table name), so they need no warm-up.
    private bool _warmed;

    private void TryWarmUp()
    {
        if (_warmed || !_services.Lua.Ready) return;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        int n = _lua.CfgBatch(AtlasGroups.WarmChestIds, 0, AtlasGroups.WarmChestIds.Length);
        if (n < 0) return;   // Lua not ready yet → the next tick
        _warmed = true;
        double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        bool veiled = (_services.ClientState.UiState & GameUIState.Loading) != 0;
        _services.Log.Info($"[Atlas] warm-up: table names ready ({n} ids) in {ms:F1} ms ({(veiled ? "loading screen" : "first world tick")})");
    }

    private void FastTick()
    {
        var cs = _services.ClientState;
        if (!_warmed && cs.Phase == GamePhase.World && cs.IsWorldActive) TryWarmUp();
        if (!WorldReady()) { NoteWorldUnstable(); return; }
        long charId = 0;
        try { charId = _services.PlayerState.CharId; } catch { }
        if (charId != 0 && charId != _charId) OnCharacter(charId);

        int scene = _scanner.ReadSceneId();
        if (scene != _scene) OnSceneChange(scene);
        if (_scene <= 0) return;

        _scanner.ReadPlayer();
        TickGuide();
        if (_scanner.HasPlayerPos)
        {
            var p = _scanner.PlayerPos;
            _map.CreatureMask = CreatureMask;
            if (CharReady) _map.Tick(_scene, _fetchedOnce, p.y, _mask & ~Creatures.AllBits, _model, _scanner.Live, _opened, _services.Framework.TimeNow);
        }
    }

    private void ScanTick()
    {
        if (!WorldReady() || _scene <= 0) return;
        _scanner.Poll();
        TickSharingEdge();
        TickAbsent();
        if (Shown || _guide.Active) RecomputeNearest();
        if (CreaturesShown && (Shown || _guide.Cre != 0)) RecomputeCreatures();
        TickCreatureLines();
        _map.PollIcons(_services.Framework.TimeNow);
    }

    private void SlowTick()
    {
        if (!WorldReady()) return;
        if (SharingActive) _reports.Tick();
        float now = _services.Framework.TimeNow;
        if (now - _queueLogAt >= 60f)
        {
            _queueLogAt = now;
            _services.Log.Info($"[Atlas] report queue: {_reports.Status()} · sharing={(SharingActive ? "on" : "off")} " +
                               $"(notice acknowledged={_noticeSeen}, key={(AtlasClient.HasKey ? "set" : "none")}, scene={_scene})");
        }
        if (_scene > 0 && !_fetchInFlight && _lastFetchAt >= 0f && now - _lastFetchAt >= RefreshSeconds) Fetch();
        TickFlash(now);
        TickCreatures(now);
    }

    private void OnSceneChange(int scene)
    {
        if (SharingActive) _reports.Tick(force: true);   // flush the old scene's finds now
        // a point guide never survives a scene change; a creature guide (its goal names Asteria Plains) is kept and its
        // marker re-placed once the new scene is up, so guiding from another map still works after the loading screen
        if (_guide.Cre == 0) StopGuide(clearMarker: true); else _guide.Remark = _guide.MarkerSet || _guide.Pending;   // pending: set on arriving in its scene
        _groundCache.Clear();
        NoteWorldUnstable();
        int prevActual = _actualScene;
        _scene = scene;
        // _scene may be the scene the big map browses, not where the player is: the creature "you are here" logic uses the
        // actual scene, and the line (per scene) is dropped only when the ACTUAL scene changed (re-read within 1 s).
        RefreshActualScene();
        if (_actualScene != prevActual) _line = 0;
        _scanner.Reset();
        _model.Clear();
        _map.OnScene(scene, _services.Framework.TimeNow);
        _opened.SetScene(scene);
        _sightings.ResetSeen();
        _fetchState = AtlasClient.FetchState.NoData;
        _fetchedOnce = false;
        _fetchedUnix = 0;
        _statusKey = null;
        if (scene > 0) { LoadCachedFirst(scene); Fetch(); }
    }

    // Login / scene change (in-game 2026-11-23: "markers shown after 26352 ms" at login — the server fetch queued
    // behind the prestage index + loads on the client's one-request gate): the disk copy goes up at once, so the
    // flags are pushed without waiting for the network; the server's answer (Fetch) replaces it, and the map is
    // pushed again only if the shown set differs. Ignored once the server answered first.
    private void LoadCachedFirst(int scene)
    {
        _client.LoadCached(scene, res =>
        {
            if (res.Scene != _scene || _fetchedOnce || res.State != AtlasClient.FetchState.Cached) return;
            _fetchState = res.State;
            _fetchedOnce = true;
            _fetchedUnix = res.FetchedUnix;
            _model.SetPoints(res.Points);
            _statusKey = null;
            RecomputeNearest();
            _services.Log.Info($"[Atlas] scene {res.Scene}: {res.Points.Length} points (disk cache, server check pending)");
        });
    }

    // Disk cache first + conditional GET on the worker; the result lands back here on the main thread.
    private void Fetch()
    {
        int scene = _scene;
        _fetchInFlight = true;
        _lastFetchAt = _services.Framework.TimeNow;
        _client.FetchScene(scene, res =>
        {
            _fetchInFlight = false;
            if (res.Scene != _scene) return;   // left the scene meanwhile
            // A failed refresh must not throw away a good copy already on screen.
            if (res.State == AtlasClient.FetchState.NoData && _fetchState != AtlasClient.FetchState.NoData)
            { _fetchState = AtlasClient.FetchState.Offline; _statusKey = null; return; }
            _fetchState = res.State;
            _fetchedOnce = true;
            _fetchedUnix = res.FetchedUnix;
            _model.SetPoints(res.Points);
            _statusKey = null;
            RecomputeNearest();
            _services.Log.Info($"[Atlas] scene {res.Scene}: {res.Points.Length} points ({res.State})");
        });
    }

    private void RecomputeNearest()
    {
        if (!CharReady) return;   // never list with another character's (or no) filter
        if (_scene <= 0 || !_scanner.HasPlayerPos) return;
        var p = _scanner.PlayerPos;
        _model.Recompute(p.x, p.y, p.z, _mask, _scanner.Live, _opened);
    }

    // Reports were only queued from the scanner's Appeared event, so everything already in range when sharing became
    // active (the first-open notice shown with chests in view) was never sent (in-game 2026-11-23: server had 0
    // reports). On the rising edge of SharingActive — "Got it" clicked, or the first world tick of a session that already
    // acknowledged it — queue every tracked live entity once (the queue still dedupes per uuid per session).
    private bool _wasSharing;

    private void TickSharingEdge()
    {
        bool active = SharingActive;
        if (active && !_wasSharing)
            foreach (var e in _scanner.Live.Values)
                if (e.Tracked && e.HasPos && !_opened.HasUuid(e.Uuid)) _reports.Enqueue(_scene, e);
        _wasSharing = active;
    }

    // ── scanner events (2 Hz poll, main thread, O(1)) ─────────────────────────────────────────────────────────────
    private void OnLiveAppeared(LiveEnt e)
    {
        if (SharingActive && !_opened.HasUuid(e.Uuid)) _reports.Enqueue(_scene, e);
    }

    private void OnLiveOpened(LiveEnt e)
    {
        _opened.Add(_scene, e.Uuid, e.X, e.Z);
        OnChestOpened(e);   // Plugin.Guide.cs: the 2d flash + guide moves on
    }
}
