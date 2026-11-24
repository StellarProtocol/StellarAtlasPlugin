using System;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Stellar.Atlas;

// Install registration + signed reports (StellarAtlasServer README "Anti-abuse"):
//   POST /v1/installs   client key → 201 {"install_id","install_secret","sig_version"}; stored by the caller (config).
//   POST /v1/reports    client key + X-Atlas-Install + X-Atlas-Timestamp (unix s) + X-Atlas-Nonce (8–64 chars, single
//                       use) + X-Atlas-Signature = hex(HMAC-SHA256(key = install_secret hex string as UTF-8,
//                       msg = METHOD "\n" PATH "\n" TIMESTAMP "\n" NONCE "\n" hex(SHA256(body)))).
// The body is signed over the EXACT bytes sent. A 401 "unknown install" means the install was dropped server-side →
// the caller clears its credentials and the next batch registers again.
internal sealed partial class AtlasClient
{
    private const string HeaderClientKey = "X-Atlas-Client-Key";
    private const string HeaderInstall   = "X-Atlas-Install";
    private const string HeaderTimestamp = "X-Atlas-Timestamp";
    private const string HeaderNonce     = "X-Atlas-Nonce";
    private const string HeaderSignature = "X-Atlas-Signature";
    private const string ReportsPath     = "/v1/reports";
    // Creature sightings are signed EXACTLY like reports (same headers, path in the signature) and share this worker, the
    // install credentials and the one-at-a-time gate (a concurrent first sighting + first report must not register twice).
    private const string SightingsPath   = "/v1/creature-sightings";

    // `installId`/`installSecret` are the caller's current credentials (empty = register first, in the same job).
    public void PostReport(byte[] body, string installId, string installSecret, Action<ReportOutcome> onMain)
    {
        _ = Task.Run(async () =>
        {
            var outcome = await PostSignedWorker(ReportsPath, body, installId, installSecret).ConfigureAwait(false);
            if (!_disposed) _services.Framework.Post(() => onMain(outcome));
        });
    }

    private async Task<ReportOutcome> PostSignedWorker(string path, byte[] body, string installId, string secret)
    {
        var outcome = new ReportOutcome { Status = ReportStatus.Retry };
        if (!HasKey) { outcome.Status = ReportStatus.NoKey; return outcome; }
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (installId.Length == 0 || secret.Length == 0)
            {
                var reg = await Register().ConfigureAwait(false);
                if (reg.Status != ReportStatus.Ok) return reg;
                installId = reg.NewInstallId!; secret = reg.NewInstallSecret!;
                outcome.NewInstallId = installId; outcome.NewInstallSecret = secret;
            }

            var uri = new Uri(BaseUrl + path);
            string ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
            string nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            string sig = Sign(secret, "POST", uri.AbsolutePath, ts, nonce, body);

            using var req = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new ByteArrayContent(body) };
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            req.Headers.TryAddWithoutValidation(HeaderClientKey, ClientKey);
            req.Headers.TryAddWithoutValidation(HeaderInstall, installId);
            req.Headers.TryAddWithoutValidation(HeaderTimestamp, ts);
            req.Headers.TryAddWithoutValidation(HeaderNonce, nonce);
            req.Headers.TryAddWithoutValidation(HeaderSignature, sig);
            using var resp = await _http.SendAsync(req).ConfigureAwait(false);
            outcome.Status = Classify(resp.StatusCode);
            if (outcome.Status == ReportStatus.Ok && path == ReportsPath) CountResults(await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false), outcome);
            if (outcome.Status != ReportStatus.Ok)
                _services.Log.Warning($"[Atlas] {path} → HTTP {(int)resp.StatusCode} ({outcome.Status})");
        }
        catch (Exception ex)
        {
            outcome.Status = ReportStatus.Retry;
            _services.Log.Warning($"[Atlas] {path} failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally { _gate.Release(); }
        return outcome;
    }

    private static ReportStatus Classify(HttpStatusCode code) => (int)code switch
    {
        >= 200 and < 300 => ReportStatus.Ok,
        401 => ReportStatus.ReRegister,      // unknown install / bad signature / clock skew → fresh install next time
        403 => ReportStatus.Banned,
        409 => ReportStatus.Drop,            // replayed nonce: this exact request already landed
        400 or 413 or 422 => ReportStatus.Drop,   // the server will never accept this body — don't loop on it
        429 => ReportStatus.RateLimited,
        _ => ReportStatus.Retry,             // 5xx / proxy errors
    };

    // Called with _gate held.
    private async Task<ReportOutcome> Register()
    {
        var o = new ReportOutcome { Status = ReportStatus.Retry };
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/v1/installs");
            req.Headers.TryAddWithoutValidation(HeaderClientKey, ClientKey);
            using var resp = await _http.SendAsync(req).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                o.Status = resp.StatusCode == HttpStatusCode.TooManyRequests ? ReportStatus.RateLimited : ReportStatus.Retry;
                _services.Log.Warning($"[Atlas] install registration → HTTP {(int)resp.StatusCode}");
                return o;
            }
            var bytes = await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(bytes);
            var root = doc.RootElement;
            string id = root.TryGetProperty("install_id", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString() ?? "" : "";
            string sec = root.TryGetProperty("install_secret", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() ?? "" : "";
            if (id.Length == 0 || sec.Length == 0) { _services.Log.Warning("[Atlas] install registration: malformed response"); return o; }
            o.Status = ReportStatus.Ok; o.NewInstallId = id; o.NewInstallSecret = sec;
            _services.Log.Info("[Atlas] install registered");
        }
        catch (Exception ex) { _services.Log.Warning($"[Atlas] install registration failed: {ex.GetType().Name}: {ex.Message}"); }
        return o;
    }

    // 200 {"seen":[{"point_id":812}|{"point_id":9001,"created":true}|{"error":"…"}], …} → accepted / rejected counts.
    private static void CountResults(byte[] body, ReportOutcome o)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("seen", out var seen) || seen.ValueKind != JsonValueKind.Array) return;
            foreach (var e in seen.EnumerateArray())
                if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty("error", out _)) o.Rejected++; else o.Accepted++;
        }
        catch { }
    }

    internal static string Sign(string secret, string method, string path, string ts, string nonce, byte[] body)
    {
        string bodyHash = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
        string msg = method + "\n" + path + "\n" + ts + "\n" + nonce + "\n" + bodyHash;
        using var h = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(h.ComputeHash(Encoding.UTF8.GetBytes(msg))).ToLowerInvariant();
    }
}
