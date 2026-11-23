using System.Collections.Generic;

namespace Stellar.Atlas;

// Chests opened BEFORE Atlas (in-game 2026-11-23: the list showed server chests at 1 m and 31 m that this character had
// opened earlier). The game never sends an opened chest to that character again, so the scanner can't see it open — but
// it can see that nothing is there: a server CHEST point that stays within AbsentRadius (horizontal) of the player for
// AbsentSeconds in a row, while the scan is healthy, with no live chest within LiveNearRadius of it (or its uuid not
// live), is "not here for this character" → stored with the opened chests (OpenedStore "A" lines), hidden from NEAREST,
// the guide and the map. Local only: it is never reported (it says nothing about other characters). Chests only.
//
// Cost (2 Hz, world-ready): one pass over the scene's points (a distance test each — no allocation), dictionary work only
// for the few chest points within 30 m.
public sealed partial class Plugin
{
    private const float AbsentRadius   = 30f;
    private const float AbsentSeconds  = 5f;
    private const float LiveNearRadius = 3f;

    private struct NearMark { public float Since; public int Seq; }

    private readonly Dictionary<long, NearMark> _nearSince = new();
    private readonly List<long> _nearDrop = new();
    private int _nearSeq;

    private void TickAbsent()
    {
        if (!WorldReady() || _scene <= 0 || !_scanner.Healthy || !_fetchedOnce) { _nearSince.Clear(); return; }
        var pl = _scanner.PlayerPos;
        float now = _services.Framework.TimeNow;
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
