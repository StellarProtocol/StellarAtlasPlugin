using System;
using System.Collections.Generic;

namespace Stellar.Atlas;

// Guiding: clicking a NEAREST row puts the game's own guide marker (world icon + "58m" label, validated in-game) on that
// point via GameLua.SetGuide. The marker clears itself when the player is within ArriveRadius, on Stop guiding, on a
// scene change, on logout and on dispose.
//
// Marker height, best first (user-approved 2026-11-23): 1. the live entity's y (Seen live) → 2. the server row's y
// (reported points carry the exact height) → 3. the GROUND under (x, z) from the game's physics (GroundProbe.cs; cached
// per point, retried every GroundRetrySecs while guiding — physics only streams in near the player) → 4. the player's y.
// A better height re-sets the goal only when it differs from the placed one by more than 1 m. A guessed height is never
// reported (reports carry only live entity positions).
//
// "Just opened" (prototype 2d): when this character opens a chest, its row shows green "Opened" at the top of the list
// for FlashSeconds, then leaves; if the guide was on that chest (or had just arrived at it) the guide moves to the next
// nearest SHOWN point.
public sealed partial class Plugin
{
    private const float ArriveRadius    = 3f;
    private const float SameRadius      = 5f;    // a live entity this close to a guided server point (same chip) = it
    private const float ArrivedKeepSecs = 60f;   // after arriving, an open within this window still advances the guide
    private const float FlashSeconds    = 2.5f;
    private const float GroundRetrySecs = 2f;
    private const float ResetYDelta     = 1f;

    private struct GuideState
    {
        public bool Active, MarkerSet, Arrived;
        public long Uuid, ServerId;
        public Chip Chip;
        public float X, Y, Z;
        public bool HasY;                 // exact height (live entity or server row)
        public bool HasGround;            // ground height under (x, z) known (GroundY)
        public float GroundY, NextGroundAt;
        public float ArrivedAt, MarkedY;
    }

    private struct FlashState
    {
        public bool Active, AdvanceGuide;
        public Chip Chip;
        public string Label;
        public float Until;
    }

    private GuideState _guide;
    private FlashState _flash;

    private bool Guiding => _guide.Active && _guide.MarkerSet;

    // Row click.
    private void GuideTo(in NearestModel.Row r)
    {
        _guide = new GuideState
        {
            Active = true, Uuid = r.Uuid, ServerId = r.ServerId, Chip = r.Chip,
            X = r.X, Y = r.Y, Z = r.Z, HasY = r.HasY,
        };
        if (!_guide.HasY) TryGroundHeight(_services.Framework.TimeNow);
        SetMarker();
    }

    // The height the marker uses now (priority above).
    private float GuideY() => _guide.HasY ? _guide.Y
        : _guide.HasGround ? _guide.GroundY
        : (_scanner.HasPlayerPos ? _scanner.PlayerPos.y : _guide.Y);

    private bool GuideHasHeight => _guide.HasY || _guide.HasGround;

    // Ground under the target: per-point cache first, else one down-cast (≤ every GroundRetrySecs).
    private readonly Dictionary<long, float> _groundCache = new();

    private long GuideKey() => _guide.ServerId != 0 ? _guide.ServerId : -_guide.Uuid;

    private void TryGroundHeight(float now)
    {
        if (_guide.HasY || _guide.HasGround || now < _guide.NextGroundAt) return;
        _guide.NextGroundAt = now + GroundRetrySecs;
        long key = GuideKey();
        if (key != 0 && _groundCache.TryGetValue(key, out float gy)) { _guide.GroundY = gy; _guide.HasGround = true; return; }
        if (!_scanner.HasPlayerPos || !_ground.TryGround(_guide.X, _guide.Z, _scanner.PlayerPos.y, out gy)) return;
        _guide.GroundY = gy; _guide.HasGround = true;
        if (key != 0) _groundCache[key] = gy;
    }

    private void SetMarker()
    {
        float y = GuideY();
        _guide.MarkerSet = _lua.SetGuide(_scene, _guide.X, y, _guide.Z);
        _guide.MarkedY = y;
        if (!_guide.MarkerSet) _services.Log.Warning("[Atlas] guide marker could not be set (game Lua not ready?)");
    }

    private void StopGuide(bool clearMarker)
    {
        if (_guide.Active && _guide.MarkerSet && clearMarker) { try { _lua.ClearGuide(); } catch { } }
        _guide = default;
    }

    private void StopGuidingClicked() => StopGuide(clearMarker: true);

    private bool IsGuided(in NearestModel.Row r)
    {
        if (!_guide.Active || _guide.Arrived) return false;
        if (r.Uuid != 0 && r.Uuid == _guide.Uuid) return true;
        if (r.ServerId != 0 && r.ServerId == _guide.ServerId) return true;
        if (r.Chip != _guide.Chip) return false;
        float dx = r.X - _guide.X, dz = r.Z - _guide.Z;
        return dx * dx + dz * dz <= SameRadius * SameRadius;
    }

    // 5 Hz: follow the target when it becomes live (exact position + height), clear on arrival.
    private void TickGuide()
    {
        if (!_guide.Active || !_scanner.HasPlayerPos) return;
        var p = _scanner.PlayerPos;
        float now = _services.Framework.TimeNow;
        if (_guide.Arrived)
        {
            if (now - _guide.ArrivedAt > ArrivedKeepSecs) _guide = default;
            return;
        }

        if (FindLiveTarget() is { } live)
        {
            bool moved = MathF.Abs(live.X - _guide.X) + MathF.Abs(live.Z - _guide.Z) > 1f
                         || MathF.Abs(live.Y - _guide.MarkedY) > ResetYDelta;
            _guide.Uuid = live.Uuid; _guide.X = live.X; _guide.Y = live.Y; _guide.Z = live.Z; _guide.HasY = true;
            if (moved && _guide.MarkerSet) SetMarker();
        }
        else if (!_guide.HasY && !_guide.HasGround)
        {
            TryGroundHeight(now);
            if (_guide.HasGround && _guide.MarkerSet && MathF.Abs(_guide.GroundY - _guide.MarkedY) > ResetYDelta) SetMarker();
        }

        float d = NearestModel.Dist(_guide.X, GuideY(), _guide.Z, GuideHasHeight, p.x, p.y, p.z);
        if (d <= ArriveRadius)
        {
            if (_guide.MarkerSet) { try { _lua.ClearGuide(); } catch { } }
            _guide.MarkerSet = false;
            _guide.Arrived = true;
            _guide.ArrivedAt = now;
        }
    }

    private LiveEnt? FindLiveTarget()
    {
        var live = _scanner.Live;
        if (_guide.Uuid != 0) return live.TryGetValue(_guide.Uuid, out var e) && e.Tracked && e.HasPos ? e : null;
        foreach (var l in live.Values)
        {
            if (!l.Tracked || !l.HasPos || l.Chip != _guide.Chip) continue;
            float dx = l.X - _guide.X, dz = l.Z - _guide.Z;
            if (dx * dx + dz * dz <= SameRadius * SameRadius) return l;
        }
        return null;
    }

    // Scanner: a chest this character opened (OnLiveOpened has already recorded it).
    private void OnChestOpened(LiveEnt e)
    {
        bool wasTarget = _guide.Active && (e.Uuid == _guide.Uuid
                         || (e.Chip == _guide.Chip && Sq(e.X - _guide.X) + Sq(e.Z - _guide.Z) <= SameRadius * SameRadius));
        var info = _lua.Cfg(e.Cfg);
        _flash = new FlashState
        {
            Active = true, AdvanceGuide = wasTarget, Chip = e.Chip,
            Label = info.Name.Length > 0 ? info.Name : _loc.T(AtlasGroups.LabelKey(e.Chip)),
            Until = _services.Framework.TimeNow + FlashSeconds,
        };
        if (wasTarget && _guide.MarkerSet) { try { _lua.ClearGuide(); } catch { } _guide.MarkerSet = false; }
        RecomputeNearest();
    }

    // 1 Hz: end the flash; move the guide to the next nearest shown point if it was on the opened chest.
    private void TickFlash(float now)
    {
        if (!_flash.Active || now < _flash.Until) return;
        bool advance = _flash.AdvanceGuide;
        _flash = default;
        if (!advance) return;
        RecomputeNearest();
        if (_model.Count > 0) GuideTo(in _model.Out[0]);
        else _guide = default;
    }

    private static float Sq(float v) => v * v;
}
