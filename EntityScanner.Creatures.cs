using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using Stellar.Abstractions.Services;
using UnityEngine;

namespace Stellar.Atlas;

// One magical creature the game currently streams to this character (Creatures.cs).
internal sealed class LiveCreature
{
    public long Uuid;
    public int Idx;                      // Creatures index (chip order)
    public float X, Y, Z;
    public bool HasPos;
    public long Hp, MaxHp;               // -1 = not read
    public float Dist = -1f;             // 3D distance to the player at the last poll
    public int Seq;
    public bool Dead => MaxHp > 0 && Hp <= 0;
    public int HpPct => MaxHp > 0 ? (int)Math.Clamp(Hp * 100 / MaxHp, 0, 100) : -1;
}

// Creatures piggyback on the 2 Hz collection walk (EntityScanner.cs Poll): the SAME entityDict_ key walk, no second pass.
// Only in Asteria Plains (CreaturesOn, set by the plugin per scene). A monster uuid ((uuid >> 6) & 31 == EntMonster) costs
// one dictionary lookup per poll; its BaseId is read ONCE on first sight (bound getter) and cached, so the ~100 ordinary
// monsters of the scene cost a lookup each. Only the five creature ids get the per-poll reads (bound position + two HP
// attrs through one compiled delegate — no Invoke / boxing, Plugin-Performance-Rules.md §2-3).
//
// Facts used (research 2026-11-24, NOT yet validated in-game — each logs one line the first time it is read):
//   ZEntity.BaseId (int)                              — monster config id (10900..10904)
//   ZEntity.GetAttr<long>(EAttrType, bool checkInherit) — AttrHp = 11310, AttrMaxHp = 11320 (checkInherit passed: true)
//   dead = MaxHp > 0 && Hp <= 0, or the uuid leaves entityDict_ (AttrDeadType is stale — don't use it)
internal sealed partial class EntityScanner
{
    private const int EntMonster  = 1;
    private const int AttrHp      = 11310;
    private const int AttrMaxHp   = 11320;
    private const int MonPruneEvery = 120;   // polls (~1 min): drop remembered non-creature monster uuids not seen since

    private readonly Dictionary<long, LiveCreature> _creatures = new();
    private readonly Dictionary<long, int> _monIdx = new();      // monster uuid → creature index or -1 (BaseId read once)
    private readonly Dictionary<long, int> _monSeen = new();     // monster uuid → last poll seq it was in the walk
    private readonly List<long> _creGone = new();
    private Func<object, int>? _getBaseId;
    private Func<object, int, long>? _getAttrLong;
    private bool _creApiResolved, _creLoggedBase, _creLoggedHp;

    public bool CreaturesOn;                                       // the plugin sets it while in scene 7
    public Dictionary<long, LiveCreature> LiveCreatures => _creatures;
    public Action<LiveCreature, bool>? CreatureLeft;              // (creature, killed) — raised inside Poll, O(1) handlers

    // Walk hook (called for every monster key of the walk when CreaturesOn).
    private void PollMonster(object mgr, long uuid)
    {
        _monSeen[uuid] = _seq;
        if (_monIdx.TryGetValue(uuid, out int idx) && idx < 0) return;   // an ordinary monster: one lookup, done
        var ent = _getEntity!(mgr, uuid);
        if (ent == null) return;
        if (!_monIdx.ContainsKey(uuid))
        {
            if (!EnsureCreatureApi()) { _monIdx[uuid] = -1; return; }
            int baseId = 0;
            try { baseId = _getBaseId!(ent); } catch { }
            if (baseId == 0) return;   // still initialising → asked again next poll
            idx = Creatures.Index(baseId);
            _monIdx[uuid] = idx;
            if (idx < 0) return;
            if (!_creLoggedBase)
            {
                _creLoggedBase = true;
                _services.Log.Info($"[Atlas] creature: ZEntity.BaseId read {baseId} for uuid {uuid} (type {(uuid >> 6) & 31})");
            }
        }
        if (!_creatures.TryGetValue(uuid, out var c)) { c = new LiveCreature { Uuid = uuid, Idx = idx, Hp = -1, MaxHp = -1 }; _creatures[uuid] = c; }
        c.Seq = _seq;
        if (TryPos(ent, out var p)) { c.X = p.x; c.Y = p.y; c.Z = p.z; c.HasPos = true; }
        if (_getAttrLong != null)
        {
            try { c.Hp = _getAttrLong(ent, AttrHp); c.MaxHp = _getAttrLong(ent, AttrMaxHp); }
            catch (Exception ex) { _getAttrLong = null; Warn("creature HP read threw — HP off: " + ex.Message); }
            if (!_creLoggedHp && c.MaxHp >= 0)
            {
                _creLoggedHp = true;
                _services.Log.Info($"[Atlas] creature: {Creatures.Names[idx]} hp {c.Hp}/{c.MaxHp} (GetAttr<long> 11310/11320) " +
                                   $"at ({c.X:F1}, {c.Y:F1}, {c.Z:F1})");
            }
        }
        if (c.HasPos && HasPlayerPos) c.Dist = Vector3.Distance(new Vector3(c.X, c.Y, c.Z), PlayerPos);
    }

    // After the walk: creatures not in it left (killed when the last read said HP 0, else out of range / despawned —
    // the plugin decides with the player's distance). Ordinary monster uuids are forgotten every ~minute.
    private void EndCreatureWalk()
    {
        foreach (var kv in _creatures) if (kv.Value.Seq != _seq) _creGone.Add(kv.Key);
        foreach (long u in _creGone)
        {
            var c = _creatures[u];
            _creatures.Remove(u);
            _monIdx.Remove(u);
            _monSeen.Remove(u);
            CreatureLeft?.Invoke(c, c.Dead);
        }
        _creGone.Clear();
        if (_seq % MonPruneEvery != 0) return;
        foreach (var kv in _monSeen) if (_seq - kv.Value > MonPruneEvery) _creGone.Add(kv.Key);
        foreach (long u in _creGone) { _monSeen.Remove(u); _monIdx.Remove(u); }
        _creGone.Clear();
    }

    private void ResetCreatures()
    {
        _creatures.Clear();
        _monIdx.Clear();
        _monSeen.Clear();
    }

    private bool EnsureCreatureApi()
    {
        if (_creApiResolved) return _getBaseId != null;
        _creApiResolved = true;
        try
        {
            var entT = StellarInterop.FindType("Panda.ZGame.ZEntity");
            var attrT = StellarInterop.FindType("Zproto.EAttrType");
            if (entT == null) { Warn("creature api: ZEntity not found"); return false; }
            _getBaseId = Bind0<int>(entT.GetProperty("BaseId", BindingFlags.Public | BindingFlags.Instance)?.GetGetMethod());
            if (attrT != null && attrT.IsEnum)
                foreach (var m in entT.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (m.Name != "GetAttr" || !m.IsGenericMethodDefinition) continue;
                    var ps = m.GetParameters();
                    if (ps.Length != 2 || ps[0].ParameterType != attrT || ps[1].ParameterType != typeof(bool)) continue;
                    _getAttrLong = BindGetAttr(entT, attrT, m.MakeGenericMethod(typeof(long)));
                    break;
                }
            _services.Log.Info($"[Atlas] creature api baseId={_getBaseId != null} hp={_getAttrLong != null}");
        }
        catch (Exception ex) { Warn("creature api resolve error: " + (ex.InnerException?.Message ?? ex.Message)); }
        return _getBaseId != null;
    }

    // (object ent, int attr) => ((ZEntity)ent).GetAttr<long>((EAttrType)attr, true) — compiled once (expression trees bind
    // IL2CPP proxy calls fine, Plugin-Performance-Rules.md §6), so a read is a plain delegate call with no object[] / box.
    private static Func<object, int, long>? BindGetAttr(Type entT, Type attrT, MethodInfo mi)
    {
        try
        {
            var ent = Expression.Parameter(typeof(object), "ent");
            var attr = Expression.Parameter(typeof(int), "attr");
            var call = Expression.Call(Expression.Convert(ent, entT), mi, Expression.Convert(attr, attrT), Expression.Constant(true));
            return Expression.Lambda<Func<object, int, long>>(call, ent, attr).Compile();
        }
        catch { return null; }
    }
}
