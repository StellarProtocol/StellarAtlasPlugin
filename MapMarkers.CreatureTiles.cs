using System;
using System.Collections.Generic;
using System.Diagnostics;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace Stellar.Atlas;

// Magical creature flags on the game's big map + minimap (approved prototype atlas-creatures.tpl.html, map mock):
// round head tile in a 2 px dark ring; alive on your line = full colour + green ring; other lines = faded + a small line
// badge ("7", "2·6·9"); an absent fixed-schedule boarlet = faded + its next spawn time ("14:00").
//
// The flags ride the same wrapper / push / prestage as the chests (MapMarkers.cs): Select appends the creature model's
// PUBLISHED set when the current scene is Asteria Plains, and the prestage build of scene 7 appends it too — so entering
// the scene finds them already staged (no extra MapFlagShowListChanged). The set is published only on material change
// and at most once a minute (CreatureModel.UpdateFlags); everything else (your line, badges, times, hp) is the TILE, which
// the icon pass swaps in place — never a rebuild.
//
// Tiles: the head disc art is pre-drawn per unit size (tools/icons/gen-creature-icons.py: mapc-<id>-here|dim-<px>.png).
// The badge / time pill is COMPOSITED onto a copy of those pixels at runtime from pre-drawn glyph pixels of the same size
// (mapc-glyphs-<px>.png) — pixel copies and an anti-aliased rounded rect, never a scaled bitmap (house icon rule). Pixel
// work goes through ONE span over the IL2CPP array (ChatTools Plugin.Avatars.Bake.cs: per-element access = several native
// calls per pixel). At most one tile is composed per icon pass (like the chest tile decode); composed sprites are cached by
// tile key (creature | state | badge) — a handful per session.
internal sealed partial class MapMarkers
{
    private const string Glyphs = "0123456789·:+";
    private const int MaxCreSprites = 48;

    public CreatureModel? CreatureSource;

    private long[] _creN = Array.Empty<long>();          // per icon entry: creature flag n, 0 = a chest / point
    private readonly Dictionary<string, Sprite> _creSprites = new();
    private readonly List<Texture2D> _creTextures = new();
    private readonly Dictionary<string, Color32[]?> _crePixels = new();   // "<id>-here|dim" → base tile pixels (cur size)
    private Color32[]? _glyphPx;
    private int _glyphW, _glyphH, _glyphCell, _creSize;
    private readonly int[] _glyphX0 = new int[Glyphs.Length], _glyphX1 = new int[Glyphs.Length];
    private bool _creFailed;

    // Select / prestage build of scene 7: the published creature flags after the chest entries (outside MapCap).
    private void AppendCreatures(int scene, List<Entry> job, ref ulong sig, ref ulong sigAll)
    {
        var src = CreatureSource;
        if (scene != Creatures.Scene || src == null) return;
        foreach (var f in src.Published)
        {
            job.Add(new Entry { N = f.N, Chip = Chip.Hidden, X = f.X, Y = f.Y, Z = f.Z, Name = Creatures.Names[f.Idx] });
            sig = (sig ^ (ulong)f.N) * 1099511628211UL;
            sig = (sig ^ (ulong)(long)MathF.Round(f.X)) * 1099511628211UL;
            sigAll = (sigAll ^ (ulong)f.N) * 1099511628211UL;
            sigAll = (sigAll ^ (ulong)(long)MathF.Round(f.X)) * 1099511628211UL;
        }
    }

    // A new creature flag set was published. In the scene: the next Select (≤ SelectGap / DispatchGap) pushes it. Away:
    // re-stage scene 7 silently (no dispatch: it is not on screen), so arriving there costs no rebuild.
    public void CreaturesChanged()
    {
        if (_curScene == Creatures.Scene) { _lastSelectAt = -100f; return; }
        if (_staged.ContainsKey(Creatures.Scene) || _emptyScenes.ContainsKey(Creatures.Scene)) Enqueue(Creatures.Scene);
    }

    // The sprite for a creature tile, or null while it is still to be composed (one per pass).
    private Sprite? CreatureSprite(CreatureModel.Tile t)
    {
        int px = TilePx();
        if (px != _creSize) { DestroyCreatureSprites(); _creSize = px; }
        if (_creSprites.TryGetValue(t.Key, out var s) && s != null) return s;
        if (_creFailed || _createdThisPass) return null;
        _createdThisPass = true;
        long t0 = Stopwatch.GetTimestamp();
        try
        {
            var basePx = BasePixels(t.Idx, t.Kind == CreatureModel.TileKind.Here, px);
            if (basePx == null || !EnsureGlyphs(px)) { CreFail("creature tile art missing for " + px + " px"); return null; }
            var buf = (Color32[])basePx.Clone();
            if (t.Badge.Length > 0) DrawPill(buf, px, t.Badge, t.Time, t.Kind == CreatureModel.TileKind.Here);
            if (_creSprites.Count >= MaxCreSprites) DestroyCreatureSprites();
            var tex = new Texture2D(px, px, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            tex.hideFlags = HideFlags.HideAndDontSave;
            Il2CppStructArray<Color32> il = tex.GetPixels32();
            buf.AsSpan().CopyTo(il.AsSpan());
            tex.SetPixels32(il);
            tex.Apply(false, true);
            s = Sprite.Create(tex, new Rect(0f, 0f, px, px), new Vector2(0.5f, 0.5f), 100f);
            s.hideFlags = HideFlags.HideAndDontSave;
            _creTextures.Add(tex);
            _creSprites[t.Key] = s;
            if (!_creLogged) { _creLogged = true; _services.Log.Info($"[Atlas] creature map tile composed ({px} px, badge '{t.Badge}')"); }
            return s;
        }
        catch (Exception ex) { CreFail("creature tile compose failed: " + ex.Message); return null; }
        finally { Slow("creature tile compose", t0); }
    }
    private bool _creLogged;

    private void CreFail(string why) { _creFailed = true; _services.Log.Warning("[Atlas] " + why + " — creature flags keep the game's placeholder"); }

    private static int TilePx()
    {
        float want = 42f * Screen.height / 1440f - 0.5f;
        foreach (int s in TileSizes) if (s >= want) return s;
        return TileSizes[^1];
    }

    private Color32[]? BasePixels(int idx, bool here, int px)
    {
        string key = Creatures.Ids[idx] + (here ? "-here" : "-dim");
        if (_crePixels.TryGetValue(key, out var p)) return p;
        p = DecodePixels($"Stellar.Atlas.mapc-{key}-{px}.png", px, px, out _, out _);
        _crePixels[key] = p;
        return p;
    }

    private bool EnsureGlyphs(int px)
    {
        if (_glyphPx != null) return true;
        _glyphPx = DecodePixels($"Stellar.Atlas.mapc-glyphs-{px}.png", 0, 0, out _glyphW, out _glyphH);
        if (_glyphPx == null) return false;
        _glyphCell = _glyphW / Glyphs.Length;
        for (int g = 0; g < Glyphs.Length; g++)
        {
            int x0 = int.MaxValue, x1 = -1;
            for (int x = g * _glyphCell; x < (g + 1) * _glyphCell; x++)
                for (int y = 0; y < _glyphH; y++)
                    if (_glyphPx[y * _glyphW + x].a > 24) { x0 = Math.Min(x0, x); x1 = Math.Max(x1, x); break; }
            _glyphX0[g] = x1 < 0 ? g * _glyphCell : x0;
            _glyphX1[g] = x1 < 0 ? g * _glyphCell : x1 + 1;
        }
        return true;
    }

    // Embedded PNG → managed pixels (bottom-up rows, GetPixels32 order). Event-rate (first use per size).
    private static Color32[]? DecodePixels(string resource, int wantW, int wantH, out int w, out int h)
    {
        w = h = 0;
        Texture2D? tex = null;
        try
        {
            using var st = typeof(MapMarkers).Assembly.GetManifestResourceStream(resource);
            if (st == null) return null;
            using var ms = new System.IO.MemoryStream();
            st.CopyTo(ms);
            tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!ImageConversion.LoadImage(tex, ms.ToArray())) return null;
            w = tex.width; h = tex.height;
            if ((wantW > 0 && w != wantW) || (wantH > 0 && h != wantH)) return null;
            Il2CppStructArray<Color32> il = tex.GetPixels32();
            return il.AsSpan().ToArray();
        }
        catch { return null; }
        finally { if (tex != null) UnityEngine.Object.Destroy(tex); }
    }

    // The line badge (bottom-right) or time label (bottom-centre): a rounded pill with the text in pre-drawn glyphs. Text
    // that doesn't fit the tile keeps as many whole entries as fit and ends in "+".
    private void DrawPill(Color32[] buf, int px, string text, bool time, bool here)
    {
        int padX = Math.Max(2, (px + 7) / 14), padY = Math.Max(1, px / 32);
        int maxText = px - 2 * padX - 2;
        int len = TextWidth(text, text.Length);
        int cut = text.Length;
        bool plus = false;
        while (len > maxText && cut > 0)
        {
            int dot = text.LastIndexOf('·', cut - 1);
            cut = dot < 0 ? 0 : dot;
            plus = true;
            len = TextWidth(text, cut) + 1 + Adv(12);   // kept entries + "+"
        }
        int pw = len + 2 * padX, ph = _glyphH + 2 * padY;
        int x0 = time ? (px - pw) / 2 : px - pw - 1, y0 = 0;
        float r = time ? MathF.Max(2f, ph / 3f) : ph / 2f;
        var fill = time ? new Color32(26, 20, 30, 217) : here ? new Color32(0x2f, 0x7d, 0x4e, 255) : new Color32(0x2a, 0x21, 0x18, 255);
        Color32? edge = time ? null : here ? new Color32(0x5f, 0xd3, 0x8d, 255) : new Color32(0x6b, 0x5a, 0x4a, 255);
        RoundRect(buf, px, x0, y0, pw, ph, r, fill, edge);
        var ink = time ? new Color32(0xff, 0xcc, 0x73, 255) : new Color32(255, 255, 255, 255);
        int x = x0 + padX;
        for (int i = 0; i < cut; i++) x += Blit(buf, px, Glyphs.IndexOf(text[i]), x, y0 + padY, ink);
        if (plus) Blit(buf, px, 12, x, y0 + padY, ink);
    }

    private int Adv(int g) => g < 0 ? 0 : _glyphX1[g] - _glyphX0[g] + 1;

    private int TextWidth(string s, int n)
    {
        int w = 0;
        for (int i = 0; i < n; i++) w += Adv(Glyphs.IndexOf(s[i]));
        return Math.Max(0, w - 1);
    }

    private int Blit(Color32[] buf, int px, int g, int dx, int dy, Color32 ink)
    {
        if (g < 0) return 0;
        int gx0 = _glyphX0[g], gw = _glyphX1[g] - gx0;
        for (int y = 0; y < _glyphH; y++)
            for (int x = 0; x < gw; x++)
            {
                int tx = dx + x, ty = dy + y;
                if (tx < 0 || ty < 0 || tx >= px || ty >= px) continue;
                byte a = _glyphPx![y * _glyphW + gx0 + x].a;
                if (a == 0) continue;
                Blend(ref buf[ty * px + tx], ink, a * ink.a / 255f / 255f);
            }
        return gw + 1;
    }

    // Anti-aliased rounded rectangle (signed distance per pixel centre) with an optional 1 px edge.
    private static void RoundRect(Color32[] buf, int px, int x0, int y0, int w, int h, float r, Color32 fill, Color32? edge)
    {
        for (int y = Math.Max(0, y0); y < Math.Min(px, y0 + h); y++)
            for (int x = Math.Max(0, x0); x < Math.Min(px, x0 + w); x++)
            {
                float cov = Coverage(x + 0.5f, y + 0.5f, x0, y0, w, h, r);
                if (cov <= 0f) continue;
                if (edge is { } e)
                {
                    float inner = Coverage(x + 0.5f, y + 0.5f, x0 + 1, y0 + 1, w - 2, h - 2, MathF.Max(0f, r - 1f));
                    Blend(ref buf[y * px + x], e, (cov - inner) * e.a / 255f);
                    Blend(ref buf[y * px + x], fill, inner * fill.a / 255f);
                }
                else Blend(ref buf[y * px + x], fill, cov * fill.a / 255f);
            }
    }

    private static float Coverage(float px, float py, float x0, float y0, float w, float h, float r)
    {
        if (w <= 0f || h <= 0f) return 0f;
        float cx = Math.Clamp(px, x0 + r, x0 + w - r), cy = Math.Clamp(py, y0 + r, y0 + h - r);
        float dx = px - cx, dy = py - cy;
        float d = MathF.Sqrt(dx * dx + dy * dy) - r;
        return Math.Clamp(0.5f - d, 0f, 1f);
    }

    // Source-over onto a straight-alpha pixel.
    private static void Blend(ref Color32 dst, Color32 src, float a)
    {
        if (a <= 0f) return;
        a = MathF.Min(1f, a);
        float da = dst.a / 255f, oa = a + da * (1f - a);
        if (oa <= 0f) return;
        dst.r = (byte)((src.r * a + dst.r * da * (1f - a)) / oa + 0.5f);
        dst.g = (byte)((src.g * a + dst.g * da * (1f - a)) / oa + 0.5f);
        dst.b = (byte)((src.b * a + dst.b * da * (1f - a)) / oa + 0.5f);
        dst.a = (byte)(oa * 255f + 0.5f);
    }

    private void DestroyCreatureSprites()
    {
        foreach (var s in _creSprites.Values) { try { if (s != null) UnityEngine.Object.Destroy(s); } catch { } }
        foreach (var t in _creTextures) { try { if (t != null) UnityEngine.Object.Destroy(t); } catch { } }
        _creSprites.Clear();
        _creTextures.Clear();
        _crePixels.Clear();
        _glyphPx = null;
    }
}
