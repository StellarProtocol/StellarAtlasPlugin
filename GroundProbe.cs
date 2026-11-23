using System;
using System.Reflection;
using Stellar.Abstractions.Services;
using UnityEngine;

namespace Stellar.Atlas;

// Ground height at a world (x, z) — for server points with no height (questlog seeds: y = null), so the guide marker
// sits on the ground instead of at the player's height (floating over a valley / sunk into a hill).
//
// Method (Collision-and-Navmesh-Bake.md §2 + Player-Locomotion-and-Follow.md "robust ground sample", in-game proven
// 2026-08-25): a straight-DOWN ray against the game's own physics, Panda.ZGame.ZPhysics.CheckCollideRayCast(in Vector3
// fromPos, in Vector3 toPos, int layerMask, out ZPhysx.RaycastHit hit) vs ZPhysics.GroundLayerMask, reading hit.Point.y
// (release_4.7 dump.cs 176196 / 176157 / ZPhysx.RaycastHit.Point 1741449). NOT the Unity Terrain: the game has no
// TerrainCollider and its visual terrain (ZTerrainData → Terrain) is a different surface from the one you stand on.
// The out param is the game's ZPhysx.RaycastHit (NOT UnityEngine.RaycastHit) → built from the method's own param type.
//
// Limits: physics cells stream in only around the player, so a far point misses (→ the caller keeps the player's
// height and asks again later). The ray takes the TOP ground surface under (x, z): a chest under a bridge gets the
// bridge's height. Called only by the guide (one point, ≤ 0.5 Hz) → the reflection Invoke's small allocation is fine.
internal sealed class GroundProbe
{
    private const float Above = 150f, Below = 150f;   // ray from player y + 150 down to player y − 150

    private readonly IPluginServices _services;
    private bool _resolved, _ok, _logged, _warned;
    private MethodInfo? _miRay;
    private FieldInfo? _fiPoint;
    private PropertyInfo? _piPoint;
    private object[]? _args;
    private object? _maskBox, _hitTemplate;

    public GroundProbe(IPluginServices services) => _services = services;

    // Ground y under (x, z), or false when unavailable (API missing, or the point's physics isn't loaded).
    public bool TryGround(float x, float z, float playerY, out float y)
    {
        y = 0f;
        if (!EnsureApi()) return false;
        try
        {
            var a = _args!;
            a[0] = new Vector3(x, playerY + Above, z);
            a[1] = new Vector3(x, playerY - Below, z);
            a[2] = _maskBox!;
            a[3] = _hitTemplate!;
            if (_miRay!.Invoke(null, a) is not true) return false;
            object hit = a[3];
            object? p = _fiPoint != null ? _fiPoint.GetValue(hit) : _piPoint?.GetValue(hit);
            if (p is not Vector3 v) return false;
            y = v.y;
            if (!_logged) { _logged = true; _services.Log.Info("[Atlas] ground height via ZPhysics.CheckCollideRayCast (GroundLayerMask)"); }
            return true;
        }
        catch (Exception ex)
        {
            if (!_warned) { _warned = true; _services.Log.Warning("[Atlas] ground height cast failed: " + (ex.InnerException?.Message ?? ex.Message)); }
            return false;
        }
    }

    private bool EnsureApi()
    {
        if (_resolved) return _ok;
        _resolved = true;
        try
        {
            var zt = StellarInterop.FindType("Panda.ZGame.ZPhysics");
            if (zt != null)
            {
                foreach (var m in zt.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.Name != "CheckCollideRayCast") continue;
                    var ps = m.GetParameters();
                    // the (from, to, mask, out hit) overload — NOT (from, direction, mask, distance, out hit)
                    if (ps.Length == 4 && ps[2].ParameterType == typeof(int) && ps[3].IsOut) { _miRay = m; break; }
                }
                int mask = 0;
                var v = zt.GetProperty("GroundLayerMask", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                if (v is int i) mask = i;
                var hitT = _miRay?.GetParameters()[3].ParameterType.GetElementType();
                if (hitT != null)
                {
                    _hitTemplate = Activator.CreateInstance(hitT);
                    _fiPoint = hitT.GetField("Point");
                    if (_fiPoint == null) _piPoint = hitT.GetProperty("Point");
                }
                _maskBox = mask;
                _args = new object[4];
                _ok = _miRay != null && mask != 0 && _hitTemplate != null && (_fiPoint != null || _piPoint != null);
            }
        }
        catch (Exception ex) { _services.Log.Warning("[Atlas] ground height resolve error: " + (ex.InnerException?.Message ?? ex.Message)); _ok = false; }
        if (!_ok) _services.Log.Info("[Atlas] ground height unavailable → player height");
        return _ok;
    }
}
