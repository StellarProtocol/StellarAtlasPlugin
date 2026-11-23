using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.Atlas;

// NEAREST panel: exactly 8 reserved rows (R14), 26 px tiles + 2 px gap (R15), nearest first —
//   [dot] [name ……] [source] [compass word] [58 m]
// source = "Seen live" (green) / "Map data" (muted) / "Unverified" (amber). Click = guide there; the guided row = accent
// 30 % fill + 1 px accent outline. Unused rows are invisible (the list just ends); nothing known = one full-height box
// "No chests known here yet. Ones you find are added." (2c); a chest just opened = a green-tinted "Opened" row on top
// for a moment (2d). Compass words, not arrow glyphs ("→" draws blank, SDL §6).
//
// Fixed slots (no VirtualList: the list never scrolls). Each slot is a Conditional over COMPLETE tiles (flash / row /
// spacer), because the "Opened" label is bold and bold can't toggle live (SDL §6). The row data is the
// model's struct array — the Funcs index it; strings come from caches (names: GameLua, distances: _distText).
public sealed partial class Plugin
{
    private const float NameFont = 13f;
    private const float SrcColW  = 70f;
    private const float DirColW  = 28f;   // two CJK glyphs (北東 / 북동) at 12 px
    private const float DistColW = 44f;

    private readonly HoverSlots _rowHover = new(NearestModel.Rows);
    private readonly Dictionary<int, string> _distText = new();

    private bool FlashOn => _flash.Active;

    // Display slot → model row (slot 0 is the flash while it shows).
    private int RowIndex(int slot) => FlashOn ? slot - 1 : slot;
    private bool HasRow(int slot) { int i = RowIndex(slot); return i >= 0 && i < _model.Count; }
    private ref NearestModel.Row RowAt(int slot) => ref _model.Out[Math.Clamp(RowIndex(slot), 0, NearestModel.Rows - 1)];

    // Empty states (user-approved 2026-11-23: no outlined ghost rows): "Nothing shown yet" (no chip on) and "No chests known
    // here yet…" (nothing to list) are ONE Tile-coloured box filling the whole list area (8 rows incl. gaps, R14), text
    // centred. With fewer than 8 results the unused slots are invisible spacers: the list just ends, the height stays.
    private static readonly float ListAreaH = NearestModel.Rows * ListRowPitch - ListRowGap;

    private bool NothingShown => (_mask & AtlasGroups.ShownBits) == 0;
    private bool ListEmpty => _model.Count == 0 && !FlashOn;

    private HudElement BuildNearestPanel()
    {
        var rows = new HudElement[NearestModel.Rows];
        for (int s = 0; s < rows.Length; s++) rows[s] = NearestSlot(s);
        return SectionPanel(() => _loc.T("at.section.nearest"), () => _loc.T("at.hint.nearest"),
            new ConditionalElement(() => NothingShown,
                FullTile(() => _loc.T("at.none.title"), () => _loc.T("at.none.body")),
                new ConditionalElement(() => ListEmpty,
                    FullTile(null, () => _loc.T("at.empty")),
                    new ColumnElement(rows, Gap: ListRowGap))));
    }

    // One box the height of the whole list: optional bold title over a muted line (≤ 2 lines), centred both ways.
    private HudElement FullTile(Func<string>? title, Func<string> body)
    {
        var lines = new List<HudElement>(2);
        if (title != null)
            lines.Add(DropBold(new TextElement(title, Color: () => TextColor(), Align: TextAlign.Center,
                NoWrap: true, FontSize: 13) { Bold = true, Ellipsize = true }));
        lines.Add(new TextElement(body, Color: () => MutedColor(), Align: TextAlign.Center,
            FontSize: 12) { RichEmoji = true, MaxLines = 2, Ellipsize = true });
        return new BubbleElement(new RowElement(new HudElement[]
        {
            new SpacerElement(0f, ListAreaH),
            new CellElement(new ColumnElement(lines, Gap: 4f), Weight: 1f),
        }, Gap: 0.01f))
        {
            FillColorFn = () => Fill(TileColor()), CornerRadius = TileRadius, PaddingX = 16f, PaddingY = 0f,
        };
    }

    private HudElement NearestSlot(int slot)
    {
        HudElement rest = new ConditionalElement(() => HasRow(slot), RowTile(slot), new SpacerElement(0f, ListTileHeight));
        return slot == 0 ? new ConditionalElement(() => FlashOn, FlashTile(), rest) : rest;
    }

    // ── tiles ─────────────────────────────────────────────────────────────────────────────────────────────────────
    private HudElement RowTile(int slot)
    {
        long Id() { ref var r = ref RowAt(slot); return r.Uuid != 0 ? r.Uuid : -r.ServerId; }
        bool Sel() => HasRow(slot) && IsGuided(in RowAt(slot));
        var content = new RowElement(new HudElement[]
        {
            new SpacerElement(0f, ListTileHeight),
            StatusDot(() => AtlasGroups.Color(RowAt(slot).Chip)),
            new CellElement(new TextElement(() => RowName(slot), Color: () => TextColor(), NoWrap: true, FontSize: (int)NameFont)
                { RichEmoji = true, Ellipsize = true }, Weight: 1f),
            new CellElement(new TextElement(() => SourceText(RowAt(slot).Src), Color: () => SourceColor(RowAt(slot).Src),
                Width: SrcColW, Align: TextAlign.Right, NoWrap: true, FontSize: 11) { RichEmoji = true, Ellipsize = true }, Width: SrcColW),
            new CellElement(new TextElement(() => _loc.T(DirKey(RowAt(slot).Dir)), Color: () => MutedColor(),
                Width: DirColW, Align: TextAlign.Center, NoWrap: true, FontSize: 12) { RichEmoji = true }, Width: DirColW),
            new CellElement(DropBold(new TextElement(() => DistText(RowAt(slot).Dist), Color: () => TextColor(),
                Width: DistColW, Align: TextAlign.Right, NoWrap: true, FontSize: 12) { Bold = true }), Width: DistColW),
        }, Gap: 8f);

        return new BubbleElement(new SelectableElement(content, OnClick: () => { if (HasRow(slot)) GuideTo(in RowAt(slot)); })
            { Bare = true, OnHover = over => _rowHover.Set(slot, Id(), over) })
        {
            FillColorFn = () =>
            {
                bool hov = _rowHover.Get(slot, Id());
                if (Sel()) return Fill(hov ? HoverFill(SelectedFill()) : SelectedFill());
                return Fill(hov ? HoverTileColor() : TileColor());
            },
            OutlineColorFn = () => Sel() ? Fill(AccentColor()) : null,
            OutlineWidth = 1f,
            CornerRadius = TileRadius, PaddingX = 8f, PaddingY = 0f,
        };
    }

    // 2d: green-tinted row with bold green "Opened", no compass, "0 m".
    private HudElement FlashTile() => new BubbleElement(new RowElement(new HudElement[]
    {
        new SpacerElement(0f, ListTileHeight),
        StatusDot(() => AtlasGroups.Color(_flash.Chip)),
        new CellElement(new TextElement(() => _flash.Label ?? "", Color: () => TextColor(), NoWrap: true, FontSize: (int)NameFont)
            { RichEmoji = true, Ellipsize = true }, Weight: 1f),
        new CellElement(DropBold(new TextElement(() => _loc.T("at.src.opened"), Color: () => Readable(SuccessColor(), FlashFill()),
            Width: SrcColW, Align: TextAlign.Right, NoWrap: true, FontSize: 11) { Bold = true }), Width: SrcColW),
        new CellElement(new SpacerElement(DirColW, 1f), Width: DirColW),
        new CellElement(DropBold(new TextElement(() => DistText(0f), Color: () => TextColor(),
            Width: DistColW, Align: TextAlign.Right, NoWrap: true, FontSize: 12) { Bold = true }), Width: DistColW),
    }, Gap: 8f))
    {
        FillColorFn = () => Fill(FlashFill()), OutlineColorFn = () => Fill(SuccessColor()), OutlineWidth = 1f,
        CornerRadius = TileRadius, PaddingX = 8f, PaddingY = 0f,
    };

    private ColorRgba FlashFill() => Over(SuccessColor() with { A = 0.18f }, PanelColor());

    // ── cell text ─────────────────────────────────────────────────────────────────────────────────────────────────
    private string RowName(int slot)
    {
        ref var r = ref RowAt(slot);
        return string.IsNullOrEmpty(r.Label) ? _loc.T(AtlasGroups.LabelKey(r.Chip)) : r.Label;
    }

    private string SourceText(NearestModel.Src s) => _loc.T(s switch
    {
        NearestModel.Src.Live       => "at.src.live",
        NearestModel.Src.Unverified => "at.src.unverified",
        _                           => "at.src.map",
    });

    private ColorRgba SourceColor(NearestModel.Src s) => s switch
    {
        NearestModel.Src.Live       => Readable(SuccessColor(), TileColor()),
        NearestModel.Src.Unverified => Readable(CautionColor(), TileColor()),
        _                           => MutedColor(),
    };

    private static readonly string[] DirKeys =
        { "at.dir.n", "at.dir.ne", "at.dir.e", "at.dir.se", "at.dir.s", "at.dir.sw", "at.dir.w", "at.dir.nw" };
    private static string DirKey(int d) => DirKeys[d & 7];

    // "58 m" — cached per whole metre (cleared on a language change), so the polled Func never allocates after warm-up.
    private string DistText(float d)
    {
        int m = (int)MathF.Round(d);
        if (_distText.TryGetValue(m, out var s)) return s;
        s = _loc.TFormat("at.dist", m);
        _distText[m] = s;
        return s;
    }
}
