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

    private void FastTick()
    {
        if (!WorldReady()) return;
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
            _map.Tick(_scene, _fetchedOnce, p.y, _mask, _model, _scanner.Live, _opened, _services.Framework.TimeNow);
        }
    }

    private void ScanTick()
    {
        if (!WorldReady() || _scene <= 0) return;
        _scanner.Poll();
        TickSharingEdge();
        TickAbsent();
        if (Shown || _guide.Active) RecomputeNearest();
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
                               $"(share={_share}, notice={_noticeShown}, key={(AtlasClient.HasKey ? "set" : "none")}, scene={_scene})");
        }
        if (_scene > 0 && !_fetchInFlight && _lastFetchAt >= 0f && now - _lastFetchAt >= RefreshSeconds) Fetch();
        TickFlash(now);
    }

    private void OnSceneChange(int scene)
    {
        if (SharingActive) _reports.Tick(force: true);   // flush the old scene's finds now
        StopGuide(clearMarker: true);                     // a guide never survives a scene change
        _scene = scene;
        _scanner.Reset();
        _model.Clear();
        _map.OnScene();
        _opened.SetScene(scene);
        _fetchState = AtlasClient.FetchState.NoData;
        _fetchedOnce = false;
        _fetchedUnix = 0;
        _statusKey = null;
        if (scene > 0) Fetch();
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
        if (_scene <= 0 || !_scanner.HasPlayerPos) return;
        var p = _scanner.PlayerPos;
        _model.Recompute(p.x, p.y, p.z, _mask, _scanner.Live, _opened);
    }

    // Reports were only queued from the scanner's Appeared event, so everything already in range when sharing became
    // active (the player pressed "OK, share" with chests in view) was never sent (in-game 2026-11-23: server had 0
    // reports). On the rising edge of SharingActive — consent, the Share chip, or the first world tick of a session that
    // already consented — queue every tracked live entity once (the queue still dedupes per uuid per session).
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
