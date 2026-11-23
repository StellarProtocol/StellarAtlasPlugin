using System.Collections.Generic;

namespace Stellar.Atlas;

// Chests opened BEFORE Atlas (in-game 2026-11-23: the list showed server chests at 1 m and 31 m that this character had
// opened earlier). The game never sends an opened chest to that character again, so the scanner can't see it open — but
// it can see that nothing is there: a server CHEST point that stays within AbsentRadius (horizontal) of the player for
// AbsentSeconds in a row with no live chest within LiveNearRadius of it (or its uuid not live) is "not here for this
// character" → stored with the opened chests (OpenedStore "A" lines), hidden from NEAREST, the guide and the map. Local
// only: never reported (it says nothing about other characters). Chests only.
//
// Tuning (measured in-game 2026-11-23): chests stream in up to ~136 m away, but the grid AOI drops unopened chests as
// close as 51 m → AbsentRadius 45 m (user, 2026-11-23). Dwell AbsentSeconds 2 s from the moment the player is within
// 45 m, counted only while (a) the scan is healthy, (b) the world has been stable for StableSecs (entities arrive in a
// burst after a scene load / line change), and (c) the player didn't jump more than TeleportJumpM between two scans (a
// teleport resets every dwell timer and the stable clock).
//
// Live wins: a not-here point (or an opened chest) seen LIVE again — its uuid, or a live chest within LiveNearRadius of
// it — is un-hidden at once and removed from the per-character file.
//
// Cost (2 Hz, world-ready): one pass over the scene's points (a distance test each — no allocation), dictionary work only
// for the few chest points within 30 m.
public sealed partial class Plugin
{
    private const float AbsentRadius   = 45f;
    private const float AbsentSeconds  = 2f;
    private const float LiveNearRadius = 3f;
    private const float StableSecs     = 3f;
    private const float TeleportJumpM  = 30f;

    private struct NearMark { public float Since; public int Seq; }

    private readonly Dictionary<long, NearMark> _nearSince = new();
    private readonly List<long> _nearDrop = new();
    private int _nearSeq;
    private float _stableSince = -1f, _lastAbsX, _lastAbsZ;
    private bool _haveLastAbs;
    private readonly List<long> _unhidden = new();

    // FastTick: anything that is not a stable world (loading, scene change) restarts the stable clock.
    private void NoteWorldUnstable() { _stableSince = -1f; _nearSince.Clear(); _haveLastAbs = false; }

    private void TickAbsent()
    {
        if (!WorldReady() || _scene <= 0 || !_scanner.Healthy || !_fetchedOnce || !CharReady) { NoteWorldUnstable(); return; }
        var pl = _scanner.PlayerPos;
        float now = _services.Framework.TimeNow;
        UnhideSeenLive();
        if (_stableSince < 0f) _stableSince = now;
        if (_haveLastAbs && Sq(pl.x - _lastAbsX) + Sq(pl.z - _lastAbsZ) > TeleportJumpM * TeleportJumpM)
        {
            _stableSince = now;   // teleport / fast travel: restart the clock, drop every dwell
            _nearSince.Clear();
        }
        _lastAbsX = pl.x; _lastAbsZ = pl.z; _haveLastAbs = true;
        if (now - _stableSince < StableSecs) return;
        _model.RefreshHidden(_opened);
        _nearSeq++;
        foreach (var p in _model.Points)
        {
            if (p.Hidden || p.Id == 0 || !AtlasGroups.IsChest(p.Chip)) continue;
            float dx = p.X - pl.x, dz = p.Z - pl.z;
            if (dx * dx + dz * dz > AbsentRadius * AbsentRadius) continue;
            if (LiveChestAt(p)) { _nearSince.Remove(p.Id); continue; }
            if (!_nearSince.TryGetValue(p.Id, out var m)) { _nearSince[p.Id] = new NearMark { Since = now, Seq = _nearSeq }; continue; }
            if (now - m.Since >= AbsentSeconds)
            {
                _nearSince.Remove(p.Id);
                _opened.AddAbsent(_scene, p.Id, p.X, p.Z);
                _services.Log.Info($"[Atlas] chest point {p.Id} not here for this character (scene {_scene}) — hidden");
                continue;
            }
            _nearSince[p.Id] = new NearMark { Since = m.Since, Seq = _nearSeq };
        }
        // A point that left the radius (or turned out present) starts over next time: "continuously" means continuously.
        foreach (var kv in _nearSince) if (kv.Value.Seq != _nearSeq) _nearDrop.Add(kv.Key);
        foreach (var id in _nearDrop) _nearSince.Remove(id);
        _nearDrop.Clear();
    }

    // Live wins: opened chests visible again, and not-here points with a live chest on them, come back at once.
    private void UnhideSeenLive()
    {
        bool anyAbsent = _opened.HasAbsentInScene();
        foreach (var e in _scanner.Live.Values)
        {
            if (!e.Tracked || !e.HasPos || !AtlasGroups.IsChest(e.Chip)) continue;
            if (_opened.HasUuid(e.Uuid) && _opened.RemoveOpened(e.Uuid))
                _services.Log.Info($"[Atlas] chest {e.Uuid} seen live again — unhidden");
            if (!anyAbsent) continue;
            _opened.RemoveAbsentNear(e.X, e.Z, LiveNearRadius, _unhidden);
            foreach (long id in _unhidden) _services.Log.Info($"[Atlas] chest point {id} seen live again — unhidden");
            _unhidden.Clear();
            anyAbsent = _opened.HasAbsentInScene();
        }
    }

    private bool LiveChestAt(AtlasPoint p)
    {
        var live = _scanner.Live;
        if (p.Uuid != 0 && live.TryGetValue(p.Uuid, out var byId) && byId.Tracked) return true;
        foreach (var e in live.Values)
        {
            if (!e.Tracked || !e.HasPos || !AtlasGroups.IsChest(e.Chip)) continue;
            float dx = e.X - p.X, dz = e.Z - p.Z;
            if (dx * dx + dz * dz <= LiveNearRadius * LiveNearRadius) return true;
        }
        return false;
    }
}
