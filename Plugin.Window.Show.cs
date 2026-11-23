using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.Atlas;

// SHOW panel (redesign, user-approved option A 2026-11-23): caption "SHOW" + hint "Click to turn on or off", then two
// labelled sub-rows, each a fixed 4-column grid of 24 px chips of equal width:
//   CHESTS                    "<N> on this map"   Common · Epic · Legendary · Other
//   GATHERING & COLLECTIBLES                      Herbs · Ore · Fishing · Books
// ON  = fill colour @26 % + 1 px outline colour @60 %, bold label in a lightened colour, SOLID dot.
// OFF = fill Muted @12 %, regular Muted label, HOLLOW ring.
// The dots are baked PNGs (tools/icons/gen-dot-icons.py, per size) — the font has no reliable dot/ring glyph. Persisted per
// character (Plugin.cs ToggleMaskBit); the same mask drives NEAREST and the game-map markers.
//
// Toolkit notes: no wrapping layout → fixed grid; FIXED equal cell widths so the bold (on) and regular (off) labels never
// push a neighbour (R10). Bold can't toggle live → each chip is two complete variants swapped by a Conditional around the
// WHOLE chip inside its fixed cell (SDL §6); MaxWidth pins the bubble (a bubble under a Conditional otherwise caps at 100).
public sealed partial class Plugin
{
    private const float FilterChipH = 24f;
    private const float ChipGap     = 6f;
    private const float ChipCellW   = 94f;     // (396 px panel content − 3 × 6 gap) / 4
    private const float ChipOnWash  = 0.26f;
    private const float ChipOnRing  = 0.60f;
    private const float ChipOffWash = 0.12f;
    private const int   DotUnits    = 8;
    private static readonly int[] DotSizes = { 6, 8, 10, 12, 16 };

    private HudElement BuildShowPanel() => SectionPanel(() => _loc.T("at.section.show"), () => _loc.T("at.hint.show"),
        SubCaption(() => _loc.T("at.sub.chests"), ChestCountText),
        ChipRow(AtlasGroups.ChestChips),
        SubCaption(() => _loc.T("at.sub.gathering"), () => ""),
        ChipRow(AtlasGroups.GatherChips));

    // Bold 11 muted UPPERCASE sub-caption + right-aligned muted hint, pinned to 18 px. ToUpper runs only when the
    // language changes (cached), never per refresh.
    private HudElement SubCaption(Func<string> caption, Func<string> hint)
    {
        string? src = null, upper = "";
        string Cap()
        {
            string s = caption();
            if (!ReferenceEquals(s, src)) { src = s; upper = s.ToUpperInvariant(); }
            return upper;
        }
        return new RowElement(new HudElement[]
        {
            new SpacerElement(0f, 18f),
            new TextElement(Cap, Color: () => MutedColor(), NoWrap: true, FontSize: 11) { Bold = true },
            new SpacerElement(0f),   // flexible: pushes the hint to the right edge
            new TextElement(hint, Color: () => MutedColor(), NoWrap: true, FontSize: 11) { RichEmoji = true },
        }, Gap: 6f);
    }

    // "<N> on this map": the model's chest count (after opened / not-here filtering), re-formatted only on change.
    private int _ccCount = -1;
    private string? _ccLang;
    private string _ccText = "";

    private string ChestCountText()
    {
        if (_scene <= 0 || !_fetchedOnce) return "";
        int n = _model.ChestsOnMap;
        string lang = _loc.Language;
        if (n == _ccCount && ReferenceEquals(lang, _ccLang)) return _ccText;
        _ccCount = n; _ccLang = lang;
        _ccText = _loc.TFormat("at.sub.chestsCount", n);
        return _ccText;
    }

    private HudElement ChipRow(Chip[] chips)
    {
        var cells = new HudElement[chips.Length];
        for (int i = 0; i < chips.Length; i++) cells[i] = FilterChip(chips[i]);
        return new RowElement(cells, Gap: ChipGap);
    }

    private HudElement FilterChip(Chip chip)
    {
        var color = AtlasGroups.Color(chip);
        string key = AtlasGroups.LabelKey(chip);
        int bit = 1 << (int)chip;
        var hover = new HoverFlag();
        float H(float a) => a + (hover.On ? ChipHoverAlpha : 0f);
        ColorRgba OnFill() => Over(color with { A = H(ChipOnWash) }, PanelColor());
        ColorRgba OffFill() => Over(MutedColor() with { A = H(ChipOffWash) }, PanelColor());
        ColorRgba OnLabel() => EnsureContrast(Mix(color, White, 0.35f), OnFill(), TintedMinContrast);
        void Toggle() => ToggleMaskBit(bit);

        var on = FilterChipShell(() => DotPng(chip, true), DropBold(new TextElement(() => _loc.T(key), Color: () => OnLabel(),
                NoWrap: true, FontSize: 12) { Bold = true }), OnFill, () => Fill(color with { A = ChipOnRing }), hover, Toggle);
        var off = FilterChipShell(() => DotPng(chip, false), new TextElement(() => _loc.T(key), Color: () => MutedColor(),
                NoWrap: true, FontSize: 12) { RichEmoji = true }, OffFill, null, hover, Toggle);
        return new CellElement(new ConditionalElement(() => ChipOn(chip), on, off), Width: ChipCellW);
    }

    // [7] [8 px dot image] [5] [label] [7], centred in the fixed-width chip, 24 px tall (strut). Round = RoundRadius(24).
    private HudElement FilterChipShell(Func<byte[]?> dot, HudElement label, Func<ColorRgba> fill, Func<ColorRgba?>? outline,
        HoverFlag hover, Action onClick)
        => new BubbleElement(new SelectableElement(new RowElement(new HudElement[]
            {
                new SpacerElement(7f, FilterChipH),
                new CellElement(new RowElement(new HudElement[] { new ImageElement(dot, DotUnits, DotUnits) },
                    Gap: 0.01f, Justify: RowJustify.Center), Width: DotUnits),
                new SpacerElement(5f, 1f),
                label,
                new SpacerElement(7f, FilterChipH),
            }, Gap: 0.01f, Justify: RowJustify.Center),
            OnClick: onClick) { Bare = true, OnHover = over => hover.On = over })
        {
            FillColorFn = () => Fill(fill()),
            OutlineColorFn = outline,   // null Func / null value = no ring (OFF chip)
            OutlineWidth = outline != null ? 1f : 0f,
            CornerRadius = RoundRadius(FilterChipH), PaddingX = 0f, PaddingY = 0f,
            MaxWidth = ChipCellW,
        };

    // ── dot art: the bitmap pre-drawn for 8 units × the current UI scale (exact, else next larger) ─────────────────
    private readonly byte[]?[] _dotOn = new byte[]?[AtlasGroups.ChipCount];
    private byte[]? _dotRing;
    private float _dotScale = -1f;

    private byte[]? DotPng(Chip chip, bool on)
    {
        float scale = UiScale();
        if (scale != _dotScale)
        {
            _dotScale = scale;
            Array.Clear(_dotOn, 0, _dotOn.Length);
            _dotRing = null;
        }
        int px = PickDot(scale);
        if (!on) return _dotRing ??= LoadPng($"Stellar.Atlas.ring-{px}.png");
        int c = (int)chip;
        return _dotOn[c] ??= LoadPng($"Stellar.Atlas.dot-{AtlasGroups.MapIconStem(chip)}-{px}.png");
    }

    private static int PickDot(float scale)
    {
        float want = DotUnits * scale - 0.05f;
        foreach (int s in DotSizes) if (s >= want) return s;
        return DotSizes[^1];
    }

    private static byte[]? LoadPng(string resource)
    {
        try
        {
            using var s = typeof(Plugin).Assembly.GetManifestResourceStream(resource);
            if (s == null) return null;
            using var ms = new System.IO.MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
        catch { return null; }
    }
}
