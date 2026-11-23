using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using UnityEngine;
using UnityEngine.UI;

namespace Stellar.Atlas;

// Icon pass: the game draws our flags with a placeholder game sprite (ui/atlas/npc/talk_icon_box); we swap each unit's
// icon for our own rarity tile (Resources/map, tools/make-map-icons.py). Validated path (Auto-Gather-and-Interaction.md
// "Rarity colour"): unit `positionFlagName<Id>/node_parent/img_icon` (UI.Image) under
//   big map  zuiroot/UILayerFunc/map_main_pc(Clone)/anim/scrollview_map/node_viewport/rimg_map_bg/node_area_map/node_flags
//   minimap  …/main_minimap_sub_pc(Clone)/group_minimap/img_frame/mask/node_map_parent/rimg_map_bg/node_flags
// The same unit names exist under BOTH roots (GameObject.Find(name) would hit the minimap first) → search per root.
//
// When (fix 2026-11-23: a pass only after each "map rebuilt" bump missed 14/117 units created async later): EVERY 2 Hz
// poll, for each map root that is active, sweep a round-robin slice of our entries (SweepPerPass), within a time budget:
//   • unit cached + its img_icon.sprite IS our tile → nothing (one reference compare);
//   • cached but the sprite isn't ours (pooled unit reused / re-initialised) → look it up again BY NAME and re-apply;
//   • not found → retry after MissBackoff passes (a far point may have no minimap unit at all), or at once after the
//     game rebuilt its flags (_G.__atlas_mapgen changed).
// Steady cost: ≤ SweepPerPass reference compares per active root per poll, lookups only for units that changed.
//
// Rebuild burst (2026-11-23, minimap blink): every rebuild — ours or the game's own (scene load, minimap refresh) —
// re-creates the units with the placeholder sprite (IconPath must stay non-empty: map_flags_comp initFlagUnit hides the
// unit first and only shows it again inside the `IconPath ~= ""` branch, so an empty path would hide our flags). The
// wrapper bumps _G.__atlas_mapgen on every rebuild; FrameTick reads that number each plugin tick and, when it moved,
// runs the same budgeted pass on each of the next BurstTicks ticks (≈0.5 s at the ~30 Hz plugin tick) so the
// placeholder is replaced within a frame or two instead of after up to 0.5 s; the 2 Hz pass then mops up.
//
// Sprites: Texture2D.LoadImage → Sprite.Create, at most ONE new tile per pass (spreads the decode), both HideAndDontSave
// (WindowBuilder-Patterns "Scene-switch crash audit"), re-validated with Unity's == null. If assigning our sprite throws,
// fall back to TINTING the game's white placeholder with the chip colour (the validated look), logged once.
internal sealed partial class MapMarkers
{
    private const int    SweepPerPass = 100;
    private const int    FindBudget   = 40;
    private const int    MissBackoff  = 6;      // passes (≈3 s at 2 Hz)
    private const double PassBudgetMs = 3.0;
    private const string BigRootPath  = "zuiroot/UILayerFunc/map_main_pc(Clone)/anim/scrollview_map/node_viewport/rimg_map_bg/node_area_map/node_flags";
    private const string MiniHostName = "main_minimap_sub_pc(Clone)";
    private const string MiniRootRel  = "group_minimap/img_frame/mask/node_map_parent/rimg_map_bg/node_flags";
    private static readonly int[] TileSizes = { 32, 42, 64, 96 };

    private sealed class RootState
    {
        public Image?[] Cache = Array.Empty<Image?>();
        public int[] RetryAt = Array.Empty<int>();
        public int Cursor;
        public void Reset(int n) { Cache = new Image?[n]; RetryAt = new int[n]; Cursor = 0; }
        public void ClearMisses() { Array.Clear(RetryAt, 0, RetryAt.Length); }
    }

    private string[] _paths = Array.Empty<string>();
    private Chip[] _chips = Array.Empty<Chip>();
    private readonly RootState _big = new(), _mini = new();
    private readonly Sprite?[] _sprites = new Sprite?[AtlasGroups.ChipCount];
    private readonly Texture2D?[] _textures = new Texture2D?[AtlasGroups.ChipCount];
    private Transform? _miniRoot;
    private float _miniResolveAt = -100f;
    private double _mapGen = -1;
    private int _pass;
    private bool _tintFallback, _createdThisPass;

    private void CommitIcons(List<Entry> job)
    {
        int n = job.Count;
        _paths = new string[n];
        _chips = new Chip[n];
        for (int i = 0; i < n; i++)
        {
            _paths[i] = "positionFlagName" + (IdBase + job[i].N).ToString(CultureInfo.InvariantCulture) + "/node_parent/img_icon";
            _chips[i] = job[i].Chip;
        }
        _big.Reset(n);
        _mini.Reset(n);
    }

    private void ResetIcons()
    {
        _paths = Array.Empty<string>();
        _chips = Array.Empty<Chip>();
        _big.Reset(0);
        _mini.Reset(0);
    }

    private const int BurstTicks = 15;
    private int _burst;

    // Every plugin tick (world-ready): one Lua number read; a pass only during a burst after a rebuild.
    public void FrameTick(float now)
    {
        if (!_installed || _paths.Length == 0) return;
        if (Lua.TryReadGlobalNumber("__atlas_mapgen", out double gen) && gen != _mapGen)
        {
            _mapGen = gen;
            _big.ClearMisses(); _mini.ClearMisses();   // fresh units exist now → look again at once
            _burst = BurstTicks;
        }
        if (_burst <= 0) return;
        _burst--;
        RunPass(now);
    }

    // 2 Hz (world-ready): the steady sweep (late async units, pooled reuse, points pushed since).
    public void PollIcons(float now)
    {
        if (!_installed || _paths.Length == 0 || _burst > 0) return;
        RunPass(now);
    }

    private void RunPass(float now)
    {
        _pass++;
        _createdThisPass = false;
        long t0 = Stopwatch.GetTimestamp();
        try
        {
            var big = GameObject.Find(BigRootPath);   // null while the big map is closed (Find skips inactive objects)
            if (big != null) Pass(big.transform, _big, t0);
            var mini = MiniRoot(now);
            if (mini != null) Pass(mini, _mini, t0);
        }
        catch (Exception ex) { WarnOnce(ref _passWarned, "map icon pass failed: " + ex.Message); }
        Slow("icon pass", t0);
    }
    private bool _passWarned;

    // The minimap host is found by NAME (whole-hierarchy search) → cached, re-resolved at most every 10 s when lost.
    private Transform? MiniRoot(float now)
    {
        if (_miniRoot != null && _miniRoot.gameObject.activeInHierarchy) return _miniRoot;
        if (now - _miniResolveAt < 10f) return null;
        _miniResolveAt = now;
        long t0 = Stopwatch.GetTimestamp();
        var host = GameObject.Find(MiniHostName);
        _miniRoot = host != null ? host.transform.Find(MiniRootRel) : null;
        Slow("minimap root lookup", t0);
        return _miniRoot;
    }

    private void Pass(Transform root, RootState st, long t0)
    {
        if (!root.gameObject.activeInHierarchy) return;
        int n = _paths.Length, finds = 0;
        for (int k = 0; k < SweepPerPass && k < n; k++)
        {
            if (finds >= FindBudget || ElapsedMs(t0) > PassBudgetMs) return;   // the cursor keeps our place
            int i = st.Cursor;
            st.Cursor = (st.Cursor + 1) % n;
            var sprite = Tile(_chips[i]);
            if (sprite == null && !_tintFallback) continue;   // tile not decoded yet (one per pass) → next pass
            var img = st.Cache[i];
            if (img != null && Looks(img, sprite, _chips[i])) continue;
            if (img == null && _pass < st.RetryAt[i]) continue;
            finds++;
            var t = root.Find(_paths[i]);
            img = t != null ? t.GetComponent<Image>() : null;
            st.Cache[i] = img;
            if (img == null) { st.RetryAt[i] = _pass + MissBackoff; continue; }
            Apply(img, sprite, _chips[i]);
        }
    }

    private bool Looks(Image img, Sprite? sprite, Chip chip)
    {
        if (_tintFallback || sprite == null) return img.color == ToColor(AtlasGroups.Color(chip));
        return img.sprite == sprite;
    }

    private void Apply(Image img, Sprite? sprite, Chip chip)
    {
        if (!_tintFallback && sprite != null)
        {
            try { img.sprite = sprite; img.color = Color.white; return; }
            catch (Exception ex)
            {
                _tintFallback = true;
                _services.Log.Warning("[Atlas] map icon sprite could not be assigned — tinting the game icon instead: " + ex.Message);
            }
        }
        img.color = ToColor(AtlasGroups.Color(chip));
    }

    private Sprite? Tile(Chip chip)
    {
        int c = (int)chip;
        var s = _sprites[c];
        if (s != null && _textures[c] != null) return s;
        if (_tintFallback || _createdThisPass) return null;
        _createdThisPass = true;
        long t0 = Stopwatch.GetTimestamp();
        try
        {
            var png = LoadTilePng(AtlasGroups.MapIconStem(chip));
            if (png == null) return null;
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            if (!ImageConversion.LoadImage(tex, png)) { UnityEngine.Object.Destroy(tex); return null; }
            tex.hideFlags = HideFlags.HideAndDontSave;
            s = Sprite.Create(tex, new Rect(0f, 0f, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
            s.hideFlags = HideFlags.HideAndDontSave;
            _textures[c] = tex;
            _sprites[c] = s;
            return s;
        }
        catch (Exception ex)
        {
            _tintFallback = true;
            _services.Log.Warning("[Atlas] map icon sprite could not be created — tinting the game icon instead: " + ex.Message);
            return null;
        }
        finally { Slow("tile decode", t0); }
    }

    // The unit is ~42 px at 1440p → the bitmap pre-drawn for the current screen height, else the next larger.
    private byte[]? LoadTilePng(string stem)
    {
        float want = 42f * Screen.height / 1440f - 0.5f;
        int px = TileSizes[^1];
        foreach (int s in TileSizes) if (s >= want) { px = s; break; }
        try
        {
            using var st = typeof(MapMarkers).Assembly.GetManifestResourceStream($"Stellar.Atlas.map-{stem}-{px}.png");
            if (st == null) return null;
            using var ms = new System.IO.MemoryStream();
            st.CopyTo(ms);
            return ms.ToArray();
        }
        catch { return null; }
    }

    private void DestroySprites()
    {
        for (int i = 0; i < _sprites.Length; i++)
        {
            try { if (_sprites[i] != null) UnityEngine.Object.Destroy(_sprites[i]); } catch { }
            try { if (_textures[i] != null) UnityEngine.Object.Destroy(_textures[i]); } catch { }
            _sprites[i] = null; _textures[i] = null;
        }
    }

    private static Color ToColor(Stellar.Abstractions.Domain.ColorRgba c) => new(c.R, c.G, c.B, 1f);

    // ── slow-step log (the [Hitch] 137 ms hunt) ───────────────────────────────────────────────────────────────────
    // Two Stopwatch reads per step (steps run a few times a second at most, not per frame); a step over SlowMs is
    // logged with its name, at most MaxSlowLogs times a session, so the next in-game run shows where the time went.
    private const double SlowMs = 4.0;
    private const int MaxSlowLogs = 30;
    private int _slowLogs;

    private static double ElapsedMs(long t0) => (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;

    private void Slow(string what, long t0)
    {
        double ms = ElapsedMs(t0);
        if (ms <= SlowMs || _slowLogs >= MaxSlowLogs) return;
        _slowLogs++;
        _services.Log.Warning($"[Atlas] slow map step: {what} {ms:F1} ms");
    }
}
