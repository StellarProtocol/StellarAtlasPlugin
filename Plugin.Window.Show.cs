using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.Atlas;

// SHOW panel: 24 px filter chips with a coloured dot (prototype section 1). ON = bold label tinted with the chip colour on
// a 20 % wash of it; OFF = regular muted label on the muted wash (the dot keeps its colour). Persisted per character
// (Plugin.cs ToggleMaskBit). "More…" expands the remaining server groups as more chips (all OFF by default).
//
// Toolkit notes: there is no wrapping/flow layout, so the chips sit in a fixed 4-column grid (2 rows; More… adds 3) with
// FIXED cell widths — bold (on) and regular (off) labels differ in width, so a hugging chip would push its neighbours
// on every toggle (R10). Bold can't toggle live → each chip is two complete variants swapped by a Conditional around the
// WHOLE chip inside its fixed cell (SDL §6); MaxWidth pins the bubble (a bubble under a Conditional otherwise caps at 100).
public sealed partial class Plugin
{
    private const float FilterChipH = 24f;
    private const float ChipGap     = 6f;
    private const float ChipWash    = 0.20f;   // prototype: rgba(colour, .20)
    // Column widths of the chip grid: 396 px panel content = 4 cells + 3 gaps of 6. The 4th column holds "Other chests".
    private static readonly float[] ChipCols = { 82f, 90f, 90f, 116f };

    private HudElement BuildShowPanel() => SectionPanel(() => _loc.T("at.section.show"), () => _loc.T("at.hint.show"),
        new ColumnElement(new HudElement[]
        {
            ChipRow(FilterChip(Chip.Common), FilterChip(Chip.Exquisite), FilterChip(Chip.Luxurious), FilterChip(Chip.OtherChests)),
            ChipRow(FilterChip(Chip.Herbs), FilterChip(Chip.Ore), FilterChip(Chip.Fishing), MoreChip()),
            new ConditionalElement(() => MoreOpen, new ColumnElement(new HudElement[]
            {
                ChipRow(FilterChip(Chip.Npc), FilterChip(Chip.Monster), FilterChip(Chip.Reading), FilterChip(Chip.Traversal)),
                ChipRow(FilterChip(Chip.Teleport), FilterChip(Chip.Activity), FilterChip(Chip.Engram), FilterChip(Chip.Resonance)),
                ChipRow(FilterChip(Chip.Other), null, null, null),
            }, Gap: ChipGap)),
        }, Gap: ChipGap));

    private static HudElement ChipRow(HudElement? a, HudElement? b, HudElement? c, HudElement? d)
    {
        var cells = new HudElement[] { a!, b!, c!, d! };
        for (int i = 0; i < 4; i++)
            cells[i] = new CellElement(cells[i] ?? new SpacerElement(ChipCols[i], FilterChipH), Width: ChipCols[i]);
        return new RowElement(cells, Gap: ChipGap);
    }

    private HudElement FilterChip(Chip chip)
    {
        int col = Array.IndexOf(AtlasGroups.MainChips, chip);
        if (col < 0) col = Array.IndexOf(AtlasGroups.MoreChips, chip);
        float w = ChipCols[col % 4];
        var color = AtlasGroups.Color(chip);
        string key = AtlasGroups.LabelKey(chip);
        return ChipVariants(() => _loc.T(key), () => color, () => ChipOn(chip), () => ToggleMaskBit(1 << (int)chip), w);
    }

    private HudElement MoreChip() => ChipVariants(() => _loc.T("at.chip.more"), () => AtlasGroups.MoreDot,
        () => MoreOpen, () => ToggleMaskBit(AtlasGroups.MoreOpenBit), ChipCols[3]);

    private HudElement ChipVariants(Func<string> label, Func<ColorRgba> color, Func<bool> on, Action toggle, float width)
    {
        var hover = new HoverFlag();
        float H(float a) => a + (hover.On ? ChipHoverAlpha : 0f);
        ColorRgba OnFill() => Over(color() with { A = H(ChipWash) }, PanelColor());
        ColorRgba OffFill() => Over(MutedColor() with { A = H(ChipOffAlpha) }, PanelColor());
        return new CellElement(new ConditionalElement(on,
            FilterChipShell(color, DropBold(new TextElement(label, Color: () => EnsureContrast(color(), OnFill(), TintedMinContrast),
                NoWrap: true, FontSize: 12) { Bold = true }), OnFill, hover, toggle, width),
            FilterChipShell(color, new TextElement(label, Color: () => MutedColor(), NoWrap: true, FontSize: 12) { RichEmoji = true },
                OffFill, hover, toggle, width)),
            Width: width);
    }

    // [7] [8 px dot] [5] [label] [7], centred in the fixed-width chip, 24 px tall (strut). Round = RoundRadius(24) = 11.
    private HudElement FilterChipShell(Func<ColorRgba> dot, HudElement label, Func<ColorRgba> fill, HoverFlag hover,
        Action onClick, float width)
        => new BubbleElement(new SelectableElement(new RowElement(new HudElement[]
            {
                new SpacerElement(7f, FilterChipH),
                StatusDot(dot),
                new SpacerElement(5f, 1f),
                label,
                new SpacerElement(7f, FilterChipH),
            }, Gap: 0.01f, Justify: RowJustify.Center),
            OnClick: onClick) { Bare = true, OnHover = over => hover.On = over })
        {
            FillColorFn = () => Fill(fill()), CornerRadius = RoundRadius(FilterChipH), PaddingX = 0f, PaddingY = 0f,
            MaxWidth = width,
        };
}
