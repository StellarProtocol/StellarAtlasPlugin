using System;
using System.Collections.Generic;
using System.Globalization;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Plugins;
using Stellar.Abstractions.Services;

namespace Stellar.Atlas;

// Atlas — a chest / resource finder fed by the crowd-sourced Atlas point server, plus what this player sees live.
// Approved prototype: _scratch\prototypes\atlas.html option A (section 1 + states 2a–2d).
//
// Core: services, window / launcher registration, logout + dispose, persisted settings (per install: server URL,
// share, notice; per character: SHOW chips). Pieces:
//   Plugin.Tick.cs            scene / player / scan / fetch / report scheduling (world-only, ≤5 Hz)
//   Plugin.Guide.cs           the game's guide marker + "just opened" flash
//   Plugin.Window*.cs         the house-style window (header, SHOW, NEAREST, footer, first-open notice)
//   Plugin.Style*.cs          house kit (copied from StellarAutoGatherPlugin)
//   EntityScanner*.cs         live collection walk (port of the validated Experiment Chest Probe)
//   AtlasClient*.cs           HTTP (points + signed reports), disk cache — worker thread only
//   NearestModel.cs           server + live merge, the 8 nearest shown points
public sealed partial class Plugin : IStellarPlugin
{
    public string Name => "Atlas";

    private const string DefaultServerUrl = "https://atlas.stellarresonance.app";

    private readonly IPluginServices _services;
    private readonly ILocalization   _loc;
    private readonly IConfigSection  _cfg;
    private readonly IWindowControl  _window;
    private readonly IDisposable     _launcherEntry;
    private readonly WindowCenterer  _centerer;

    private readonly GameLua       _lua;
    private readonly EntityScanner _scanner;
    private readonly AtlasClient   _client;
    private readonly ReportQueue   _reports;
    private readonly OpenedStore   _opened;
    private readonly NearestModel  _model;

    private bool _share;              // per install, default ON (user decision); see SharingActive
    private bool _noticeSeen;         // the first-open notice was answered
    private bool _noticeShown;        // the notice has been on screen at least once (this session or before)
    private long _charId;
    private int  _mask = AtlasGroups.DefaultMask;   // SHOW chips, per character

    public Plugin(IPluginServices services)
    {
        _services = services;
        _loc      = services.Localization;
        _cfg      = services.Config.GetSection("settings");

        _share       = _cfg.Get<bool>("share", true);
        _noticeSeen  = _cfg.Get<bool>("notice_seen", false);
        _noticeShown = _noticeSeen || _cfg.Get<bool>("notice_shown", false);
        string url   = _cfg.Get<string>("server_url", DefaultServerUrl) ?? DefaultServerUrl;

        _lua     = new GameLua(services);
        _client  = new AtlasClient(services, url.Length > 0 ? url : DefaultServerUrl);
        _scanner = new EntityScanner(services, _lua);
        _opened  = new OpenedStore(_client);
        _model   = new NearestModel(_lua);
        _reports = new ReportQueue(_client, () => _services.Framework.TimeNow, GetInstall, SetInstall);
        _scanner.Appeared = OnLiveAppeared;
        _scanner.Opened   = OnLiveOpened;
        _loc.LanguageChanged += OnLanguageChanged;

        _window = _services.Windows.Register(new WindowRegistration(
            Spec: new WindowSpec(
                Id:          "atlas.main",
                Title:       _loc.T("at.title"),
                DefaultRect: new WindowRect(200f, 120f, WindowWidth, 0f),
                Category:    WindowCategory.Tools,
                Style:       WindowPanelStyle.GlassMenu)
            { Draggable = true, Closable = true, StartVisible = false,
              TitleProvider = () => _loc.T("at.title"),
              // Built ahead of the first open (Plugin-Performance-Rules.md §5). While hidden every Func is a field read /
              // cached string: the model is only recomputed while shown, so a hidden fill binds 0 rows.
              Prebuild = true,
              ShouldRender = InWorld },
            Root: BuildRoot(),
            OnClose: () => _window!.SetVisible(false)));
        _centerer = new WindowCenterer(_services.Framework, _window, WindowCenterer.CanvasCentre);

        // The phase stays World through the logout transition, so ShouldRender alone would leave the window up on the
        // loading screen; close it synchronously on Logout (house rule, Stellar-Design-Language.md §5).
        _services.ClientState.Logout += OnLogout;

        // Title is the stable pin-identity key; TitleProvider carries the localized display.
        _launcherEntry = _services.Launcher.Register(new LauncherEntry(
            Title:   "Atlas",
            IconPng: Icon(LauncherIconPx()),
            IconKey: null,
            OnOpen:  OpenWindow)
        { Group = LauncherGroup.Plugin,
          TitleProvider = () => _loc.T("at.title"),
          ShouldShow = () => _services.ClientState.Phase == GamePhase.World });

        StartTicks();
        _services.Log.Info($"[Atlas] constructed (server={_client.BaseUrl}, key={(AtlasClient.HasKey ? "set" : "none: offline only")})");
    }

    private bool InWorld() => _services.ClientState.Phase == GamePhase.World
                              && (_services.ClientState.UiState & GameUIState.Loading) == 0;

    private bool Shown => _window is { IsShown: true };

    private void OpenWindow()
    {
        _centerer.OpenCentred();
        if (!_noticeSeen && !_noticeShown) { _noticeShown = true; _cfg.Set<bool>("notice_shown", true); _cfg.Save(); }
        RecomputeNearest();
    }

    private void OnLogout()
    {
        _centerer.Stop();
        _window.SetVisible(false);
        StopGuide(clearMarker: true);
        _scanner.Reset();
        _model.Clear();
        _lua.OnLogout();
        _scene = 0;
        _charId = 0;
    }

    private void OnLanguageChanged()
    {
        _lua.OnLanguageChanged();
        _distText.Clear();
        _statusKey = null;   // header line rebuilt in the new language
        _sceneName = "";
        _sceneNameFor = 0;
    }

    // ── persisted settings ────────────────────────────────────────────────────────────────────────────────────────
    // Reports go out only when Share is on AND the first-open notice has been on screen once — default ON is the user's
    // decision, but nothing leaves the machine before the player was told what is sent.
    private bool SharingActive => _share && _noticeShown;

    private void SetShare(bool on)
    {
        _share = on;
        if (!on) _reports.Clear();
        _cfg.Set<bool>("share", on);
        _cfg.Save();
    }

    private void AnswerNotice(bool share)
    {
        _noticeSeen = _noticeShown = true;
        _cfg.Set<bool>("notice_seen", true);
        _cfg.Set<bool>("notice_shown", true);
        SetShare(share);   // saves
    }

    private (string Id, string Secret) GetInstall() =>
        (_cfg.Get<string>("install_id", "") ?? "", _cfg.Get<string>("install_secret", "") ?? "");

    private void SetInstall(string id, string secret)
    {
        _cfg.Set<string>("install_id", id);
        _cfg.Set<string>("install_secret", secret);
        _cfg.Save();
    }

    private string MaskKey(long charId) => "show." + charId.ToString(CultureInfo.InvariantCulture);

    // Character switch (first world tick after a login): per-character chips + opened chests.
    private void OnCharacter(long charId)
    {
        _charId = charId;
        _mask = charId != 0 ? _cfg.Get<int>(MaskKey(charId), AtlasGroups.DefaultMask) : AtlasGroups.DefaultMask;
        _opened.SetCharacter(charId);
        RecomputeNearest();
    }

    private bool ChipOn(Chip c) => (_mask & (1 << (int)c)) != 0;
    private bool MoreOpen => (_mask & AtlasGroups.MoreOpenBit) != 0;

    private void ToggleMaskBit(int bit)
    {
        _mask ^= bit;
        if (_charId != 0) { _cfg.Set<int>(MaskKey(_charId), _mask); _cfg.Save(); }
        RecomputeNearest();
    }

    public void Dispose()
    {
        try { _services.ClientState.Logout -= OnLogout; } catch { }
        try { _loc.LanguageChanged -= OnLanguageChanged; } catch { }
        StopTicks();
        _centerer.Stop();
        StopGuide(clearMarker: true);
        _launcherEntry.Dispose();
        _window.Remove();
        _client.Dispose();
    }

    // ── icons ─────────────────────────────────────────────────────────────────────────────────────────────────────
    // Pre-drawn per size (tools/icons/gen-atlas-icon.py); pick the exact bitmap for the current UI scale, else the next
    // larger (matches the framework's IconSizePicker). Cached per size, misses included (ImageElement funcs are polled).
    private static readonly int[] IconSizes = { 16, 18, 24, 32, 36, 48, 64, 128 };
    private static readonly Dictionary<int, byte[]?> _iconPngs = new();
    private float _iconScale = -1f;
    private byte[]? _headerPng;

    private float UiScale()
    {
        int cw = _services.Framework.CanvasWidth;
        return cw > 0 ? (float)_services.Framework.ScreenWidth / cw : 1f;
    }

    private static int PickSize(float units, float scale)
    {
        float want = units * scale - 0.05f;
        foreach (int s in IconSizes) if (s >= want) return s;
        return IconSizes[^1];
    }

    private int LauncherIconPx() => PickSize(32f, UiScale());

    private byte[]? HeaderIcon()
    {
        float scale = UiScale();
        if (scale != _iconScale) { _iconScale = scale; _headerPng = Icon(PickSize(HeaderGlyphPx, scale)); }
        return _headerPng;
    }

    private static byte[]? Icon(int px)
    {
        if (_iconPngs.TryGetValue(px, out var png)) return png;
        try
        {
            using var s = typeof(Plugin).Assembly.GetManifestResourceStream($"Stellar.Atlas.atlas-{px}.png");
            if (s != null)
            {
                using var ms = new System.IO.MemoryStream();
                s.CopyTo(ms);
                png = ms.ToArray();
            }
        }
        catch { png = null; }
        _iconPngs[px] = png;
        return png;
    }
}
