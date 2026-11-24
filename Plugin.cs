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
// notice; per character: SHOW chips). Pieces:
//
// Sharing is ALWAYS ON (user decision 2026-11-23): using Atlas means sharing what you find; the player is informed by the
// first-open notice (and the README / launcher description), and nothing is sent before they click its "Got it".
// To stop sharing, turn Atlas off in the launcher.
//   Plugin.Tick.cs            scene / player / scan / fetch / report scheduling (world-only, ≤5 Hz)
//   Plugin.Guide.cs           the game's guide marker + "just opened" flash
//   Plugin.Window*.cs         the house-style window (header, SHOW, NEAREST, footer, first-open notice)
//   Plugin.Style*.cs          house kit (copied from StellarAutoGatherPlugin)
//   EntityScanner*.cs         live collection walk (port of the validated Experiment Chest Probe)
//   AtlasClient*.cs           HTTP (points + signed reports), disk cache — worker thread only
//   NearestModel.cs           server + live merge, the 8 nearest shown points
//   MapMarkers*.cs            the shown points as markers on the game's own big map + minimap (option C)
//   Plugin.Creatures.cs       magical creatures (prototype atlas-creatures.tpl.html): Creatures.cs / CreatureModel.cs /
//                             CreatureSightings.cs / SessionLua.cs / EntityScanner.Creatures.cs / AtlasClient.Creatures.cs,
//                             panel Plugin.Window.Creatures*.cs, map tiles MapMarkers.CreatureTiles.cs
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
    private readonly MapMarkers    _map;
    private readonly GroundProbe   _ground;

    private bool _noticeSeen;         // the first-open notice was acknowledged ("Got it") — gates every report
    private long _charId;                            // 0 until this login's character is known (FastTick)
    private int  _mask = AtlasGroups.DefaultMask;   // SHOW chips, per character

    // In-game 2026-11-23: right after a login the first map push (198 flags) and NEAREST used the PREVIOUS filter, because
    // the mask of the last character stayed loaded until the new character id was read → a wrong list and a second
    // ~100–200 ms map rebuild. Nothing filter-driven (NEAREST, map flags, not-here) runs before the character's own saved
    // filter (or the new-user defaults) is loaded; logout resets the mask to the defaults.
    private bool CharReady => _charId != 0;

    public Plugin(IPluginServices services)
    {
        _services = services;
        _loc      = services.Localization;
        _cfg      = services.Config.GetSection("settings");

        // Migration: the old per-install "share" switch is gone (sharing is always on) — drop a stored value once.
        if (!_cfg.Get<bool>("share_dropped", false))
        {
            _cfg.RemoveByPrefix("share");
            _cfg.Set<bool>("share_dropped", true);
            _cfg.SaveQuiet();
        }
        // Migration: the old notice keys (notice_seen / notice_shown, from the "OK, share / Don't share" notice) are dropped
        // once, so every install sees the new "Atlas is a shared map" notice. Runs before notice_seen_v2 exists (the
        // prefix would match it too); the guard key deliberately doesn't start with "notice_".
        if (!_cfg.Get<bool>("v1_notice_dropped", false))
        {
            _cfg.RemoveByPrefix("notice_");
            _cfg.Set<bool>("v1_notice_dropped", true);
            _cfg.SaveQuiet();
        }
        _noticeSeen  = _cfg.Get<bool>("notice_seen_v2", false);
        string url   = _cfg.Get<string>("server_url", DefaultServerUrl) ?? DefaultServerUrl;

        _lua     = new GameLua(services);
        _client  = new AtlasClient(services, url.Length > 0 ? url : DefaultServerUrl);
        _scanner = new EntityScanner(services, _lua);
        _opened  = new OpenedStore(_client);
        _model   = new NearestModel(_lua);
        _map     = new MapMarkers(services, _client);
        _ground  = new GroundProbe(services);
        _reports = new ReportQueue(_client, () => _services.Framework.TimeNow, GetInstall, SetInstall, m => _services.Log.Info(m));
        _session   = new SessionLua(services);
        _sightings = new CreatureSightings(_client, () => _services.Framework.TimeNow, GetInstall, SetInstall, m => _services.Log.Info(m));
        _map.CreatureSource = _cre;
        _scanner.CreatureLeft = OnCreatureLeft;
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
        RecomputeNearest();
    }

    private void OnLogout()
    {
        _centerer.Stop();
        CloseCreatureLines();
        _window.SetVisible(false);
        StopGuide(clearMarker: true);
        _scanner.Reset();
        _model.Clear();
        _lua.OnLogout();
        OnCreaturesLogout();
        _map.OnLogout();
        _scene = 0;
        _charId = 0;
        SetMask(AtlasGroups.DefaultMask, "logout");
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
    // Reports go out whenever Atlas runs, once the player clicked "Got it" on the first-open notice (user decision
    // 2026-11-23: "seen" = acknowledged, not merely displayed). Before that nothing is queued or sent; on the click
    // everything already in range is queued at once (Plugin.Tick.cs TickSharingEdge).
    private bool SharingActive => _noticeSeen;

    private void AcknowledgeNotice()
    {
        _noticeSeen = true;
        _cfg.Set<bool>("notice_seen_v2", true);
        _cfg.Save();
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
        long prev = _charId;
        _charId = charId;
        // A mask saved before the SHOW redesign keeps the chips that still exist (MigrateMask); a new character gets the
        // new-character default (every chip off).
        // In-game 2026-11-24 (Atlas creatures test): with chip clicks ~0.7 s apart the in-memory mask fell back to an OLDER
        // value (chest bits lost, creature bits kept) — only this method can do that, by re-reading the config. The
        // framework config tree can be replaced by an external-change reload of the file (async writer + file watcher,
        // PluginConfigService.OnExternalFileChanged), so a re-read during a burst of saves can return a stale value. So
        // the config is read ONCE per character per session; any later OnCharacter for the same character (char id
        // flicker, logout + login) takes the session copy, which every toggle keeps current.
        if (charId == 0) SetMask(AtlasGroups.DefaultMask, "char 0");
        else if (_maskByChar.TryGetValue(charId, out int kept)) SetMask(kept, $"char {charId} (session copy; was {prev})");
        else
        {
            int raw = _cfg.Get<int>(MaskKey(charId), AtlasGroups.DefaultMask);
            SetMask(AtlasGroups.MigrateMask(raw), $"char {charId} (config 0x{raw:X}, migrate; was {prev})");
            _maskByChar[charId] = _mask;
        }
        _opened.SetCharacter(charId);
        RecomputeNearest();
    }

    private readonly Dictionary<long, int> _maskByChar = new();   // session copy per character (see OnCharacter)

    // The ONLY writer of _mask (toggle / char / logout), logged on every change so a lost chip shows its cause in-game.
    private void SetMask(int value, string reason)
    {
        if (value != _mask) _services.Log.Info($"[Atlas] mask 0x{_mask:X} → 0x{value:X} ({reason})");
        _mask = value;
    }

    private bool ChipOn(Chip c) => (_mask & (1 << (int)c)) != 0;

    private void ToggleMaskBit(int bit)
    {
        SetMask(_mask ^ bit, $"toggle 0x{bit:X}");
        if (_charId != 0) { _maskByChar[_charId] = _mask; _cfg.Set<int>(MaskKey(_charId), _mask); _cfg.Save(); }
        _map.NoteFilterChanged(_services.Framework.TimeNow);   // coalesce a burst of chip clicks into one map rebuild
        if ((bit & Creatures.AllBits) != 0) OnCreatureChipToggled();
        RecomputeNearest();
    }

    public void Dispose()
    {
        try { _services.ClientState.Logout -= OnLogout; } catch { }
        try { _loc.LanguageChanged -= OnLanguageChanged; } catch { }
        StopTicks();
        _centerer.Stop();
        CloseCreatureLines();
        StopGuide(clearMarker: true);
        _map.Restore();
        _launcherEntry.Dispose();
        RemoveCreatureLines();
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
