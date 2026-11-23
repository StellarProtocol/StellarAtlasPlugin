using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Stellar.Abstractions.Services;

namespace Stellar.Atlas;

// Atlas points as markers on the game's OWN big map (M) and minimap (option C, user-approved 2026-11-23). Pure Lua,
// client-only — the recipe validated in-game (Auto-Gather-and-Interaction.md "Custom markers on the game's OWN big
// map"): wrap MapVM.GetMonsterHuntFlagDataBySceneId (its only caller is map_flags_comp RefreshMap, shared by BOTH maps)
// and append one Position flag per point (TypeId 303 = the Position Sharing scene tag; Id unique; Layer = the arg). The
// game then draws them, lists them in its nearby pick list and Info panel (Name) with its own Navigate / Track buttons.
//
// Data flow: C# picks the shown set for the scene and pushes it into a Lua staging table in bounded batches (BatchSize
// per call, one call per 5 Hz tick), commits it as _G.__atlas_map[sceneId] and dispatches MapFlagShowListChanged so both
// maps rebuild. Bridge chunks don't share plain globals → everything lives under rawset(_G, …). Every helper is compiled
// once (Plugin-Performance-Rules.md §4).
//
// ⚠️ A dispatch makes the game rebuild ALL flags of BOTH maps: ~80–114 ms on the main thread with 100 of ours, and
// every unit comes back with the placeholder sprite until our pass re-skins it (in-game 2026-11-23: a nearest-300 set
// that followed the player re-dispatched every few metres → minimap icons blinked + repeated 100 ms hitches). So the set
// must NOT depend on where the player stands: it is every point passing the SHOW filters (minus opened / not-here /
// stale) in a FIXED order — chests first, then by group, then by point id — capped at MapCap. Live entities the server
// doesn't know are remembered for the scene once seen (they don't drop out when they leave AOI). The set changes only
// for reasons the player expects: new scene / new server data, a chip toggled, a chest opened or hidden, a new
// live-only find. Changes are coalesced: one dispatch at most every DispatchGap seconds, never while a push is running.
//
// Hides don't rebuild (2026-11-23: each "not here" hide cost an 84–107 ms rebuild, and a map where the character opened
// many chests long ago hides several while walking). Two signatures: _jobSigAll ignores opened / not-here hides,
// _jobSig doesn't. When only the hides changed, the hidden entries are made transparent in place (MapMarkers.Icons.cs
// SuppressIcons) and folded into the next push that happens anyway; a hide-only push is allowed at most every
// HideDispatchGap seconds.
//
// Ids: Id = 90000000000000 + n (14 digits; the KB recipe used 9e14 = 15 digits). The unit is named
// "positionFlagName" .. data.Id by Lua string concat (map_flags_comp.lua:398), and Lua 5.1/LuaJIT formats numbers with
// %.14g — a 15-digit id would come out as "9e+14" for EVERY point. 14 digits print exactly, so C# can rebuild the unit
// name for the icon pass. n = the server point id (stable), or 600000000 + a per-session counter for a live-only entity.
// Uid = 900000000 + n (KB recipe).
internal sealed partial class MapMarkers
{
    private const int   MapCap         = 300;
    private const int   BatchSize      = 25;
    private const int   BatchesPerTick = 1;
    private const float SelectGap      = 2f;     // re-check the set (cheap, no Lua) this often
    private const float DispatchGap    = 5f;     // at most one map rebuild per 5 s
    private const float HideDispatchGap = 60f;   // …and one per minute when only hides changed
    private const long  IdBase         = 90000000000000L;
    private const long  LiveIdBase     = 600000000L;   // server point ids are assumed < 600 000 000
    private const long  MaxN           = 1000000000L;

    private const string Defs =
        "rawset(_G,'__atlas_minstall',function()" +
        " rawset(_G,'__at_mok',nil)" +
        " local vm = Z.VMMgr.GetVM('map')" +
        " if vm == nil then return end" +
        " if rawget(vm,'__orig_MH') == nil then" +
        "  local orig = vm.GetMonsterHuntFlagDataBySceneId" +
        "  if orig == nil then return end" +
        "  rawset(vm,'__orig_MH_raw', rawget(vm,'GetMonsterHuntFlagDataBySceneId') ~= nil)" +
        "  rawset(vm,'__orig_MH', orig)" +
        "  vm.GetMonsterHuntFlagDataBySceneId = function(sceneId, layer)" +
        "   local list = (rawget(vm,'__orig_MH'))(sceneId, layer) or {}" +
        "   pcall(function()" +
        "    rawset(_G,'__atlas_mapgen', (rawget(_G,'__atlas_mapgen') or 0) + 1)" +
        "    local m = rawget(_G,'__atlas_map')" +
        "    local pts = m and m[sceneId]" +
        "    if pts == nil then return end" +
        "    for i = 1, #pts do local p = pts[i]" +
        "     list[#list + 1] = {Id = 90000000000000 + p[1], Uid = 900000000 + p[1], Type = -1, FlagType = E.MapFlagType.Position," +
        "      TypeId = 303, Layer = layer, Pos = Vector3.New(p[2], p[3], p[4]), IconPath = 'ui/atlas/npc/talk_icon_box', Name = p[5]}" +
        "    end" +
        "   end)" +
        "   return list" +
        "  end" +
        " end" +
        " rawset(_G,'__at_mok',true)" +
        " end)" +
        " rawset(_G,'__atlas_mstage',function() rawset(_G,'__atlas_stage',{}) end)" +
        " rawset(_G,'__atlas_mpush',function(...)" +
        " local a = rawget(_G,'__atlas_stage') if a == nil then return end" +
        " for i = 1, select('#', ...) do a[#a + 1] = (select(i, ...)) end" +
        " end)" +
        " rawset(_G,'__atlas_mcommit',function(sid)" +
        " local m = {} if sid ~= nil then m[sid] = rawget(_G,'__atlas_stage') or {} end" +
        " rawset(_G,'__atlas_map', m) rawset(_G,'__atlas_stage', nil)" +
        " end)" +
        " rawset(_G,'__atlas_mdispatch',function()" +
        " pcall(function() Z.EventMgr:Dispatch(Z.ConstValue.MapFlagShowListChanged) end)" +
        " end)" +
        " rawset(_G,'__atlas_mrestore',function()" +
        " pcall(function()" +
        "  local vm = Z.VMMgr.GetVM('map') local o = rawget(vm,'__orig_MH')" +
        "  if o ~= nil then" +
        "   if rawget(vm,'__orig_MH_raw') then vm.GetMonsterHuntFlagDataBySceneId = o else rawset(vm,'GetMonsterHuntFlagDataBySceneId', nil) end" +
        "   rawset(vm,'__orig_MH', nil)" +
        "  end" +
        " end)" +
        " rawset(_G,'__atlas_map', nil)" +
        " pcall(function() Z.EventMgr:Dispatch(Z.ConstValue.MapFlagShowListChanged) end)" +
        " end)";

    private struct Entry
    {
        public long N;
        public Chip Chip;
        public float X, Y, Z;
        public string Name;
    }

    private readonly IPluginServices _services;
    private readonly ILocalization _loc;
    private bool _defined, _installed;

    // selection scratch (grown on demand, reused — no allocation per sync once warm)
    private long[] _keys = new long[256];
    private int[] _idx = new int[256];
    private readonly List<object> _cands = new(256);
    private readonly Dictionary<long, long> _liveIds = new();   // uuid → n, session-stable
    private readonly Dictionary<long, Entry> _sticky = new();   // live-only finds of this scene (uuid → entry)
    private long _liveNext;

    // the job being pushed / the set last committed
    private readonly List<Entry> _job = new(MapCap);
    private int _jobPos = -1, _jobScene;
    private bool _dispatchPending, _installedThisTick;
    private float _nowForDispatch;
    private ulong _jobSig, _lastSig, _jobSigAll, _lastSigAll;
    private readonly List<long> _jobHidden = new();   // Ns in the full set that are hidden by opened / not-here
    private float _lastSelectAt = -100f, _lastDispatchAt = -100f;
    private readonly StringBuilder _sb = new(4096);

    public MapMarkers(IPluginServices services)
    {
        _services = services;
        _loc = services.Localization;
    }

    private ILua Lua => _services.Lua;

    // Lua sentinel call: defines the helpers on first use / after a Lua reset (sentinel nil) and retries once.
    private bool Call(string chunk)
    {
        if (!Lua.Ready) return false;
        if (!_defined) { Lua.DoString(Defs); _defined = true; }
        string guarded = "rawset(_G,'__atlas_has',nil) if rawget(_G,'__atlas_mpush') then rawset(_G,'__atlas_has',true) " + chunk + " end";
        Lua.DoString(guarded);
        if (Lua.TryReadGlobalBool("__atlas_has", out var has) && has) return true;
        Lua.DoString(Defs);
        _installed = false;
        Lua.DoString(guarded);
        return Lua.TryReadGlobalBool("__atlas_has", out has) && has;
    }

    private bool EnsureInstalled(float now)
    {
        if (_installed) return true;
        if (now < _installRetryAt) return false;   // map VM not there yet → ask again in 5 s, not every tick
        _installRetryAt = now + 5f;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        if (!Call("__atlas_minstall()")) return false;
        Slow("install", t0);
        _installedThisTick = true;
        _installed = Lua.TryReadGlobalBool("__at_mok", out var ok) && ok;
        if (!_installed) WarnOnce(ref _installWarned, "map VM not ready — markers wait");
        else _services.Log.Info("[Atlas] map markers installed");
        return _installed;
    }
    private bool _installWarned;
    private float _installRetryAt;

    // Logout: the next login may bring a fresh Lua state.
    public void OnLogout()
    {
        _defined = _installed = false;
        _installRetryAt = 0f;
        _sticky.Clear();
        _jobPos = -1; _lastSig = _lastSigAll = 0; _dispatchPending = false;
        ResetIcons();
    }

    // Scene change: drop what is shown; the next Sync pushes the new scene once its points arrive.
    public void OnScene()
    {
        _sticky.Clear();
        _jobPos = -1; _lastSig = _lastSigAll = 0; _dispatchPending = false;
        ResetIcons();
    }

    // Dispose: give the game its function back and remove our flags from both maps.
    public void Restore()
    {
        try { if (Lua.Ready && _defined) Lua.DoString("if rawget(_G,'__atlas_mrestore') then __atlas_mrestore() end"); } catch { }
        _installed = false;
        DestroySprites();
    }

    // 5 Hz (world-ready). Advances a push in progress, or starts one when the shown set changed.
    // `dataReady` = the scene's server points have arrived (or failed): selecting before that would push the live-only
    // finds alone and then rebuild the maps a second time when the points land.
    public void Tick(int scene, bool dataReady, float py, int mask, NearestModel model, Dictionary<long, LiveEnt> live,
        OpenedStore opened, float now)
    {
        if (scene <= 0) return;
        // Hitch fix (2026-11-23, [Hitch] stellar.atlas=137.5 ms once after the first push): every Lua step gets its own
        // tick — define/install, each batch, the commit, and the map refresh dispatch — and each is timed (Slow()).
        _installedThisTick = false;
        _nowForDispatch = now;
        if (_dispatchPending) { Dispatch(); return; }
        if (_jobPos >= 0) { PushStep(); return; }
        if (!EnsureInstalled(now) || _installedThisTick) return;   // install (prewarm) on its own tick, ahead of any push
        if (!dataReady || now - _lastSelectAt < SelectGap) return;
        // Re-selected every SelectGap (one pass over the scene's points + a sort of a few thousand longs, sub-ms, no
        // Lua); pushed only when the set (ids + chips) changed AND the last map rebuild is ≥ DispatchGap ago — changes
        // inside the gap are picked up by a later select (coalesced).
        _lastSelectAt = now;
        if (now - _lastDispatchAt < DispatchGap) return;
        Select(scene, py, mask, model, live, opened);
        if (_jobSig == _lastSig) { _job.Clear(); return; }
        if (_jobSigAll == _lastSigAll)
        {
            // only hides changed → hide those units in place; rebuild only once a minute for this alone
            SuppressIcons(_jobHidden);
            if (now - _lastDispatchAt < HideDispatchGap) { _job.Clear(); return; }
        }
        _jobScene = scene;
        _jobPos = 0;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        if (!Call("__atlas_mstage()")) { _jobPos = -1; return; }
        Slow("select + stage", t0);
    }

    // The game rebuilds BOTH maps' flag lists synchronously inside this Dispatch (RefreshMap → our wrapper → ~MapCap
    // tables); that cost is the game's, but it lands in our call, so it runs alone on its own tick.
    private void Dispatch()
    {
        _dispatchPending = false;
        _lastDispatchAt = _lastSelectAt = _nowForDispatch;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        Call("__atlas_mdispatch()");
        Slow("map refresh dispatch (" + _paths.Length.ToString(CultureInfo.InvariantCulture) + " flags)", t0);
    }

    private void PushStep()
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        for (int b = 0; b < BatchesPerTick && _jobPos < _job.Count; b++)
        {
            var sb = _sb.Clear().Append("__atlas_mpush(");
            int end = Math.Min(_job.Count, _jobPos + BatchSize);
            for (int i = _jobPos; i < end; i++)
            {
                var e = _job[i];
                if (i > _jobPos) sb.Append(',');
                sb.Append('{').Append(e.N.ToString(CultureInfo.InvariantCulture)).Append(',').Append(F(e.X)).Append(',')
                  .Append(F(e.Y)).Append(',').Append(F(e.Z)).Append(',');
                AppendLuaString(sb, e.Name);
                sb.Append('}');
            }
            sb.Append(')');
            if (!Call(sb.ToString())) { _jobPos = -1; return; }
            _jobPos = end;
        }
        Slow("push batch", t0);
        if (_jobPos < _job.Count) return;
        Call("__atlas_mcommit(" + _jobScene.ToString(CultureInfo.InvariantCulture) + ")");
        _dispatchPending = true;
        _lastSig = _jobSig;
        _lastSigAll = _jobSigAll;
        CommitIcons(_job);   // MapMarkers.Icons.cs: the unit names + chips the icon pass looks for
        _jobPos = -1;
    }

    // The shown set: server points (not stale / opened / not-here) + live-only finds of this scene, filtered by the SHOW
    // chips, in a fixed order (chip, then id) capped at MapCap. Independent of the player's position; `py` only fills the
    // height of points that have none (it doesn't enter the signature). Fills _job and its signature.
    private void Select(int scene, float py, int mask, NearestModel model, Dictionary<long, LiveEnt> live, OpenedStore opened)
    {
        model.RefreshHidden(opened);
        // remember live-only finds for the scene (an AOI exit must not change the set)
        foreach (var e in live.Values)
        {
            if (!e.Tracked || !e.HasPos || _sticky.ContainsKey(e.Uuid) || !model.IsLiveOnly(e)) continue;
            if (!_liveIds.TryGetValue(e.Uuid, out long id)) { id = LiveIdBase + ++_liveNext; _liveIds[e.Uuid] = id; }
            string name = model.LiveName(e);
            _sticky[e.Uuid] = new Entry { N = id, Chip = e.Chip, X = e.X, Y = e.Y, Z = e.Z, Name = name.Length > 0 ? name : ChipName(e.Chip) };
        }

        _cands.Clear();
        // Stale points are out; opened / not-here ones stay in the candidate list (for the "all" signature) and are
        // skipped when the job is built.
        foreach (var p in model.Points)
            if (!p.Stale && (mask & (1 << (int)p.Chip)) != 0 && p.Id > 0 && p.Id < LiveIdBase) _cands.Add(p);
        foreach (var kv in _sticky)
        {
            var en = kv.Value;
            if ((mask & (1 << (int)en.Chip)) != 0 && model.IsLiveOnly(kv.Key, en.Chip, en.X, en.Z))
                _cands.Add(kv);   // boxed KeyValuePair: only live-only finds, a handful per scene
        }

        int n = _cands.Count;
        if (_keys.Length < n) { _keys = new long[n * 2]; _idx = new int[n * 2]; }
        for (int i = 0; i < n; i++)
        {
            Chip chip; long id;
            if (_cands[i] is AtlasPoint ap) { chip = ap.Chip; id = ap.Id; }
            else { var en = ((KeyValuePair<long, Entry>)_cands[i]).Value; chip = en.Chip; id = en.N; }
            _keys[i] = ((long)chip << 40) | id;   // chests (enum 0-3) first, then the gathering groups; then id
            _idx[i] = i;
        }
        Array.Sort(_keys, _idx, 0, n);

        _job.Clear();
        _jobHidden.Clear();
        ulong sig = 1469598103934665603UL, sigAll = sig;
        int take = Math.Min(n, MapCap);
        for (int k = 0; k < take; k++)
        {
            var c = _cands[_idx[k]];
            long nId; Chip chip; bool hidden;
            if (c is AtlasPoint p) { nId = p.Id; chip = p.Chip; hidden = p.Hidden; }
            else { var kv = (KeyValuePair<long, Entry>)c; nId = kv.Value.N; chip = kv.Value.Chip; hidden = opened.HasUuid(kv.Key); }
            if (nId <= 0 || nId >= MaxN) continue;   // keeps Id at 14 digits and Uid (900000000 + n) inside int32
            sigAll = (sigAll ^ (ulong)nId) * 1099511628211UL;
            sigAll = (sigAll ^ (ulong)chip) * 1099511628211UL;
            if (hidden) { _jobHidden.Add(nId); continue; }
            Entry en;
            if (c is AtlasPoint sp)
            {
                string name = model.ServerName(sp);
                en = new Entry { N = sp.Id, Chip = sp.Chip, X = sp.X, Y = sp.HasY ? sp.Y : py, Z = sp.Z, Name = name.Length > 0 ? name : ChipName(sp.Chip) };
            }
            else en = ((KeyValuePair<long, Entry>)c).Value;
            _job.Add(en);
            sig = (sig ^ (ulong)en.N) * 1099511628211UL;
            sig = (sig ^ (ulong)en.Chip) * 1099511628211UL;
        }
        _jobSig = sig ^ (ulong)scene;
        _jobSigAll = sigAll ^ (ulong)scene;
    }

    private string ChipName(Chip c) => _loc.T(AtlasGroups.LabelKey(c));

    // Lua string literal: quotes, backslashes and control characters escaped (names come from game tables / the server).
    private static void AppendLuaString(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (char ch in s)
        {
            if (ch == '"' || ch == '\\') sb.Append('\\').Append(ch);
            else if (ch < 0x20) sb.Append('\\').Append(((int)ch).ToString("D3", CultureInfo.InvariantCulture));
            else sb.Append(ch);
        }
        sb.Append('"');
    }

    private static string F(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    private void WarnOnce(ref bool flag, string msg) { if (flag) return; flag = true; _services.Log.Warning("[Atlas] " + msg); }
}
