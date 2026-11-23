using System;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Stellar.Abstractions.Services;

namespace Stellar.Atlas;

// The Atlas server client (StellarAtlasServer README "API" + "Anti-abuse"). ALL network and disk I/O runs on a worker
// (Task.Run, one request at a time); results come back to the main thread through IFramework.Post — nothing here
// touches the game or the UI (Plugin-Performance-Rules.md §3: workers handle plain managed data only).
//
// Points are cached on disk per scene (raw body + "etag\nfetchedUnix") under the plugin data dir, so the window still
// works offline ("Offline · showing the copy from …"), and every refresh is a conditional GET (If-None-Match → 304).
internal sealed partial class AtlasClient : IDisposable
{
    public enum FetchState { Synced, Offline, NoData }

    public sealed class FetchResult
    {
        public int Scene;
        public FetchState State;
        public AtlasPoint[] Points = Array.Empty<AtlasPoint>();
        public long FetchedUnix;          // when this copy was last confirmed by the server (0 = never)
    }

    public enum ReportStatus { Ok, Retry, RateLimited, Drop, ReRegister, Banned, NoKey }

    public sealed class ReportOutcome
    {
        public ReportStatus Status;
        public string? NewInstallId, NewInstallSecret;   // set when this job registered the install
        public int Accepted, Rejected;                   // per observation, from the 200 body's "seen" array
    }

    private const string CachePrefix = "points/";

    private readonly IPluginServices _services;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);    // one request at a time — the server limits are per install/IP
    private readonly object _diskLock = new();           // IPluginDataStore's temp name is fixed per file → serialize
    private volatile bool _disposed;

    public string BaseUrl { get; set; }

    public AtlasClient(IPluginServices services, string baseUrl)
    {
        _services = services;
        BaseUrl = baseUrl.TrimEnd('/');
        _http = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.GZip })
        { Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("StellarAtlas/" + (typeof(AtlasClient).Assembly.GetName().Version?.ToString(3) ?? "1.0.0"));
    }

    // Build-time key (Local.props / CI secret → AssemblyMetadata "AtlasClientKey"). Empty or the example placeholder =
    // no server calls at all; the cached copy is still shown.
    public static readonly string ClientKey = ReadClientKey();
    public static bool HasKey => ClientKey.Length > 0;

    private static string ReadClientKey()
    {
        try
        {
            foreach (var a in typeof(AtlasClient).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
                if (a.Key == "AtlasClientKey")
                {
                    string v = (a.Value ?? "").Trim();
                    return v.StartsWith("REPLACE_", StringComparison.Ordinal) ? "" : v;
                }
        }
        catch { }
        return "";
    }

    // ── points ────────────────────────────────────────────────────────────────────────────────────────────────────
    public void FetchScene(int scene, Action<FetchResult> onMain) => LoadScene(scene, -1, true, onMain);

    // Prestaging (MapMarkers.Prestage.cs): `indexVersion` = the scene's version in the /v1/scenes index (-1 = unknown →
    // always ask the server). The cached copy is used WITHOUT a request when its version equals the index's; a scene
    // with no cached copy is fetched only when `fetchWithoutCache` (a first visit / browse), never at login.
    public void LoadScene(int scene, long indexVersion, bool fetchWithoutCache, Action<FetchResult> onMain)
    {
        _ = Task.Run(async () =>
        {
            var res = await FetchSceneWorker(scene, indexVersion, fetchWithoutCache).ConfigureAwait(false);
            if (!_disposed) _services.Framework.Post(() => onMain(res));
        });
    }

    // Etag "s<scene>-v<version>" (StellarAtlasServer read.go sceneETag) → version, or -1.
    private static long EtagVersion(string etag)
    {
        int v = etag.LastIndexOf("-v", StringComparison.Ordinal);
        if (v < 0) return -1;
        var digits = etag.Substring(v + 2).Trim('"');
        return long.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out long r) ? r : -1;
    }

    private async Task<FetchResult> FetchSceneWorker(int scene, long indexVersion, bool fetchWithoutCache)
    {
        var res = new FetchResult { Scene = scene, State = FetchState.NoData };
        string name = CachePrefix + scene.ToString(CultureInfo.InvariantCulture);
        byte[]? cached; string etag = ""; long fetched = 0;
        lock (_diskLock)
        {
            cached = _services.Data.Read(name + ".json");
            var meta = _services.Data.Read(name + ".meta");
            if (meta != null)
            {
                var parts = Encoding.UTF8.GetString(meta).Split('\n');
                etag = parts.Length > 0 ? parts[0] : "";
                if (parts.Length > 1) long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out fetched);
            }
        }

        if (cached != null && indexVersion >= 0 && EtagVersion(etag) == indexVersion)
            return Fill(res, cached, FetchState.Synced, fetched);   // the index says the copy is current → no request
        if (cached == null && !fetchWithoutCache) return res;     // not visited yet → fetched on the first visit only

        if (HasKey)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, BaseUrl + "/v1/scenes/" + scene.ToString(CultureInfo.InvariantCulture) + "/points");
                req.Headers.TryAddWithoutValidation(HeaderClientKey, ClientKey);
                if (cached != null && etag.Length > 0) req.Headers.TryAddWithoutValidation("If-None-Match", etag);
                using var resp = await _http.SendAsync(req).ConfigureAwait(false);
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                if (resp.StatusCode == HttpStatusCode.NotModified && cached != null)
                {
                    WriteMeta(name, etag, now);
                    return Fill(res, cached, FetchState.Synced, now);
                }
                if (resp.IsSuccessStatusCode)
                {
                    var body = await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                    string newTag = resp.Headers.ETag?.ToString() ?? "";
                    if (ServerPoints.Parse(body, out _) is { } pts)
                    {
                        lock (_diskLock) _services.Data.Write(name + ".json", body);
                        WriteMeta(name, newTag, now);
                        res.Points = pts; res.State = FetchState.Synced; res.FetchedUnix = now;
                        return res;
                    }
                }
                else if (resp.StatusCode == HttpStatusCode.NotFound)
                {
                    // Scene unknown to the server: nothing known yet (2c), but the server IS reachable.
                    res.State = FetchState.Synced; res.FetchedUnix = now;
                    return res;
                }
                _services.Log.Warning($"[Atlas] points scene={scene} → HTTP {(int)resp.StatusCode}");
            }
            catch (Exception ex) { _services.Log.Warning($"[Atlas] points scene={scene} failed: {ex.GetType().Name}: {ex.Message}"); }
            finally { _gate.Release(); }
        }
        return cached != null ? Fill(res, cached, FetchState.Offline, fetched) : res;
    }

    // GET /v1/scenes → scene id → version, only scenes with points (n > 0), plus the scenes cached on disk — both read
    // off the main thread. The index is null when unreachable (prestaging then uses the cached scenes, unversioned).
    public void FetchIndex(Action<System.Collections.Generic.Dictionary<int, long>?, System.Collections.Generic.List<int>> onMain)
    {
        _ = Task.Run(async () =>
        {
            System.Collections.Generic.Dictionary<int, long>? map = null;
            if (HasKey)
            {
                await _gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, BaseUrl + "/v1/scenes");
                    req.Headers.TryAddWithoutValidation(HeaderClientKey, ClientKey);
                    using var resp = await _http.SendAsync(req).ConfigureAwait(false);
                    if (resp.IsSuccessStatusCode)
                    {
                        var body = await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                        using var doc = System.Text.Json.JsonDocument.Parse(body);
                        if (doc.RootElement.TryGetProperty("scenes", out var arr) && arr.ValueKind == System.Text.Json.JsonValueKind.Array)
                        {
                            map = new System.Collections.Generic.Dictionary<int, long>();
                            foreach (var e in arr.EnumerateArray())
                            {
                                if (!e.TryGetProperty("id", out var id) || !e.TryGetProperty("v", out var v)) continue;
                                if (e.TryGetProperty("n", out var n) && n.ValueKind == System.Text.Json.JsonValueKind.Number && n.GetInt64() <= 0) continue;
                                map[id.GetInt32()] = v.GetInt64();
                            }
                        }
                    }
                    else _services.Log.Warning($"[Atlas] scene index → HTTP {(int)resp.StatusCode}");
                }
                catch (Exception ex) { _services.Log.Warning($"[Atlas] scene index failed: {ex.GetType().Name}: {ex.Message}"); }
                finally { _gate.Release(); }
            }
            var cachedScenes = CachedScenes();
            if (!_disposed) _services.Framework.Post(() => onMain(map, cachedScenes));
        });
    }

    // Scenes that have a cached copy on disk (worker thread: a directory listing).
    private System.Collections.Generic.List<int> CachedScenes()
    {
        var list = new System.Collections.Generic.List<int>();
        lock (_diskLock)
            foreach (var name in _services.Data.List(CachePrefix))
            {
                // PluginDataStore.List returns names relative to the data dir: "points/123.json"
                int slash = name.LastIndexOf('/');
                string file = slash >= 0 ? name.Substring(slash + 1) : name;
                if (!file.EndsWith(".json", StringComparison.Ordinal)) continue;
                if (int.TryParse(file.Substring(0, file.Length - 5), NumberStyles.Integer, CultureInfo.InvariantCulture, out int s)) list.Add(s);
            }
        return list;
    }

    private static FetchResult Fill(FetchResult res, byte[] body, FetchState state, long fetched)
    {
        var pts = ServerPoints.Parse(body, out _);
        if (pts == null) return res;   // corrupt cache → NoData
        res.Points = pts; res.State = state; res.FetchedUnix = fetched;
        return res;
    }

    private void WriteMeta(string name, string etag, long unix)
    {
        lock (_diskLock) _services.Data.Write(name + ".meta", Encoding.UTF8.GetBytes(etag + "\n" + unix.ToString(CultureInfo.InvariantCulture)));
    }

    // Small per-character files (opened chests) written off the main thread, serialized with the cache writes.
    public void WriteFileAsync(string name, byte[] data)
    {
        _ = Task.Run(() => { lock (_diskLock) _services.Data.Write(name, data); });
    }

    public byte[]? ReadFile(string name) { lock (_diskLock) return _services.Data.Read(name); }

    public void Dispose()
    {
        _disposed = true;
        try { _http.CancelPendingRequests(); } catch { }
        try { _http.Dispose(); } catch { }
    }
}
