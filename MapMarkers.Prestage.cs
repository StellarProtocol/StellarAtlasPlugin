using System;
using System.Collections.Generic;
using System.Globalization;

namespace Stellar.Atlas;

// "Prepare in advance" (user-approved 2026-11-23), like the game's own NPC flags: every scene's flags are ALREADY in
// _G.__atlas_map[scene] when the GAME builds a map (scene load, big-map browse), so they appear in the game's own first
// RefreshMap — no extra MapFlagShowListChanged (a ~100–280 ms rebuild) on scene change or browse.
//
//   1. Index: GET /v1/scenes (per-scene versions) after login, on every scene change and every IndexEvery seconds.
//      Every scene with a disk copy is queued; a copy whose version differs from the index is refreshed from the server
//      (AtlasClient.LoadScene, worker thread). A scene never visited (no copy) is fetched only on its first visit /
//      browse — then staged and dispatched once, as before.
//   2. Staging, one scene at a time: same filter / order / cap / hides as the current scene (BeginBuild + ContinueBuild = Select minus
//      the live part), pushed in 25-point batches within StageBudgetMs per tick, committed WITHOUT a dispatch — unless
//      that scene is on screen (the current scene is owned by Select; a big-map-browsed scene dispatches once).
//      All scenes use the same cap as the current one (MapCap): the staged set must be exactly what Select would build
//      on arrival, or entering the scene would rebuild anyway; it also bounds the minimap's flag count.
//   3. Scene change (OnScene): a staged scene adopts its staged set + signature, so Select sees "no change" and does
//      not dispatch. Logged: "[Atlas] scene change S: flags ready (staged)" / "not staged → fetch".
//   4. Chip toggles: after the 1.2 s hold every staged scene is re-queued with the new filter (silent, except the
//      current scene — Select — and a browsed one). Scenes with nothing to show are dropped from Lua (memory bound) and
//      remembered as empty, so a browse doesn't fetch them.
// The icon pass only covers the current scene (both maps) + the one scene being browsed on the big map.
internal sealed partial class MapMarkers
{
    private const double StageBudgetMs = 2.0;
    private const float SettleTimeout = 20f;   // prestaging resumes anyway if the current scene never settles (no data)
    private const float  IndexEvery    = 600f;

    private sealed class StagedScene
    {
        public List<Entry> Entries = new();
        public ulong Sig, SigAll;
        public int Mask;
    }

    private readonly Dictionary<int, StagedScene> _staged = new();
    private readonly Dictionary<int, int> _emptyScenes = new();   // scene → mask it was empty with
    private Dictionary<int, long> _index = new();
    private readonly List<int> _queue = new();                    // scenes to (re)stage, front = next
    private readonly HashSet<int> _queued = new();
    private readonly HashSet<int> _cachedScenes = new();
    private int _loading, _readyScene, _priorityScene, _viewedScene, _stagedMask = -1, _curMask;
    private AtlasPoint[]? _readyPoints;
    private bool _indexInFlight, _indexDue = true, _jobExtra;
    private float _indexAt = -1000f, _sceneChangeAt = -1f, _settleAt = -1f, _settleBy;
    private bool _curSettled = true;
    private int _stagedCount, _stagedFlags;

    private NearestModel? _modelRef;
    private OpenedStore? _openedRef;

    private void Enqueue(int scene, bool front = false)
    {
        if (scene <= 0 || scene >= 10000) return;
        if (_queued.Add(scene)) { if (front) _queue.Insert(0, scene); else _queue.Add(scene); }
        else if (front) { _queue.Remove(scene); _queue.Insert(0, scene); }
    }

    // Tick (5 Hz, idle, installed, after the current scene had its turn). Returns true when it used the tick.
    private bool TickPrestage(float now, int mask)
    {
        // index (login / scene change / every 10 min)
        if (!_indexInFlight && (_indexDue || now - _indexAt > IndexEvery))
        {
            _indexDue = false; _indexInFlight = true; _indexAt = now;
            _client.FetchIndex((idx, cached) =>
            {
                _indexInFlight = false;
                foreach (int s in cached) _cachedScenes.Add(s);
                var old = _index;
                if (idx != null) _index = idx;
                foreach (int s in _cachedScenes)
                {
                    bool known = _staged.ContainsKey(s) || _emptyScenes.ContainsKey(s);
                    bool changed = idx != null && idx.TryGetValue(s, out long v) && (!old.TryGetValue(s, out long ov) || ov != v);
                    if (!known || changed) Enqueue(s);
                }
            });
        }

        // filter changed (after the chip-click hold): everything is rebuilt with the new filter
        bool anyShown = (mask & AtlasGroups.ShownBits) != 0;
        if (mask != _stagedMask && now >= _filterHoldUntil)
        {
            _stagedMask = mask;
            if (!anyShown)
            {
                // every chip off: nothing to show anywhere → drop the other scenes from Lua at once (no load needed)
                var drop = new List<int>(_staged.Keys);
                foreach (int s in drop)
                {
                    if (s == _curScene) continue;
                    _staged.Remove(s);
                    _emptyScenes[s] = mask;
                    Call("__atlas_mdrop(" + s.ToString(CultureInfo.InvariantCulture) + ")");
                    if (s == _viewedScene) { DropIcons(s); _dispatchPending = true; }
                }
            }
            else
            {
                foreach (int s in _staged.Keys) Enqueue(s);
                foreach (int s in _emptyScenes.Keys) Enqueue(s);
            }
        }

        // a build in progress → continue it within the budget; when complete, start its push
        if (_bScene != 0)
        {
            if (_bScene == _curScene) { _bScene = 0; return false; }                     // we moved there: Select's now
            if (_bMask != mask) { Enqueue(_bScene, front: true); _bScene = 0; return false; }   // filter changed: redo
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            if (!ContinueBuild(t0)) { Slow("prestage build slice", t0); return true; }
            Slow("prestage build slice", t0);
            int scene = _bScene;
            _bScene = 0;
            _job.Clear();
            _job.AddRange(_bJob);   // the push machinery works on _job (free now: no job in progress)
            _jobSig = _bSig ^ (ulong)scene;
            _jobSigAll = _bSigAll ^ (ulong)scene;
            if (_staged.TryGetValue(scene, out var st) && st.Sig == _jobSig && st.Mask == mask) { _job.Clear(); return true; }
            if (_job.Count == 0)
            {
                bool wasStaged = _staged.Remove(scene);
                _emptyScenes[scene] = mask;
                if (wasStaged) Call("__atlas_mdrop(" + scene.ToString(CultureInfo.InvariantCulture) + ")");
                if (scene == _viewedScene) { DropIcons(scene); if (wasStaged) _dispatchPending = true; }
                if (scene == _priorityScene) { _priorityScene = 0; _services.Log.Info($"[Atlas] big map viewing scene {scene}: 0 points staged"); }
                return true;
            }
            _emptyScenes.Remove(scene);
            _jobScene = scene;
            _jobExtra = true;
            _jobPos = 0;
            if (!Call("__atlas_mstage()")) { _jobPos = -1; Enqueue(scene); }
            return true;
        }

        // a loaded scene → begin its build (filter + sort now; entries in budgeted slices)
        if (_readyPoints != null)
        {
            int scene = _readyScene;
            var pts = _readyPoints;
            _readyPoints = null;
            if (scene == _curScene) return false;   // the current scene is Select's
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            BeginBuild(scene, pts, mask);
            Slow("prestage build begin", t0);
            return true;
        }

        // load the next queued scene (worker thread) — not while every chip is off (nothing could be shown; the queue
        // waits for the first chip)
        if (_loading == 0 && _bScene == 0 && _queue.Count > 0 && anyShown)
        {
            int scene = _queue[0];
            _queue.RemoveAt(0);
            _queued.Remove(scene);
            if (scene == _curScene) return false;
            _loading = scene;
            long ver = _index.TryGetValue(scene, out long v) ? v : -1;
            _client.LoadScene(scene, ver, fetchWithoutCache: scene == _priorityScene, res =>
            {
                _loading = 0;
                if (res.State == AtlasClient.FetchState.NoData && res.Points.Length == 0) return;   // never visited, not fetched
                _cachedScenes.Add(res.Scene);
                _readyScene = res.Scene;
                _readyPoints = res.Points;
            });
            return false;
        }
        if (_queue.Count == 0 && _loading == 0 && _stagedCount > 0)
        {
            _services.Log.Info($"[Atlas] prestaged {_stagedCount} scenes ({_stagedFlags} flags); {_staged.Count} scenes staged in total");
            _stagedCount = _stagedFlags = 0;
        }
        return false;
    }

    // Select minus the live part: same candidates (not stale, filter, id range), same hides (this character's opened /
    // not-here for THAT scene, kept in the "all" signature), same order and cap → the same signature Select would get.
    //
    // SLICED (in-game 2026-11-23: "prestage build 12.1 ms" for scene 7, 486 → 300): BeginBuild does the cheap part —
    // filter + sort of ≤ a few thousand longs — and ContinueBuild turns the ordered points into entries within
    // StageBudgetMs per tick (the cost is the names: ServerName asks the game's tables once per new config id, and the
    // opened / not-here check). Own buffers (_b*), so the current scene's Select may run between slices.
    private int _bScene, _bMask, _bPos, _bTake;
    private AtlasPoint[] _bPts = Array.Empty<AtlasPoint>();
    private long[] _bKeys = new long[256];
    private int[] _bIdx = new int[256];
    private readonly List<Entry> _bJob = new(MapCap);
    private ulong _bSig, _bSigAll;

    private void BeginBuild(int scene, AtlasPoint[] points, int mask)
    {
        int n = 0;
        if (_bKeys.Length < points.Length) { _bKeys = new long[points.Length * 2]; _bIdx = new int[points.Length * 2]; }
        for (int i = 0; i < points.Length; i++)
        {
            var p = points[i];
            if (p.Stale || (mask & (1 << (int)p.Chip)) == 0 || p.Id <= 0 || p.Id >= LiveIdBase) continue;
            _bKeys[n] = CapKey(p.Chip, p.Id);
            _bIdx[n] = i;
            n++;
        }
        Array.Sort(_bKeys, _bIdx, 0, n);
        _bScene = scene; _bMask = mask; _bPts = points; _bPos = 0;
        _bTake = Math.Min(n, MapCap);
        LogCap(scene, _bTake, n);
        _bJob.Clear();
        _bSig = _bSigAll = 1469598103934665603UL;
    }

    // Returns true when the build is complete.
    // In-game 2026-11-23 ("prestage build slice 8.8 ms" after the first slicing): the clock was read every 8 points,
    // and each first-time config id costs a Lua table lookup (~1 ms). Now the clock is checked after EVERY point, and a
    // first-time lookup runs only as the slice's first one or while under half the budget — otherwise the slice
    // stops BEFORE that point and the next tick resolves it (names are cached for the session, so this is front-loaded).
    private bool ContinueBuild(long t0)
    {
        int done = 0, lookups = 0;
        while (_bPos < _bTake)
        {
            if (done > 0 && ElapsedMs(t0) >= StageBudgetMs) return false;
            var p = _bPts[_bIdx[_bPos]];
            bool hidden = _openedRef != null && _openedRef.MatchesIn(_bScene, p);
            string name = p.Label;
            if (!hidden && _modelRef != null && !_modelRef.TryServerNameCached(p, out name))
            {
                if (lookups > 0 && ElapsedMs(t0) >= StageBudgetMs * 0.5) return false;   // this point first, next tick
                lookups++;
                name = _modelRef.ServerName(p);
            }
            _bPos++; done++;
            _bSigAll = (_bSigAll ^ (ulong)p.Id) * 1099511628211UL;
            _bSigAll = (_bSigAll ^ (ulong)p.Chip) * 1099511628211UL;
            if (hidden) continue;
            _bJob.Add(new Entry { N = p.Id, Chip = p.Chip, X = p.X, Y = p.HasY ? p.Y : 0f, Z = p.Z,
                                  Name = name.Length > 0 ? name : ChipName(p.Chip) });
            _bSig = (_bSig ^ (ulong)p.Id) * 1099511628211UL;
            _bSig = (_bSig ^ (ulong)p.Chip) * 1099511628211UL;
        }
        _bPts = Array.Empty<AtlasPoint>();   // don't keep the scene's points alive
        return true;
    }

    // PushStep finished a prestaged scene: remember it; rebuild only if it is on screen (browsed on the big map).
    private bool CommittedStaged(int scene)
    {
        _staged[scene] = new StagedScene { Entries = new List<Entry>(_job), Sig = _jobSig, SigAll = _jobSigAll, Mask = _curMask };
        _stagedCount++; _stagedFlags += _job.Count;
        bool onScreen = scene == _viewedScene || scene == _priorityScene;
        if (scene == _viewedScene) CommitIcons(scene, _job, extra: true);
        if (scene == _priorityScene)
        {
            _priorityScene = 0;
            _services.Log.Info($"[Atlas] big map viewing scene {scene}: {_job.Count} points staged");
        }
        return onScreen;
    }

    // The current scene's own push (Select) keeps the staged copy in sync, so leaving and re-entering needs no rebuild.
    private void CommittedCurrent(int scene)
    {
        _staged[scene] = new StagedScene { Entries = new List<Entry>(_job), Sig = _jobSig, SigAll = _jobSigAll, Mask = _curMask };
        _emptyScenes.Remove(scene);
    }

    // FrameTick, right after a rebuild bump: did the big map ask for a scene that isn't the current one?
    private void NoteOtherRequest()
    {
        if (!Lua.TryReadGlobalNumber("__atlas_other", out double v)) return;
        Lua.DoString("rawset(_G,'__atlas_other',nil)");
        int s = (int)v;
        if (s <= 0 || s == _curScene || s >= 10000) return;
        if (s != _viewedScene && _viewedScene != 0 && _viewedScene != _curScene) DropIcons(_viewedScene);
        _viewedScene = s;
        if (_staged.TryGetValue(s, out var st)) { CommitIcons(s, st.Entries, extra: true); return; }   // already drawn by the game
        if (_emptyScenes.ContainsKey(s) || (_curMask & AtlasGroups.ShownBits) == 0) return;
        _priorityScene = s;   // never visited → fetch now, stage, dispatch once
        Enqueue(s, front: true);
    }

    // Big map closed: stop re-skinning the browsed scene (its data stays staged for the next look).
    private void BigMapClosed()
    {
        if (_viewedScene != 0 && _viewedScene != _curScene) DropIcons(_viewedScene);
        _viewedScene = 0;
    }

    // Scene change: adopt the staged set (→ no dispatch) or fall back to fetch + one dispatch.
    private void AdoptScene(int scene, float now)
    {
        _sceneChangeAt = now;
        _indexDue = true;
        _curSettled = false; _settleAt = now; _settleBy = now + SettleTimeout;
        if (_staged.TryGetValue(scene, out var st) && st.Mask == _curMask)
        {
            _lastSig = st.Sig; _lastSigAll = st.SigAll;
            CommitIcons(scene, st.Entries, extra: false);
            _services.Log.Info($"[Atlas] scene change {scene}: flags ready (staged, {st.Entries.Count})");
            Settle(now, "staged");   // already on the game's maps → nothing to wait for
        }
        else if (_emptyScenes.TryGetValue(scene, out int m) && m == _curMask)
        {
            _lastSig = _lastSigAll = 0;
            _services.Log.Info($"[Atlas] scene change {scene}: nothing to show (staged empty)");
        }
        else
        {
            _lastSig = _lastSigAll = 0;
            _services.Log.Info($"[Atlas] scene change {scene}: not staged → fetch");
        }
    }

    // The current scene's flags are pushed and dispatched (or there was nothing to push): prestaging may run again.
    private void Settle(float now, string why)
    {
        if (_curSettled) return;
        _curSettled = true;
        _services.Log.Info($"[Atlas] scene {_curScene} settled ({why}) after {(int)((now - _settleAt) * 1000f)} ms → prestaging resumes");
    }

    // Icon pass: the first current-scene unit re-skinned after a scene change → how long the markers took.
    private void NoteShown(float now)
    {
        if (_sceneChangeAt < 0f) return;
        _services.Log.Info($"[Atlas] scene {_curScene} markers shown after {(int)((now - _sceneChangeAt) * 1000f)} ms");
        _sceneChangeAt = -1f;
    }

    private void ResetPrestage()
    {
        _staged.Clear(); _emptyScenes.Clear(); _queue.Clear(); _queued.Clear();
        _loading = _readyScene = _priorityScene = _viewedScene = _bScene = 0;
        _bPts = Array.Empty<AtlasPoint>();
        _readyPoints = null; _stagedMask = -1; _indexDue = true; _sceneChangeAt = -1f;
        _curSettled = true;
    }
}
