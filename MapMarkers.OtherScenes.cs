using System;
using System.Collections.Generic;
using System.Globalization;

namespace Stellar.Atlas;

// Other maps on the BIG map (user-approved 2026-11-23). The big map (M) can browse other maps with its map picker; the
// game then calls our wrapper with THAT scene id, and the wrapper records it in _G.__atlas_other (only when it differs
// from the current scene, _G.__atlas_cur). On the next "map rebuilt" bump (FrameTick) the id is read and cleared; the
// plugin then:
//   1. loads that scene's points through the normal client path (disk cache + conditional GET, worker thread);
//   2. applies the same SHOW filters + this character's opened / not-here hides for THAT scene (OpenedStore.MatchesIn),
//      fixed order (chip, then id), capped at MapCap;
//   3. stages them as _G.__atlas_map[scene] with the same batching, then ONE refresh dispatch (same DispatchGap).
// At most MaxExtraScenes extra scenes are kept (LRU); all of them are dropped from the Lua table when the big map closes
// or the current scene changes (no rebuild needed for that: the minimap only ever asks for the current scene). Their
// units are re-skinned on the big-map root only.
// ⚠️ The refresh event (MapFlagShowListChanged) has no big-map-only variant: it also rebuilds the minimap's flags (same
// current-scene set, ~one rebuild's cost). It happens once per newly viewed scene, not per browse.
internal sealed partial class MapMarkers
{
    private const int MaxExtraScenes = 3;

    private readonly List<int> _extraOrder = new();   // staged extra scenes, least recently viewed first
    private int _otherWanted, _otherLoading;
    private AtlasPoint[]? _otherPoints;
    private int _otherPointsScene;
    private bool _jobExtra;

    // FrameTick, right after a rebuild bump: did the big map ask for a scene that isn't the current one?
    private void NoteOtherRequest()
    {
        if (!Lua.TryReadGlobalNumber("__atlas_other", out double v)) return;
        Lua.DoString("rawset(_G,'__atlas_other',nil)");
        int s = (int)v;
        if (s <= 0 || s == _curScene || s >= 10000) return;
        int at = _extraOrder.IndexOf(s);
        if (at >= 0) { _extraOrder.RemoveAt(at); _extraOrder.Add(s); return; }   // already staged → just most recent
        _otherWanted = s;
    }

    // Tick (5 Hz, idle, installed): load a wanted scene, then start its push. Returns true when it used this tick.
    private bool TickOther(float now, int mask)
    {
        if (_otherWanted == 0) return false;
        if ((mask & AtlasGroups.ShownBits) == 0) { _otherWanted = 0; return false; }   // nothing on → maps untouched
        if (_otherPoints == null || _otherPointsScene != _otherWanted)
        {
            if (_otherLoading == _otherWanted) return false;
            int s = _otherLoading = _otherWanted;
            _client.FetchScene(s, res =>
            {
                if (_otherLoading == s) _otherLoading = 0;
                if (res.Scene != _otherWanted) return;   // the player moved on to another map meanwhile
                _otherPoints = res.Points;
                _otherPointsScene = res.Scene;
            });
            return false;
        }
        if (now - _lastDispatchAt < DispatchGap) return false;
        BuildOther(_otherWanted, _otherPoints, mask);
        _otherPoints = null;
        int scene = _otherWanted;
        _otherWanted = 0;
        if (_extraOrder.Count >= MaxExtraScenes) DropExtra(_extraOrder[0]);
        _extraOrder.Add(scene);
        if (_job.Count == 0)
        {
            // nothing to show there (filters, all opened, no data / offline without a copy): remember it so the map
            // picker doesn't re-ask, but don't rebuild the maps for nothing
            _services.Log.Info($"[Atlas] big map viewing scene {scene}: 0 points staged");
            return false;
        }
        _jobScene = scene;
        _jobExtra = true;
        _jobPos = 0;
        if (!Call("__atlas_mstage()")) { _jobPos = -1; _extraOrder.Remove(scene); return true; }
        return true;
    }

    // Same rules as Select for the current scene, minus anything live (we only see the current scene live).
    private NearestModel? _modelRef;
    private OpenedStore? _openedRef;

    private void BuildOther(int scene, AtlasPoint[] points, int mask)
    {
        _cands.Clear();
        foreach (var p in points)
            if (!p.Stale && (mask & (1 << (int)p.Chip)) != 0 && p.Id > 0 && p.Id < LiveIdBase
                && (_openedRef == null || !_openedRef.MatchesIn(scene, p))) _cands.Add(p);
        int n = _cands.Count;
        if (_keys.Length < n) { _keys = new long[n * 2]; _idx = new int[n * 2]; }
        for (int i = 0; i < n; i++)
        {
            var ap = (AtlasPoint)_cands[i];
            _keys[i] = ((long)ap.Chip << 40) | ap.Id;
            _idx[i] = i;
        }
        Array.Sort(_keys, _idx, 0, n);
        _job.Clear();
        int take = Math.Min(n, MapCap);
        for (int k = 0; k < take; k++)
        {
            var p = (AtlasPoint)_cands[_idx[k]];
            string name = _modelRef != null ? _modelRef.ServerName(p) : p.Label;
            _job.Add(new Entry { N = p.Id, Chip = p.Chip, X = p.X, Y = p.HasY ? p.Y : 0f, Z = p.Z,
                                 Name = name.Length > 0 ? name : ChipName(p.Chip) });
        }
    }

    // Commit of an extra scene's push (PushStep): icons + log.
    private void CommittedOther(int scene, int count)
    {
        CommitIcons(scene, _job, extra: true);
        _services.Log.Info($"[Atlas] big map viewing scene {scene}: {count} points staged");
    }

    private void DropExtra(int scene)
    {
        _extraOrder.Remove(scene);
        Call("__atlas_mdrop(" + scene.ToString(CultureInfo.InvariantCulture) + ")");
        DropIcons(scene);
    }

    // Big map closed / scene changed: forget every extra scene (no rebuild: only the big map ever showed them).
    private void DropAllExtras()
    {
        _otherWanted = 0; _otherPoints = null; _otherLoading = 0;
        while (_extraOrder.Count > 0) DropExtra(_extraOrder[0]);
    }
}
