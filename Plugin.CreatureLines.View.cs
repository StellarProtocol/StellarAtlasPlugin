using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.Atlas;

// Creature lines dialog layout (user-approved 2026-11-24):
//   header  [40 px round head] bold name / muted "SEA · alive on 3 of 40 lines · you are on line 4 · updated 20 s ago"
//   LINES   caption + two right-aligned chips "Alive only" | "All <M>" (a 2-way choice; "All" only with line_count),
//           a VirtualList of 30 px rows (+2 px gap), viewport 8 rows:
//             [Line N] [spot (muted, ellipsis) + " · Seen by a player" (muted blue)] [64 px HP bar] [40 s ago]
//           your line = green 1 px outline; selected = accent wash + accent outline (wins).
//   detail  one tile: "Line 5 · Scout E · HP 2% · 399 m SE" (Asteria Plains name instead of distance on other maps)
//   footer  Primary "Guide me there" (only with a selected line that has a position) · Neutral "Close"
// Fixed geometry: the list viewport is pinned (FixedHeight), the detail and header are fixed lines, so live updates never
// move anything. Every Func reads the row array or a cached string (Plugin.CreatureLines.Text.cs).
public sealed partial class Plugin
{
    private const int   ClHeadUnits = 40;
    private const float ClRowH      = 30f;
    private const float ClPitch     = ClRowH + 2f;
    private const int   ClPool      = 10;
    private const int   ClViewRows  = 8;
    private const float ClLineW     = 56f;
    private const float ClBarW      = 64f;
    private const float ClAgeW      = 60f;
    private const float ClChipH     = 24f;
    private const float ClAliveChipW = 108f;  // label width: the longest "Alive only" across the six locales (id "Hanya yang ada")
    private const float ClAllChipW   = 64f;
    private const float ClGuideW     = 150f;
    private const float ClCloseW     = 90f;

    private readonly HoverSlots _clHover = new(ClPool);

    private HudElement BuildCreatureLinesRoot() => new ColumnElement(new HudElement[]
    {
        new RowElement(new HudElement[]
        {
            new CellElement(new RowElement(new HudElement[]
                { new ImageElement(() => CreHead(_clIdx, false, ClHeadUnits), ClHeadUnits, ClHeadUnits) { Live = true } },
                Gap: 0.01f, Justify: RowJustify.Center), Width: ClHeadUnits),
            new CellElement(new ColumnElement(new HudElement[]
            {
                new TextElement(() => Creatures.Names[_clIdx], Color: () => TextColor(), Emphasis: true, NoWrap: true) { Ellipsize = true },
                FixedLine(new TextElement(ClStatusText, Color: () => MutedColor(), NoWrap: true, FontSize: 12)
                    { RichEmoji = true, Ellipsize = true }, StatusLineH),
            }, Gap: 2f), Weight: 1f),
        }, Gap: 10f),
        BuildClLinesPanel(),
        new BubbleElement(FixedLine(new TextElement(ClDetailText, Color: () => _clSel > 0 ? TextColor() : MutedColor(), NoWrap: true,
            FontSize: 12) { RichEmoji = true, Ellipsize = true }, 26f))
        {
            FillColorFn = () => Fill(TileColor()), CornerRadius = TileRadius, PaddingX = 10f, PaddingY = 0f,
        },
        new RowElement(new HudElement[]
        {
            new SpacerElement(0f, PillHeight),
            new CellElement(PrimaryButton(() => _loc.T("at.cl.guide"), GuideToSelectedLine, ClGuideW, enabled: () => ClCanGuide),
                Width: ClGuideW + 2f * PillPadX),
            new SpacerElement(0f),
            new CellElement(NeutralButton(() => _loc.T("at.cl.close"), CloseCreatureLines, ClCloseW), Width: ClCloseW + 2f * PillPadX),
        }, Gap: 8f),
    }, Gap: 8f);

    private HudElement BuildClLinesPanel()
    {
        var pool = new HudElement[ClPool];
        for (int s = 0; s < ClPool; s++) pool[s] = ListSlot(ClRowTile(s));
        string? capSrc = null, capUpper = "";
        string Cap()
        {
            string t = _loc.T("at.cl.lines");
            if (!ReferenceEquals(t, capSrc)) { capSrc = t; capUpper = t.ToUpperInvariant(); }
            return capUpper;
        }
        return new BubbleElement(new ColumnElement(new HudElement[]
        {
            new RowElement(new HudElement[]
            {
                new SpacerElement(0f, ClChipH),
                new TextElement(Cap, Color: () => TextColor(), NoWrap: true, FontSize: 12) { Bold = true },
                new SpacerElement(0f),
                ClChip(() => _loc.T("at.cl.aliveOnly"), () => !ClAllMode, () => SetClMode(false), ClAliveChipW),
                new ConditionalElement(() => ClLineCount > 0,
                    ClChip(ClAllLabel, () => ClAllMode, () => SetClMode(true), ClAllChipW)),
            }, Gap: 6f),
            new VirtualListElement(() => _clN, ClPitch, pool, first => _clFirst = first, ClViewRows * ClPitch - 2f)
            {
                FixedHeight = true,
                ResetScroll = ClScrollReset,
            },
        }, Gap: 6f))
        {
            FillColorFn = () => Fill(PanelColor()), CornerRadius = 6f, PaddingX = 10f, PaddingY = 8f,
        };
    }

    // Back to the top when the shown set is a different one (creature / mode), not on a live refresh.
    private int _clScrollKey = -1;
    private bool ClScrollReset()
    {
        int key = _clIdx * 2 + (ClAllMode ? 1 : 0);
        if (key == _clScrollKey) return false;
        _clScrollKey = key;
        return true;
    }

    // A 2-way choice chip in the caption (house on/off chip look, like SHOW): on = Text @14 % + Text @35 % ring + bold
    // label; off = Muted @12 %, regular muted label. Bold can't toggle live → two complete variants under a Conditional.
    private HudElement ClChip(Func<string> label, Func<bool> on, Action click, float labelW)
    {
        var hover = new HoverFlag();
        float w = labelW + 18f;
        ColorRgba OnFill() => Over(TextColor() with { A = 0.14f + (hover.On ? ChipHoverAlpha : 0f) }, PanelColor());
        ColorRgba OffFill() => Over(MutedColor() with { A = ChipOffWash + (hover.On ? ChipHoverAlpha : 0f) }, PanelColor());
        HudElement Shell(HudElement text, Func<ColorRgba> fill, Func<ColorRgba?>? ring) =>
            new BubbleElement(new SelectableElement(new RowElement(new HudElement[]
                {
                    new SpacerElement(9f, ClChipH), text, new SpacerElement(9f, ClChipH),
                }, Gap: 0.01f, Justify: RowJustify.Center), OnClick: click) { Bare = true, OnHover = o => hover.On = o })
            {
                FillColorFn = () => Fill(fill()), OutlineColorFn = ring, OutlineWidth = ring != null ? 1f : 0f,
                CornerRadius = RoundRadius(ClChipH), PaddingX = 0f, PaddingY = 0f, MaxWidth = w,
            };
        var onV = Shell(DropBold(new TextElement(label, Color: () => EnsureContrast(TextColor(), OnFill(), TintedMinContrast),
            Width: labelW, Align: TextAlign.Center, NoWrap: true, FontSize: 12) { Bold = true, Ellipsize = true }),
            OnFill, () => Fill(TextColor() with { A = 0.35f }));
        var offV = Shell(new TextElement(label, Color: () => MutedColor(), Width: labelW, Align: TextAlign.Center, NoWrap: true,
            FontSize: 12) { RichEmoji = true, Ellipsize = true }, OffFill, null);
        return new CellElement(new ConditionalElement(on, onV, offV), Width: w);
    }

    private HudElement ClRowTile(int s)
    {
        int I() => _clFirst + s;
        bool Has() => I() < _clN;
        ref ClRow R() => ref _clRows[Math.Clamp(I(), 0, _clRows.Length - 1)];
        bool Sel() => Has() && _clSel > 0 && R().Line == _clSel;
        bool Mine() => Has() && CreLine > 0 && R().Line == CreLine;
        bool HasHp() => Has() && R().Kind == ClKind.Alive && R().MaxHp > 0;
        var bar = new BarElement(() => HasHp() ? Math.Clamp((float)R().Hp / R().MaxHp, 0f, 1f) : 0f, SuccessColor())
            { Width = ClBarW, Height = 6f, Style = BarStyle.Modern, FillColorFn = () => Fill(SuccessColor()) };
        var content = new RowElement(new HudElement[]
        {
            new SpacerElement(0f, ClRowH),
            new CellElement(DropBold(new TextElement(() => Has() ? ClLineText(R().Line) : "", Color: () => TextColor(),
                NoWrap: true, FontSize: 12) { Bold = true }), Width: ClLineW),
            new CellElement(new RowElement(new HudElement[]
            {
                new CellElement(new TextElement(() => ClSpotText(s), Color: () => MutedColor(), NoWrap: true, FontSize: 11)
                    { RichEmoji = true, Ellipsize = true }, Weight: 1f),
                new TextElement(() => ClPlayerSuffix(s), Color: () => Readable(InfoBlue, TileColor()), NoWrap: true, FontSize: 11)
                    { RichEmoji = true },
            }, Gap: 0.01f), Weight: 1f),
            new CellElement(new ConditionalElement(HasHp, bar, new SpacerElement(ClBarW, 6f)), Width: ClBarW),
            new CellElement(new TextElement(() => ClAgeText(s), Color: () => MutedColor(), Width: ClAgeW, Align: TextAlign.Right,
                NoWrap: true, FontSize: 11) { RichEmoji = true }, Width: ClAgeW),
        }, Gap: 8f);
        return new BubbleElement(new SelectableElement(content, OnClick: () => { if (Has()) _clSel = R().Line; })
            { Bare = true, OnHover = over => _clHover.Set(s, Has() ? R().Line : -1, over) })
        {
            FillColorFn = () =>
            {
                var f = Sel() ? SelectedFill() : TileColor();
                return Fill(_clHover.Get(s, Has() ? R().Line : -1) ? HoverFill(f) : f);
            },
            OutlineColorFn = () => Sel() ? Fill(AccentColor()) : Mine() ? Fill(SuccessColor()) : null,
            OutlineWidth = 1f,
            CornerRadius = TileRadius, PaddingX = 8f, PaddingY = 0f,
        };
    }
}
