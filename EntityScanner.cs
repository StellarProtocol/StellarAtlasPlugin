using System;
using System.Collections.Generic;
using Stellar.Abstractions.Services;
using UnityEngine;

namespace Stellar.Atlas;

// One live collection entity (EntCollection, type 16) the game currently streams to this character.
internal sealed class LiveEnt
{
    public long Uuid;
    public int Cfg;
    public bool Tracked;                 // classified as something Atlas shows / reports
    public bool Classified;              // config id + table row resolved (Tracked is final)
    public string Group = "", Subtype = "";
    public Chip Chip;
    public float X, Y, Z;
    public bool HasPos;
    public int ObjState = int.MinValue;
    public bool Flipped;                 // AttrObjState went 0 → non-zero while seen (chest being opened)
    public float Dist = -1f;             // 3D distance to the player at the last poll (-1 = unknown)
    public int Seq;
    public int RetryAt;                  // poll seq at which an unresolved config id is retried
}

// Live nearby-collection walk at 2 Hz (world-ready only — the plugin gates it). Port of the in-game-validated Experiment
// Chest Probe walk: one bound entityDict_ key walk per poll; per collection one GetEntity + bound pos / objState reads.
// Config id + table row resolve (Invoke + Lua) only on FIRST sight of a uuid, cached. No allocation per poll beyond a
// LiveEnt per NEWLY seen entity (an event).
//
// Opened detection (validated 2026-11-23): opening a chest flips AttrObjState 0→1, then the server stops sending that
// chest to this character (it disappears at ~0.5 m and never comes back, any line, after relog). So "objState flipped
// AND it vanished while the player stood within OpenedRadius" = opened by this character.
internal sealed partial class EntityScanner
{
    private const int   EntCollection = 16;     // EEntityType.EntCollection — (uuid >> 6) & 31, NEVER uuid & 0xFFFF
    public  const float OpenedRadius  = 3f;

    private readonly IPluginServices _services;
    private readonly GameLua _lua;
    private readonly Dictionary<long, LiveEnt> _live = new();
    private readonly Dictionary<long, int> _uuidCfg = new();   // uuid → config id, kept across disappear
    private readonly List<long> _gone = new();                 // reused per poll
    private int _seq;

    public Vector3 PlayerPos;
    public bool HasPlayerPos;

    // Consecutive complete walks since the last reset (scene change / logout / a failed poll). "Healthy" = the walk is
    // bound, the player position reads, and the live set has had >= 2 s to stream in: the absent-chest heuristic
    // (Plugin.Absent.cs) must never conclude "no chest here" from an empty or broken walk.
    public int GoodPolls { get; private set; }
    public bool Healthy => GoodPolls >= 4 && HasPlayerPos;

    // Raised synchronously inside Poll (main thread). O(1) handlers only.
    public Action<LiveEnt>? Appeared;   // a NEW tracked entity with a position
    public Action<LiveEnt>? Opened;     // a tracked chest this character just opened

    public EntityScanner(IPluginServices services, GameLua lua) { _services = services; _lua = lua; }

    public Dictionary<long, LiveEnt> Live => _live;

    // Scene change / logout: every entity leaves at once — not "opened", not AOI.
    public void Reset()
    {
        _live.Clear();
        HasPlayerPos = false;
        GoodPolls = 0;
    }

    // Player position only (5 Hz path — guide arrival + list distances between walks).
    public bool ReadPlayer()
    {
        if (!EnsureApi()) return false;
        try
        {
            var mgr = _getMgr!();
            if (mgr == null) return false;
            long localUuid = 0;
            try { localUuid = _services.CombatSnapshot.LocalEntityId.Value; } catch { }
            var ent = localUuid != 0 ? _getEntity!(mgr, localUuid) : null;
            HasPlayerPos = ent != null && TryPos(ent, out PlayerPos);
            return HasPlayerPos;
        }
        catch { HasPlayerPos = false; return false; }
    }

    // 2 Hz walk.
    public void Poll()
    {
        if (!EnsureApi()) { GoodPolls = 0; return; }
        try
        {
            var mgr = _getMgr!();
            if (mgr == null) { GoodPolls = 0; return; }
            var dict = _getDict != null ? _getDict(mgr) : _piEntityDict!.GetValue(mgr);
            if (dict == null) { GoodPolls = 0; return; }
            if (!_walkResolved) ResolveKeyWalk(dict);
            if (_enumCurFn == null) { GoodPolls = 0; return; }
            ReadPlayer();

            _seq++;
            var en = _keysEnumFn!(_dictKeysFn!(dict)!);
            while (en != null && _enumMoveFn!(en))
            {
                long uuid = _enumCurFn(en);
                if (((uuid >> 6) & 31) != EntCollection) continue;
                if (_live.TryGetValue(uuid, out var e))
                {
                    e.Seq = _seq;
                    if (!e.Tracked && (e.Classified || _seq < e.RetryAt)) continue;   // untracked + resolved: nothing to read
                    var ent = _getEntity!(mgr, uuid);
                    if (ent == null) continue;
                    if (!e.Tracked) { if (Classify(e)) { Read(e, ent); if (e.HasPos) Appeared?.Invoke(e); } continue; }
                    bool hadPos = e.HasPos;
                    Read(e, ent);
                    if (!hadPos && e.HasPos) Appeared?.Invoke(e);
                }
                else
                {
                    var ent = _getEntity!(mgr, uuid);
                    if (ent == null) continue;
                    e = new LiveEnt { Uuid = uuid, Seq = _seq };
                    _live[uuid] = e;
                    if (!Classify(e)) continue;
                    Read(e, ent);
                    if (e.HasPos) Appeared?.Invoke(e);
                }
            }

            foreach (var kv in _live) if (kv.Value.Seq != _seq) _gone.Add(kv.Key);
            foreach (var u in _gone)
            {
                var e = _live[u];
                _live.Remove(u);
                if (!e.Tracked) continue;
                if (e.Flipped && AtlasGroups.IsChest(e.Chip) && e.Dist >= 0f && e.Dist <= OpenedRadius) Opened?.Invoke(e);
            }
            _gone.Clear();
            GoodPolls++;
        }
        catch (Exception ex) { GoodPolls = 0; WarnOnce(ref _pollErrLogged, "poll error: " + (ex.InnerException?.Message ?? ex.Message)); }
    }
    private bool _pollErrLogged;

    // Config id → classification. An id that doesn't resolve yet (entity still initialising / Lua not ready) is retried
    // every ~5 s (10 polls) — the Invoke allocates, so never every poll.
    private bool Classify(LiveEnt e)
    {
        if (e.Cfg <= 0)
        {
            if (!_uuidCfg.TryGetValue(e.Uuid, out e.Cfg)) { e.Cfg = ResolveConfigId(e.Uuid); if (e.Cfg > 0) _uuidCfg[e.Uuid] = e.Cfg; }
            if (e.Cfg <= 0) { e.RetryAt = _seq + 10; return false; }
        }
        var info = _lua.Cfg(e.Cfg);
        if (!info.Known) { e.RetryAt = _seq + 10; return false; }   // Lua not ready → retry the table row later
        e.Classified = true;
        e.Tracked = AtlasGroups.ClassifyLive(e.Cfg, info.Type, info.LifePro, out e.Group, out e.Subtype, out e.Chip);
        return e.Tracked;
    }

    private void Read(LiveEnt e, object ent)
    {
        if (TryPos(ent, out var p))
        {
            e.X = p.x; e.Y = p.y; e.Z = p.z; e.HasPos = true;
        }
        if (e.HasPos && HasPlayerPos) e.Dist = Vector3.Distance(new Vector3(e.X, e.Y, e.Z), PlayerPos);
        if (AtlasGroups.IsChest(e.Chip))
        {
            int obj = ReadObjState(ent);
            if (obj != int.MinValue)
            {
                if (e.ObjState == 0 && obj != 0) e.Flipped = true;
                e.ObjState = obj;
            }
        }
    }
}
