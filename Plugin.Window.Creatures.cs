using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.Atlas;

// MAGICAL CREATURES (approved prototype _scratch\prototypes\atlas-creatures.tpl.html, option A + states 2a–2d):
//   SHOW row   round head chips (16 px icon + name), on = Text @14 % + 1 px Text @35 % ring + bold label; off = Muted wash,
//              faded grey icon, regular muted label. Names stay English in every locale (fixed per-chip widths, R10).
//   panel      shown only while ≥ 1 creature chip is on, above NEAREST. Caption "MAGICAL CREATURES" · "Your line N ·
//              updated X s ago". One 40 px row per creature chip that is on:
//                [30 px round head] [bold name / tag + detail] [compass word] [distance]
//              tags: green "On your line" / "Seen live" (in view: green row), amber "Line 7" / "Lines 2 · 6 · 9", muted
//              "Absent" (+ "next spawn 14:00 (in 1 h 12 m)"), blue "Seen by a player" (+ "line N · 4 min ago").
//              Click = the game's guide marker to it (any line; the line is never switched for the player). The guided
//              row = accent 30 % fill + accent outline (like NEAREST).
//   states     2a none alive → one tile with the next fixed spawn; 2b server without live status → note under the list;
//              2d offline with nothing saved → amber tile ("chests still work"); before the first answer → a muted tile.
// No outlined ghost rows: a slot whose creature chip is off is simply absent (Conditional without Else).
//
// Performance (Plugin-Performance-Rules.md §5/§7): every Func reads the model's struct array or a per-slot cached string;
// strings are rebuilt only when the row's inputs (kind, line, hp %, minute buckets, language) change.
public sealed partial class Plugin
{
    private const float CreRowH      = 40f;
    private const float CreRowGap    = 3f;
    private const int   CreIconUnits = 30;
    private const int   CreChipUnits = 16;
    private const float CreDirW      = 24f;
    private const float CreDistW     = 48f;
    private const float CreAwayW     = CreDirW + 8f + CreDistW;   // the merged map-name cell away from Asteria Plains
    private const float CreTagH      = 17f;
    private const float CreEmptyH    = 64f;
    private static readonly int[] CreSizes = { 12, 16, 20, 24, 30, 32, 40, 48, 60, 64 };
    // Bold 12 label (Arial-measured × 1.15 for the game font, SDL §6) + 35 px chip shell; English in every locale.
    private static readonly float[] CreChipW = { 127f, 119f, 123f, 131f, 134f };
    private static readonly ColorRgba InfoBlue = Rgb(0x9cc8ff);

    // ── SHOW chips ────────────────────────────────────────────────────────────────────────────────────────────────
    private HudElement CreatureChipRows() => new ColumnElement(new HudElement[]
    {
        new RowElement(new HudElement[] { CreatureChip(0), CreatureChip(1), CreatureChip(2) }, Gap: ChipGap),
        new RowElement(new HudElement[] { CreatureChip(3), CreatureChip(4) }, Gap: ChipGap),
    }, Gap: ChipGap);

    private HudElement CreatureChip(int idx)
    {
        int bit = Creatures.Bit(idx);
        float w = CreChipW[idx];
        string name = Creatures.Names[idx];
        var hover = new HoverFlag();
        float H(float a) => a + (hover.On ? ChipHoverAlpha : 0f);
        ColorRgba OnFill() => Over(TextColor() with { A = H(0.14f) }, PanelColor());
        ColorRgba OffFill() => Over(MutedColor() with { A = H(ChipOffWash) }, PanelColor());
        void Toggle() => ToggleMaskBit(bit);

        var on = FilterChipShell(() => CreHead(idx, false, CreChipUnits), DropBold(new TextElement(() => name,
                Color: () => EnsureContrast(TextColor(), OnFill(), TintedMinContrast), NoWrap: true, FontSize: 12) { Bold = true }),
            OnFill, () => Fill(TextColor() with { A = 0.35f }), hover, Toggle, CreChipUnits, w);
        var off = FilterChipShell(() => CreHead(idx, true, CreChipUnits), new TextElement(() => name, Color: () => MutedColor(),
                NoWrap: true, FontSize: 12) { RichEmoji = true }, OffFill, null, hover, Toggle, CreChipUnits, w);
        return new CellElement(new ConditionalElement(() => (_mask & bit) != 0, on, off), Width: w);
    }

    // ── panel ─────────────────────────────────────────────────────────────────────────────────────────────────────
    private enum CreView { List, Loading, Offline, NoneAlive }

    private CreView CurrentCreView()
    {
        if (!_creHasData && _cre.Count > 0 && _cre.AnyAlive) return CreView.List;   // seen live before the first answer
        if (!_creHasData) return _creOffline ? CreView.Offline : CreView.Loading;
        return _cre.AnyAlive ? CreView.List : CreView.NoneAlive;
    }

    private HudElement BuildCreaturePanel()
    {
        var slots = new HudElement[Creatures.Count];
        for (int s = 0; s < slots.Length; s++) { int slot = s; slots[s] = new ConditionalElement(() => slot < _cre.Count, CreatureRow(slot)); }
        return SectionPanel(() => _loc.T("at.section.creatures"), CreCaption,
            new ConditionalElement(() => CurrentCreView() == CreView.List, new ColumnElement(slots, Gap: CreRowGap), CreEmptyTile()),
            new ConditionalElement(() => _creHasData && _cre.Status is { Tracked: false },
                new TextElement(() => _loc.T("at.cre.untracked"), Color: () => MutedColor(), FontSize: 11)
                { RichEmoji = true, MaxLines = 3, Ellipsize = true }));
    }

    // One box (prototype .empty, 64 px): a first line (amber when offline) over a muted second line, centred.
    private HudElement CreEmptyTile() => new BubbleElement(new RowElement(new HudElement[]
    {
        new SpacerElement(0f, CreEmptyH),
        new CellElement(new ColumnElement(new HudElement[]
        {
            new TextElement(() => CreEmptyText(true), Color: () => CurrentCreView() == CreView.Offline
                    ? Readable(CautionColor(), TileColor()) : MutedColor(), Align: TextAlign.Center, FontSize: 12)
                { RichEmoji = true, MaxLines = 2, Ellipsize = true },
            new TextElement(() => CreEmptyText(false), Color: () => MutedColor(), Align: TextAlign.Center, FontSize: 12)
                { RichEmoji = true, MaxLines = 2, Ellipsize = true },
        }, Gap: 2f), Weight: 1f),
    }, Gap: 0.01f))
    {
        FillColorFn = () => Fill(TileColor()), CornerRadius = TileRadius, PaddingX = 14f, PaddingY = 0f,
    };

    private HudElement CreatureRow(int s)
    {
        ref CreatureModel.Row R() => ref _cre.Out[Math.Clamp(s, 0, Creatures.Count - 1)];
        bool Live() => R().Kind == CreatureModel.Kind.Live;
        bool Sel() => s < _cre.Count && IsCreatureGuided(in R());
        var hover = new HoverFlag();

        var tag = new BubbleElement(new RowElement(new HudElement[]
        {
            new SpacerElement(0f, CreTagH),
            DropBold(new TextElement(() => CreText(s, true), Color: () => Readable(TagColor(s), TagFill(s)), NoWrap: true,
                FontSize: 11) { Bold = true }),
        }, Gap: 0.01f))
        {
            FillColorFn = () => Fill(TagFill(s)), CornerRadius = RoundRadius(CreTagH), PaddingX = 6f, PaddingY = 0f, MaxWidth = 170f,
        };
        var content = new RowElement(new HudElement[]
        {
            new SpacerElement(0f, CreRowH),
            new CellElement(new RowElement(new HudElement[]
                { new ImageElement(() => CreHead(R().Idx, R().Kind == CreatureModel.Kind.Absent, CreIconUnits), CreIconUnits, CreIconUnits) { Live = true } },
                Gap: 0.01f, Justify: RowJustify.Center), Width: CreIconUnits),
            new CellElement(new ColumnElement(new HudElement[]
            {
                DropBold(new TextElement(() => Creatures.Names[R().Idx], Color: () => TextColor(), NoWrap: true, FontSize: 13)
                    { Bold = true, Ellipsize = true }),
                new RowElement(new HudElement[]
                {
                    tag,
                    new SpacerElement(4f, 1f),
                    new CellElement(new TextElement(() => CreText(s, false), Color: () => MutedColor(), NoWrap: true, FontSize: 11)
                        { RichEmoji = true, Ellipsize = true }, Weight: 1f),
                }, Gap: 0.01f),
            }, Gap: 1f), Weight: 1f),
            // [compass][distance] in Asteria Plains; on any other map one merged muted cell with the map name (the game's own
            // scene name, same lookup as the header) — another map's coordinates give no meaningful distance.
            new CellElement(new ConditionalElement(() => InCreatureScene, new RowElement(new HudElement[]
            {
                new CellElement(new TextElement(() => R().HasPos && !Live() && R().Dist < 1e6f ? _loc.T(DirKey(R().Dir)) : "",
                    Color: () => MutedColor(), Width: CreDirW, Align: TextAlign.Center, NoWrap: true, FontSize: 12) { RichEmoji = true }, Width: CreDirW),
                new CellElement(DropBold(new TextElement(() => R().HasPos && R().Dist < 1e6f ? DistText(R().Dist) : "",
                    Color: () => TextColor(), Width: CreDistW, Align: TextAlign.Right, NoWrap: true, FontSize: 12) { Bold = true }), Width: CreDistW),
            }, Gap: 8f),
                new TextElement(CreSceneName, Color: () => MutedColor(), Width: CreAwayW, Align: TextAlign.Right, NoWrap: true, FontSize: 11)
                    { RichEmoji = true, Ellipsize = true }), Width: CreAwayW),
        }, Gap: 8f);

        return new BubbleElement(new SelectableElement(content, OnClick: () => { if (s < _cre.Count) GuideToCreature(in R()); })
            { Bare = true, OnHover = over => hover.On = over })
        {
            FillColorFn = () =>
            {
                var f = Sel() ? SelectedFill() : Live() ? Over(SuccessColor() with { A = 0.16f }, PanelColor()) : TileColor();
                return Fill(hover.On ? HoverFill(f) : f);
            },
            OutlineColorFn = () => Sel() ? Fill(AccentColor()) : Live() ? Fill(SuccessColor()) : null,
            OutlineWidth = 1f,
            CornerRadius = TileRadius, PaddingX = 8f, PaddingY = 0f,
        };
    }

    private ColorRgba TagColor(int s) => _cre.Out[Math.Clamp(s, 0, Creatures.Count - 1)].Kind switch
    {
        CreatureModel.Kind.Live or CreatureModel.Kind.Here => SuccessColor(),
        CreatureModel.Kind.Other  => CautionColor(),
        CreatureModel.Kind.Player => InfoBlue,
        _                         => MutedColor(),
    };

    private ColorRgba TagFill(int s) => Over(TagColor(s) with { A = 0.20f }, TileColor());

    // "Asteria Plains" in the client's language (GameLua caches it per scene; cleared on a language change).
    private string CreSceneName() => _lua.SceneName(Creatures.Scene);

    // ── head art: the bitmap pre-drawn for the units × UI scale (exact, else the next larger), cached per scale ────
    private readonly byte[]?[] _creHeadPng = new byte[]?[Creatures.Count * 4];
    private float _creHeadScale = -1f;

    private byte[]? CreHead(int idx, bool dim, int units)
    {
        float scale = UiScale();
        if (scale != _creHeadScale) { _creHeadScale = scale; Array.Clear(_creHeadPng, 0, _creHeadPng.Length); }
        int slot = idx * 4 + (dim ? 1 : 0) + (units == CreIconUnits ? 2 : 0);
        if (_creHeadPng[slot] != null) return _creHeadPng[slot];
        float want = units * scale - 0.05f;
        int px = CreSizes[^1];
        foreach (int sz in CreSizes) if (sz >= want) { px = sz; break; }
        return _creHeadPng[slot] = LoadPng($"Stellar.Atlas.cre-{Creatures.Ids[idx]}{(dim ? "-dim" : "")}-{px}.png");
    }
}
