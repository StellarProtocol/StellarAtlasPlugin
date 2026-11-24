using System;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Stellar.Atlas;

// Magical creature status (StellarAtlasServer PR #7):
//   GET  /v1/creatures?server=<id>   client-key read auth, like the points. Always 5 creatures; `lines` = the alive lines
//                                    of every creature + the absent lines of Lovely / Breezy Boarlet; an unlisted line is
//                                    unknown. Times are unix ms; source live | player; tracked:false = only player
//                                    sightings on that server.
//   POST /v1/creature-sightings     signed like /v1/reports (AtlasClient.Reports.cs PostSignedWorker).
//
// Head-of-line (Plugin-Performance-Rules.md "Head-of-line blocking"): the 30 s status GET has its OWN one-at-a-time gate,
// so it never queues behind a scene / prestage load and a chest fetch never queues behind it. Parsed on the worker into
// plain managed data; the result is posted back to the main thread.
internal sealed partial class AtlasClient
{
    public sealed class CreatureLine
    {
        public int Line;
        public bool Alive;
        public bool HasPos;
        public float X, Y, Z;
        public long Hp, MaxHp;           // 0 = unknown
        public string Spot = "";
        public bool Player;              // source "player" (seen by an Atlas player, not the live status)
        public long UpdatedAt;           // unix ms
    }

    public sealed class CreatureInfo
    {
        public int Id;
        public bool Tracked;
        public long NextSpawnAt;         // unix ms, 0 = none
        public CreatureLine[] Lines = Array.Empty<CreatureLine>();
    }

    public sealed class CreatureStatus
    {
        public string Server = "";
        public bool Ok;                  // false = the request failed (offline / HTTP error / malformed)
        public bool Tracked, Stale;
        public long UpdatedAt;           // unix ms
        public int LineCount;            // top-level "line_count" (lines on this server); 0 = null / missing = unknown
        public CreatureInfo?[] ByIdx = new CreatureInfo?[Creatures.Count];   // Creatures index → info
    }

    private readonly SemaphoreSlim _creGate = new(1, 1);

    public void FetchCreatures(string server, Action<CreatureStatus> onMain)
    {
        _ = Task.Run(async () =>
        {
            var res = new CreatureStatus { Server = server };
            if (HasKey)
            {
                await _creGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, BaseUrl + "/v1/creatures?server=" + Uri.EscapeDataString(server));
                    req.Headers.TryAddWithoutValidation(HeaderClientKey, ClientKey);
                    using var resp = await _http.SendAsync(req).ConfigureAwait(false);
                    if (resp.IsSuccessStatusCode) ParseCreatures(await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false), res);
                    else _services.Log.Warning($"[Atlas] creatures server={server} → HTTP {(int)resp.StatusCode}");
                }
                catch (Exception ex) { _services.Log.Warning($"[Atlas] creatures server={server} failed: {ex.GetType().Name}: {ex.Message}"); }
                finally { _creGate.Release(); }
            }
            if (!_disposed) _services.Framework.Post(() => onMain(res));
        });
    }

    private static void ParseCreatures(byte[] body, CreatureStatus res)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            res.Tracked = Bool(root, "tracked");
            res.Stale = Bool(root, "stale");
            res.UpdatedAt = Long(root, "updated_at");
            res.LineCount = (int)Math.Clamp(Long(root, "line_count"), 0, 999);   // optional (server PR in progress)
            if (!root.TryGetProperty("creatures", out var arr) || arr.ValueKind != JsonValueKind.Array) return;
            foreach (var c in arr.EnumerateArray())
            {
                int idx = Creatures.Index((int)Long(c, "id"));
                if (idx < 0) continue;
                var info = new CreatureInfo { Id = Creatures.Ids[idx], Tracked = Bool(c, "tracked"), NextSpawnAt = Long(c, "next_spawn_at") };
                if (c.TryGetProperty("lines", out var lines) && lines.ValueKind == JsonValueKind.Array)
                {
                    var list = new CreatureLine[lines.GetArrayLength()];
                    int n = 0;
                    foreach (var l in lines.EnumerateArray())
                    {
                        var cl = new CreatureLine
                        {
                            Line = (int)Long(l, "line"),
                            Alive = l.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String && st.GetString() == "alive",
                            Hp = Long(l, "hp"), MaxHp = Long(l, "max_hp"),
                            Spot = l.TryGetProperty("spot", out var sp) && sp.ValueKind == JsonValueKind.String ? sp.GetString() ?? "" : "",
                            Player = l.TryGetProperty("source", out var so) && so.ValueKind == JsonValueKind.String && so.GetString() == "player",
                            UpdatedAt = Long(l, "updated_at"),
                        };
                        if (Num(l, "x", out double x) && Num(l, "z", out double z))
                        {
                            cl.HasPos = true; cl.X = (float)x; cl.Z = (float)z;
                            cl.Y = Num(l, "y", out double y) ? (float)y : 0f;
                        }
                        if (cl.Line > 0) list[n++] = cl;
                    }
                    if (n < list.Length) Array.Resize(ref list, n);
                    info.Lines = list;
                }
                res.ByIdx[idx] = info;
            }
            res.Ok = true;
        }
        catch { res.Ok = false; }
    }

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static long Long(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long l) ? l
        : e.TryGetProperty(name, out v) && v.ValueKind == JsonValueKind.Number ? (long)v.GetDouble() : 0;

    private static bool Num(JsonElement e, string name, out double d)
    {
        d = 0;
        return e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out d);
    }

    // POST /v1/creature-sightings (one sighting per body). Same outcome handling as a report batch.
    public void PostSighting(byte[] body, string installId, string installSecret, Action<ReportOutcome> onMain)
    {
        _ = Task.Run(async () =>
        {
            var outcome = await PostSignedWorker(SightingsPath, body, installId, installSecret).ConfigureAwait(false);
            if (!_disposed) _services.Framework.Post(() => onMain(outcome));
        });
    }

    internal static string Ms(long v) => v.ToString(CultureInfo.InvariantCulture);
}
