using System;
using System.Reflection;
using Stellar.Abstractions.Services;
using UnityEngine;

namespace Stellar.Atlas;

// Every game member the scanner reads, resolved ONCE (first world-ready poll) and bound into typed delegates, so the
// 2 Hz walk pays no MethodInfo.Invoke / object[] / boxing per read (Plugin-Performance-Rules.md §2-3). Port of the
// in-game-validated Experiment Chest Probe (ChestProbe.Reflection.cs, 2026-11-23). All names verified in release_4.7:
//   ZEntityMgr (ZSingleton → FlattenHierarchy) .entityDict_ Dictionary<long,ZEntity> · GetEntity(long)
//   ZEntityHelper.TryGetConfigIdByUuid(long, out int) (static)               — collection config id
//   ZEntity.Model → ZModel.ModelGoComp → .Position                           — world position
//   Panda.ZGame.EntityAttrExtensions.GetAttrObjState(ZEntity) : int          — AttrObjState=360 (NOT GetAttr<long>:
//     int32 attrs read as long log "arr type err" every call)
//   Bokura.Table.ITable.CurrentSceneId (static int)                          — the BIG MAP's browsed scene id, NOT where the
//     player is: only a change trigger for the actual-scene read (Plugin.Tick.cs CheckScene)
internal sealed partial class EntityScanner
{
    private bool _apiResolved, _apiOk;

    private Func<object?>?               _getMgr;
    private PropertyInfo?                _piEntityDict;   // IL2CPP field → PropertyInfo
    private Func<object, object?>?       _getDict;
    private Func<object, long, object?>? _getEntity;
    private MethodInfo?                  _miTryGetConfigId;   // out param → Invoke, only on FIRST sight of a uuid (cached)
    private Func<object, object?>?       _getModel, _getGoComp;
    private Func<object, Vector3>?       _getPos;
    private Func<object, int>?           _getObjState;
    private Func<int>?                   _getSceneId;

    // Bound entityDict_.Keys walk (mirrors MechanicCalloutTracker.FastReflection.ResolveKeyWalk).
    private Func<object, object?>? _dictKeysFn, _keysEnumFn;
    private Func<object, bool>?    _enumMoveFn;
    private Func<object, long>?    _enumCurFn;
    private bool                   _walkResolved;

    private bool _colPos = true, _colObj = true;   // flipped off (logged once) on first failure

    private bool EnsureApi()
    {
        if (_apiResolved) return _apiOk;
        _apiResolved = true;
        try
        {
            var entMgr = StellarInterop.FindType("Panda.ZGame.ZEntityMgr");
            var entT   = StellarInterop.FindType("Panda.ZGame.ZEntity");
            var modelT = StellarInterop.FindType("Panda.ZGame.ZModel");
            var helper = StellarInterop.FindType("Panda.ZGame.ZEntityHelper");
            if (entMgr == null || entT == null) { Warn($"type resolve failed entMgr={entMgr != null} ent={entT != null}"); return false; }

            const BindingFlags pubInst = BindingFlags.Public | BindingFlags.Instance;
            const BindingFlags anyInst = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            _getMgr = BindStatic0(entMgr.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)?.GetGetMethod());
            _piEntityDict = entMgr.GetProperty("entityDict_", anyInst);
            _getDict = Bind0<object?>(_piEntityDict?.GetGetMethod(true));
            // Hand-scan by param TYPE (Framework-Interop-Foundation.md: name+arity lookups can pick the wrong overload).
            foreach (var m in entMgr.GetMethods(pubInst))
                if (m.Name == "GetEntity" && m.GetParameters() is { Length: 1 } ps && ps[0].ParameterType == typeof(long))
                { _getEntity = Bind1<long, object?>(m); break; }

            if (helper != null)
                foreach (var m in helper.GetMethods(BindingFlags.Public | BindingFlags.Static))
                    if (m.Name == "TryGetConfigIdByUuid" && m.GetParameters() is { Length: 2 } ps
                        && ps[0].ParameterType == typeof(long) && ps[1].IsOut) { _miTryGetConfigId = m; break; }

            var piGo = modelT?.GetProperty("ModelGoComp", pubInst);
            _getModel  = Bind0<object?>(entT.GetProperty("Model", pubInst)?.GetGetMethod());
            _getGoComp = Bind0<object?>(piGo?.GetGetMethod());
            _getPos    = Bind0<Vector3>(piGo?.PropertyType.GetProperty("Position", pubInst)?.GetGetMethod());
            if (_getModel == null || _getGoComp == null || _getPos == null) { _colPos = false; Warn("position chain unbound"); }

            // Three EntityAttrExtensions classes exist — only Panda.ZGame's has GetAttrObjState.
            var ext = StellarInterop.FindType("Panda.ZGame.EntityAttrExtensions");
            MethodInfo? miObj = null;
            if (ext != null)
                foreach (var m in ext.GetMethods(BindingFlags.Public | BindingFlags.Static))
                    if (m.Name == "GetAttrObjState" && m.ReturnType == typeof(int) && m.GetParameters() is { Length: 1 } ps && ps[0].ParameterType == entT)
                    { miObj = m; break; }
            _getObjState = BindStatic1<int>(miObj);
            if (_getObjState == null) { _colObj = false; Warn("GetAttrObjState unbound — opened detection off"); }

            var sceneT = StellarInterop.FindType("Bokura.Table.ITable");
            var piScene = sceneT?.GetProperty("CurrentSceneId", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy);
            if (piScene?.PropertyType == typeof(int)) _getSceneId = BindStaticGetter<int>(piScene.GetGetMethod(true));
            if (_getSceneId == null) Warn("ITable.CurrentSceneId unbound — falling back to ClientState.CurrentSceneName");

            _apiOk = _getMgr != null && (_getDict != null || _piEntityDict != null) && _getEntity != null && _miTryGetConfigId != null;
            _services.Log.Info($"[Atlas] scanner api ok={_apiOk} pos={_colPos} objState={_colObj} scene={_getSceneId != null}");
            return _apiOk;
        }
        catch (Exception ex) { Warn("api resolve error: " + (ex.InnerException?.Message ?? ex.Message)); return false; }
    }

    // The TABLE scene (follows the big map's browse) — a cheap change trigger only; never stamp anything with it.
    public int ReadSceneId()
    {
        int id = 0;
        try { if (_getSceneId != null) id = _getSceneId(); } catch { }
        if (id != 0) return id;
        // Same fallback as the Chest Probe: the framework's scene name is the numeric scene id.
        try { return int.TryParse(_services.ClientState.CurrentSceneName, out int v) ? v : 0; } catch { return 0; }
    }

    private bool TryPos(object ent, out Vector3 pos)
    {
        pos = default;
        if (!_colPos) return false;
        try
        {
            var model = _getModel!(ent);
            var go = model == null ? null : _getGoComp!(model);
            if (go == null) return false;
            pos = _getPos!(go);
            return true;
        }
        catch (Exception ex) { _colPos = false; Warn("position read threw — disabled: " + ex.Message); return false; }
    }

    private int ReadObjState(object ent)   // int.MinValue = n/a
    {
        if (!_colObj || _getObjState == null) return int.MinValue;
        try { return _getObjState(ent); }
        catch (Exception ex) { _colObj = false; Warn("GetAttrObjState threw — disabled: " + ex.Message); return int.MinValue; }
    }

    private int ResolveConfigId(long uuid)  // Invoke allocates → only on first sight of a uuid (cached by the caller)
    {
        try
        {
            var args = new object[] { uuid, 0 };
            if (_miTryGetConfigId!.Invoke(null, args) is not true) return 0;
            return Convert.ToInt32(args[1] ?? 0);
        }
        catch (Exception ex) { WarnOnce(ref _cfgErrLogged, "TryGetConfigIdByUuid threw: " + (ex.InnerException?.Message ?? ex.Message)); return 0; }
    }
    private bool _cfgErrLogged;

    private void ResolveKeyWalk(object dict)
    {
        _walkResolved = true;
        try
        {
            var keysGet = dict.GetType().GetProperty("Keys")?.GetGetMethod();
            var getEnum = keysGet != null ? FindNoArg(keysGet.ReturnType, "GetEnumerator") : null;
            var enT     = getEnum?.ReturnType;
            _dictKeysFn = Bind0<object?>(keysGet);
            _keysEnumFn = Bind0<object?>(getEnum);
            _enumMoveFn = Bind0<bool>(enT != null ? FindNoArg(enT, "MoveNext") : null);
            _enumCurFn  = Bind0<long>(enT?.GetProperty("Current")?.GetGetMethod());
        }
        catch { }
        if (_dictKeysFn == null || _keysEnumFn == null || _enumMoveFn == null || _enumCurFn == null)
        {
            _dictKeysFn = _keysEnumFn = null; _enumMoveFn = null; _enumCurFn = null;
            Warn("entityDict_ key walk could not be bound — live scan disabled");
        }
    }

    private static MethodInfo? FindNoArg(Type t, string name)
    {
        foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            if (m.Name == name && m.GetParameters().Length == 0) return m;
        return null;
    }

    // ── delegate binders (same shape as the Chest Probe; null = shape didn't fit) ─────────────────────────────────
    private static MethodInfo Helper(string name, params Type[] args) =>
        typeof(EntityScanner).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!.MakeGenericMethod(args);

    private static bool ReturnFits<TR>(Type rt) => rt.IsValueType ? rt == typeof(TR) : typeof(TR).IsAssignableFrom(rt);

    private static Func<object, TR>? Bind0<TR>(MethodInfo? m)
    {
        try
        {
            if (m == null || m.IsStatic || m.GetParameters().Length != 0) return null;
            var dt = m.DeclaringType;
            if (dt == null || dt.IsValueType || dt.ContainsGenericParameters || !ReturnFits<TR>(m.ReturnType)) return null;
            return (Func<object, TR>?)Helper(nameof(Call0), dt, m.ReturnType).Invoke(null, new object[] { m });
        }
        catch { return null; }
    }

    private static Func<object, TA, TR>? Bind1<TA, TR>(MethodInfo? m)
    {
        try
        {
            if (m == null || m.IsStatic || m.GetParameters() is not { Length: 1 } ps || ps[0].ParameterType != typeof(TA)) return null;
            var dt = m.DeclaringType;
            if (dt == null || dt.IsValueType || dt.ContainsGenericParameters || !ReturnFits<TR>(m.ReturnType)) return null;
            return (Func<object, TA, TR>?)Helper(nameof(Call1), dt, typeof(TA), m.ReturnType).Invoke(null, new object[] { m });
        }
        catch { return null; }
    }

    private static Func<object?>? BindStatic0(MethodInfo? m)
    {
        try
        {
            if (m == null || !m.IsStatic || m.GetParameters().Length != 0 || m.ReturnType.IsValueType) return null;
            return (Func<object?>?)Helper(nameof(StaticCall0), m.ReturnType).Invoke(null, new object[] { m });
        }
        catch { return null; }
    }

    private static Func<TR>? BindStaticGetter<TR>(MethodInfo? m)
    {
        try { return m == null || !m.IsStatic || m.ReturnType != typeof(TR) ? null : (Func<TR>)Delegate.CreateDelegate(typeof(Func<TR>), m); }
        catch { return null; }
    }

    // Static method with one reference-type arg (an [Extension] taking ZEntity) → Func<object, TR>.
    private static Func<object, TR>? BindStatic1<TR>(MethodInfo? m)
    {
        try
        {
            if (m == null || !m.IsStatic || m.GetParameters() is not { Length: 1 } ps || ps[0].ParameterType.IsValueType || m.ReturnType != typeof(TR)) return null;
            return (Func<object, TR>?)Helper(nameof(StaticCall1), ps[0].ParameterType, typeof(TR)).Invoke(null, new object[] { m });
        }
        catch { return null; }
    }

    private static Func<object, TRet> Call0<T, TRet>(MethodInfo m) where T : class
    { var d = (Func<T, TRet>)Delegate.CreateDelegate(typeof(Func<T, TRet>), m); return o => d((T)o); }

    private static Func<object, TA, TRet> Call1<T, TA, TRet>(MethodInfo m) where T : class
    { var d = (Func<T, TA, TRet>)Delegate.CreateDelegate(typeof(Func<T, TA, TRet>), m); return (o, a) => d((T)o, a); }

    private static Func<TRet> StaticCall0<TRet>(MethodInfo m) => (Func<TRet>)Delegate.CreateDelegate(typeof(Func<TRet>), m);

    private static Func<object, TRet> StaticCall1<T, TRet>(MethodInfo m) where T : class
    { var d = (Func<T, TRet>)Delegate.CreateDelegate(typeof(Func<T, TRet>), m); return o => d((T)o); }

    private void Warn(string msg) => _services.Log.Warning("[Atlas] " + msg);
    private void WarnOnce(ref bool flag, string msg) { if (flag) return; flag = true; Warn(msg); }
}
