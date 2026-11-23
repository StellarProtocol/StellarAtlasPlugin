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
// Data flow: C# picks the CURRENT shown set (SHOW chips, no opened chests, no stale points) for the scene, capped to the
// MapCap nearest to the player (the wrapper serves big map AND minimap and can't tell them apart, so the cap is total),
// and pushes it into a Lua staging table in bounded batches (BatchSize per call, BatchesPerTick per 5 Hz tick), then
// commits it as _G.__atlas_map[sceneId] and dispatches MapFlagShowListChanged so both maps rebuild. A push happens only
// when the set's signature changed (ids + chips), at most every MinPushGap seconds. Bridge chunks don't share plain
// globals → everything lives under rawset(_G, …). Every helper is compiled once (Plugin-Performance-Rules.md §4).
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
    private const float MinPushGap     = 3f;
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
    private float[] _keys = new float[256];
    private int[] _idx = new int[256];
    private readonly List<object> _cands = new(256);
    private readonly Dictionary<long, long> _liveIds = new();   // uuid → n, session-stable
    private long _liveNext;

    // the job being pushed / the set last committed
    private readonly List<Entry> _job = new(MapCap);
    private int _jobPos = -1, _jobScene;
    private bool _dispatchPending, _installedThisTick;
    private ulong _jobSig, _lastSig;
    private float _lastPushAt = -100f;
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
        _jobPos = -1; _lastSig = 0; _dispatchPending = false;
        ResetIcons();
    }

    // Scene change: drop what is shown; the next Sync pushes the new scene once its points arrive.
    public void OnScene()
    {
        _jobPos = -1; _lastSig = 0; _dispatchPending = false;
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
    public void Tick(int scene, float px, float py, float pz, int mask, NearestModel model, Dictionary<long, LiveEnt> live,
        OpenedStore opened, float now)
    {
        if (scene <= 0) return;
        // Hitch fix (2026-11-23, [Hitch] stellar.atlas=137.5 ms once after the first push): every Lua step gets its own
        // tick — define/install, each batch, the commit, and the map refresh dispatch — and each is timed (Slow()).
        _installedThisTick = false;
        if (_dispatchPending) { Dispatch(); return; }
        if (_jobPos >= 0) { PushStep(); return; }
        if (!EnsureInstalled(now) || _installedThisTick) return;   // install (prewarm) on its own tick, ahead of any push
        if (now - _lastPushAt < MinPushGap) return;
        // Re-selected every MinPushGap (one pass over the scene's points + a sort of ≤ a few thousand floats ≈ sub-ms);
        // pushed only when the nearest-MapCap set (ids + chips) actually changed — e.g. the player walked far enough.
        _lastPushAt = now;
        Select(scene, px, py, pz, mask, model, live, opened);
        if (_jobSig == _lastSig) { _job.Clear(); return; }
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
        CommitIcons(_job);   // MapMarkers.Icons.cs: the unit names + chips the icon pass looks for
        _jobPos = -1;
    }

    // The shown set: server points (not stale / opened) + live entities the server doesn't know yet, filtered by the
    // SHOW chips, nearest MapCap. Fills _job and its signature.
    private void Select(int scene, float px, float py, float pz, int mask, NearestModel model, Dictionary<long, LiveEnt> live,
        OpenedStore opened)
    {
        model.RefreshHidden(opened);
        _cands.Clear();
        foreach (var p in model.Points)
            if (!p.Hidden && (mask & (1 << (int)p.Chip)) != 0) _cands.Add(p);
        foreach (var e in live.Values)
            if (e.Tracked && e.HasPos && (mask & (1 << (int)e.Chip)) != 0 && !opened.HasUuid(e.Uuid) && model.IsLiveOnly(e)) _cands.Add(e);

        int n = _cands.Count;
        if (_keys.Length < n) { _keys = new float[n * 2]; _idx = new int[n * 2]; }
        for (int i = 0; i < n; i++)
        {
            float x, z;
            if (_cands[i] is AtlasPoint ap) { x = ap.X; z = ap.Z; } else { var le = (LiveEnt)_cands[i]; x = le.X; z = le.Z; }
            _keys[i] = Sq(x - px) + Sq(z - pz);
            _idx[i] = i;
        }
        Array.Sort(_keys, _idx, 0, n);

        _job.Clear();
        ulong sig = 1469598103934665603UL;
        int take = Math.Min(n, MapCap);
        for (int k = 0; k < take; k++)
        {
            var c = _cands[_idx[k]];
            Entry en;
            if (c is AtlasPoint p)
            {
                string name = model.ServerName(p);
                en = new Entry { N = p.Id, Chip = p.Chip, X = p.X, Y = p.HasY ? p.Y : py, Z = p.Z, Name = name.Length > 0 ? name : ChipName(p.Chip) };
            }
            else
            {
                var e = (LiveEnt)c;
                if (!_liveIds.TryGetValue(e.Uuid, out long id)) { id = LiveIdBase + ++_liveNext; _liveIds[e.Uuid] = id; }
                string name = model.LiveName(e);
                en = new Entry { N = id, Chip = e.Chip, X = e.X, Y = e.Y, Z = e.Z, Name = name.Length > 0 ? name : ChipName(e.Chip) };
            }
            if (en.N <= 0 || en.N >= MaxN) continue;   // keeps Id at 14 digits and Uid (900000000 + n) inside int32
            _job.Add(en);
            sig = (sig ^ (ulong)en.N) * 1099511628211UL;
            sig = (sig ^ (ulong)en.Chip) * 1099511628211UL;
        }
        _jobSig = sig ^ (ulong)scene;
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
    private static float Sq(float v) => v * v;

    private void WarnOnce(ref bool flag, string msg) { if (flag) return; flag = true; _services.Log.Warning("[Atlas] " + msg); }
}
