using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.Atlas;

// The Atlas window, house style, built to the approved prototype (_scratch\prototypes\atlas.html, option A):
//   ToolHeader   [accent map tile] Atlas / ● "<map> · N chests on this map · M nearby" (green dot = synced; amber
//                "Offline · showing the copy from 2 h ago" when the server can't be reached)
//   SHOW         filter chips (Plugin.Window.Show.cs)
//   NEAREST      8 reserved rows (Plugin.Window.Nearest.cs)
//   footer       Stop guiding (Neutral) · Share chip · muted sharing hint
// First open (2a): the same header + a notice tile + OK, share (Primary) / Don't share (Neutral), shown until answered.
//
// Performance (Plugin-Performance-Rules.md §7): every Func is a field read or a cached string; strings are rebuilt only
// when what they show changes (scene, counts, sync state, age bucket, language), never per refresh.
public sealed partial class Plugin
{
    // 440, not the prototype's ~420: in-game bold 12 Latin runs ~7.5 px/char (SDL §6), and the four chest chips of one
    // SHOW row ("Other chests" the longest) did not fit 420 − 24 window pad − 20 panel pad (SDL R18: widen, don't cram).
    private const float WindowWidth    = 440f;
    private const float StatusLineH    = 18f;
    private const float StopPillLabelW = 106f;   // "Stop guiding" + every locale; pill = 130 px like the prototype

    private HudElement BuildRoot() => new ColumnElement(new HudElement[]
    {
        ToolHeader(HeaderIcon, () => _loc.T("at.title"), BuildHeaderStatus()),
        new ConditionalElement(() => !_noticeSeen, BuildNotice(), BuildMain()),
    }, Gap: 8f);

    private HudElement BuildMain() => new ColumnElement(new HudElement[]
    {
        BuildShowPanel(),
        BuildNearestPanel(),
        BuildFooter(),
    }, Gap: 8f);

    // ── header status ─────────────────────────────────────────────────────────────────────────────────────────────
    private enum HeadKind { Idle, Loading, Synced, Offline, OfflineNone }

    private HeadKind CurrentHead()
    {
        if (_scene <= 0) return HeadKind.Idle;
        if (!_fetchedOnce) return HeadKind.Loading;
        return _fetchState switch
        {
            AtlasClient.FetchState.Synced  => HeadKind.Synced,
            AtlasClient.FetchState.Offline => HeadKind.Offline,
            _                              => HeadKind.OfflineNone,
        };
    }

    private HudElement BuildHeaderStatus() => FixedLine(new RowElement(new HudElement[]
    {
        new SpacerElement(0f, StatusLineH),
        StatusDot(HeadDotColor),
        new SpacerElement(5f, 1f),
        new CellElement(new TextElement(HeadText, Color: () => HeadTextColor(), NoWrap: true, FontSize: 12)
            { RichEmoji = true, Ellipsize = true }, Weight: 1f),
    }, Gap: 0.01f), StatusLineH);

    private ColorRgba HeadDotColor() => CurrentHead() switch
    {
        HeadKind.Synced                         => SuccessColor(),
        HeadKind.Offline or HeadKind.OfflineNone => CautionColor(),
        HeadKind.Idle                           => Transparent,
        _                                       => MutedColor(),
    };

    private ColorRgba HeadTextColor() => CurrentHead() is HeadKind.Offline or HeadKind.OfflineNone
        ? Readable(CautionColor(), Menu.MenuBackground) : MutedColor();

    // Rebuilt only when an input changes. _statusKey = null forces a rebuild (scene / fetch / language changes).
    private string? _statusKey;
    private string _statusText = "", _sceneName = "";
    private int _sceneNameFor, _stChests = -1, _stNearby = -1, _stAge = -1;
    private HeadKind _stKind;

    private string HeadText()
    {
        var kind = CurrentHead();
        if (kind == HeadKind.Idle) return "";
        if (_sceneNameFor != _scene) { _sceneName = _lua.SceneName(_scene); _sceneNameFor = _sceneName.Length > 0 ? _scene : 0; _statusKey = null; }
        int chests = _model.ChestsOnMap, nearby = _model.Nearby, age = kind == HeadKind.Offline ? AgeBucket() : -1;
        if (_statusKey != null && kind == _stKind && chests == _stChests && nearby == _stNearby && age == _stAge) return _statusText;
        _stKind = kind; _stChests = chests; _stNearby = nearby; _stAge = age; _statusKey = "ok";
        _statusText = kind switch
        {
            HeadKind.Offline     => _loc.TFormat("at.status.offline", AgeText(age)),
            HeadKind.OfflineNone => _loc.T("at.status.offlineNone"),
            _                    => _loc.TFormat("at.status.synced", _sceneName, chests, nearby),
        };
        return _statusText;
    }

    // Age of the offline copy, bucketed so the line changes at most once a minute: minutes < 60, then hours, then days
    // (encoded: m, 1000 + h, 2000 + d).
    private int AgeBucket()
    {
        if (_fetchedUnix <= 0) return 0;
        long s = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - _fetchedUnix;
        if (s < 3600) return (int)Math.Max(1, s / 60);
        if (s < 48 * 3600) return 1000 + (int)(s / 3600);
        return 2000 + (int)Math.Min(999, s / 86400);
    }

    private string AgeText(int b) => b >= 2000 ? _loc.TFormat("at.age.d", b - 2000)
                                   : b >= 1000 ? _loc.TFormat("at.age.h", b - 1000)
                                   : _loc.TFormat("at.age.min", Math.Max(1, b));

    // ── footer ────────────────────────────────────────────────────────────────────────────────────────────────────
    // [Stop guiding] [Share] [muted hint, up to two lines]. Stop guiding is recessed while nothing is guided (rule 22).
    private HudElement BuildFooter() => new RowElement(new HudElement[]
    {
        new SpacerElement(0f, PillHeight),
        new CellElement(NeutralButton(() => _loc.T("at.stopGuiding"), StopGuidingClicked, StopPillLabelW,
            enabled: () => _guide.Active), Width: StopPillLabelW + 2f * PillPadX),
        ToggleChip(() => _loc.T("at.share"), () => _share, () => SetShare(!_share)),
        new CellElement(new TextElement(() => _loc.T(_share ? "at.hint.shareOn" : "at.hint.shareOff"),
            Color: () => MutedColor(), FontSize: 11) { RichEmoji = true, MaxLines = 2, Ellipsize = true }, Weight: 1f),
    }, Gap: 8f);

    // ── first open (2a) ───────────────────────────────────────────────────────────────────────────────────────────
    private const float NoticePillLabelW = 150f;

    private HudElement BuildNotice() => new ColumnElement(new HudElement[]
    {
        new BubbleElement(new ColumnElement(new HudElement[]
        {
            DropBold(new TextElement(() => _loc.T("at.notice.title"), Color: () => TextColor(), FontSize: 13) { Bold = true }),
            new TextElement(() => _loc.T("at.notice.body"), Color: () => TextColor(), FontSize: 13) { RichEmoji = true },
        }, Gap: 4f))
        {
            FillColorFn = () => Fill(TileColor()), CornerRadius = TileRadius, PaddingX = 10f, PaddingY = 8f,
        },
        // PillPair: equal weighted cells, both pills given the same label width (SDL §4).
        new RowElement(new HudElement[]
        {
            new CellElement(PrimaryButton(() => _loc.T("at.notice.ok"), () => AnswerNotice(true), NoticePillLabelW), Weight: 1f),
            new CellElement(NeutralButton(() => _loc.T("at.notice.no"), () => AnswerNotice(false), NoticePillLabelW), Weight: 1f),
        }, Gap: 8f),
    }, Gap: 8f);
}
