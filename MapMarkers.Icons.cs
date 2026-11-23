using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.UI;

namespace Stellar.Atlas;

// Icon pass: the game draws our flags with a placeholder game sprite (ui/atlas/npc/talk_icon_box); we swap each unit's
// icon for our own rarity tile (Resources/map, tools/make-map-icons.py) after the map builds it. Validated path
// (Auto-Gather-and-Interaction.md "Rarity colour"): unit `positionFlagName<Id>/node_parent/img_icon` (UI.Image) under
//   big map  zuiroot/UILayerFunc/map_main_pc(Clone)/anim/scrollview_map/node_viewport/rimg_map_bg/node_area_map/node_flags
//   minimap  …/main_minimap_sub_pc(Clone)/group_minimap/img_frame/mask/node_map_parent/rimg_map_bg/node_flags
// The same unit names exist under BOTH roots (GameObject.Find(name) would hit the minimap first) → search per root.
//
// When: units are rebuilt asynchronously (and pooled) on every RefreshMap, whose only data source is our wrapper — so
// the wrapper bumps _G.__atlas_mapgen. The 2 Hz Poll reads that one number; only when it changed does it run a few
// passes (PassesPerRefresh, ≤ PassBudget lookups each) that fill units not yet carrying our tile. A unit already showing
// our tile is skipped without a lookup; one that lost it (pooled + reused) is looked up again BY NAME, so a tile can't
// stick to a unit that now belongs to another flag. Idle cost: one Lua number read per poll.
//
// Sprites: Texture2D.LoadImage → Sprite.Create once per chip, both HideAndDontSave (WindowBuilder-Patterns "Scene-switch
// crash audit": a sprite held only by a C# cache is swept by UnloadUnusedAssets otherwise), re-validated with Unity's
// == null. If assigning our sprite throws, fall back to TINTING the game's white placeholder with the chip colour
// (the validated look), logged once.
internal sealed partial class MapMarkers
{
    private const int PassesPerRefresh = 4;
    private const int PassBudget       = 150;
    private const string BigRootPath  = "zuiroot/UILayerFunc/map_main_pc(Clone)/anim/scrollview_map/node_viewport/rimg_map_bg/node_area_map/node_flags";
    private const string MiniHostName = "main_minimap_sub_pc(Clone)";
    private const string MiniRootRel  = "group_minimap/img_frame/mask/node_map_parent/rimg_map_bg/node_flags";
    private static readonly int[] TileSizes = { 32, 42, 64, 96 };

    private string[] _paths = Array.Empty<string>();
    private Chip[] _chips = Array.Empty<Chip>();
    private Image?[] _bigCache = Array.Empty<Image?>(), _miniCache = Array.Empty<Image?>();
    private readonly Sprite?[] _sprites = new Sprite?[AtlasGroups.ChipCount];
    private readonly Texture2D?[] _textures = new Texture2D?[AtlasGroups.ChipCount];
    private Transform? _miniRoot;
    private float _miniResolveAt = -100f;
    private double _mapGen = -1;
    private int _passesLeft;
    private bool _tintFallback;

    private void CommitIcons(List<Entry> job)
    {
        int n = job.Count;
        _paths = new string[n];
        _chips = new Chip[n];
        _bigCache = new Image?[n];
        _miniCache = new Image?[n];
        for (int i = 0; i < n; i++)
        {
            _paths[i] = "positionFlagName" + (IdBase + job[i].N).ToString(CultureInfo.InvariantCulture) + "/node_parent/img_icon";
            _chips[i] = job[i].Chip;
        }
        _passesLeft = PassesPerRefresh;
    }

    private void ResetIcons()
    {
        _paths = Array.Empty<string>();
        _chips = Array.Empty<Chip>();
        _bigCache = Array.Empty<Image?>();
        _miniCache = Array.Empty<Image?>();
        _passesLeft = 0;
    }

    // 2 Hz (world-ready).
    public void PollIcons(float now)
    {
        if (!_installed || _paths.Length == 0) return;
        if (Lua.TryReadGlobalNumber("__atlas_mapgen", out double gen) && gen != _mapGen) { _mapGen = gen; _passesLeft = PassesPerRefresh; }
        if (_passesLeft <= 0) return;
        _passesLeft--;
        try
        {
            var big = GameObject.Find(BigRootPath);   // null while the big map is closed (Find skips inactive objects)
            if (big != null) Pass(big.transform, _bigCache);
            var mini = MiniRoot(now);
            if (mini != null) Pass(mini, _miniCache);
        }
        catch (Exception ex) { WarnOnce(ref _passWarned, "map icon pass failed: " + ex.Message); _passesLeft = 0; }
    }
    private bool _passWarned;

    // The minimap host is found by NAME (whole-hierarchy search) → cached, re-resolved at most every 10 s when lost.
    private Transform? MiniRoot(float now)
    {
        if (_miniRoot != null && _miniRoot.gameObject.activeInHierarchy) return _miniRoot;
        if (now - _miniResolveAt < 10f) return null;
        _miniResolveAt = now;
        var host = GameObject.Find(MiniHostName);
        _miniRoot = host != null ? host.transform.Find(MiniRootRel) : null;
        return _miniRoot;
    }

    private void Pass(Transform root, Image?[] cache)
    {
        if (!root.gameObject.activeInHierarchy) return;
        int lookups = 0;
        for (int i = 0; i < _paths.Length && lookups < PassBudget; i++)
        {
            var sprite = Tile(_chips[i]);
            var img = cache[i];
            if (img != null && Looks(img, sprite, _chips[i])) continue;
            lookups++;
            var t = root.Find(_paths[i]);
            img = t != null ? t.GetComponent<Image>() : null;
            cache[i] = img;
            if (img != null) Apply(img, sprite, _chips[i]);
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
        if (_tintFallback) return null;
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
}
