using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace Stellar.Atlas;

// One point from GET /v1/scenes/{id}/points (StellarAtlasServer README "API"). Plain managed data: parsed on the worker
// thread, handed to the main thread with IFramework.Post.
internal sealed class AtlasPoint
{
    public long Id;
    public string Group = "", Subtype = "";
    public Chip Chip;
    public float X, Y, Z;
    public bool HasY;                  // questlog seeds have no height until a report supplies one
    public bool Stale;                 // status "stale": several installs saw nothing there → not shown
    public bool Verified;              // status "verified"
    public bool Hidden;                // stale, or opened by this character (NearestModel marks it)
    public int Reports;                // distinct installs that reported it
    public int ConfigId;               // game_config_id (0 = unknown)
    public long Uuid;                  // game_uuid (0 = unknown)
    public string Label = "";          // questlog's English name (+ subtitle)

    // Prototype source labels: "Map data" = the questlog seed (no reports yet) or a verified point; "Unverified" = only
    // one player has reported it so far (a report-created point, evidence 1).
    public bool IsUnverified => !Verified && Reports > 0;
}

internal static class ServerPoints
{
    // Parses the column-oriented body: {"scene":7,"v":1201,"cols":[...],"rows":[[...],...]}. Columns are looked up by
    // NAME so a server that adds/reorders columns keeps working. Returns null on a malformed body.
    public static AtlasPoint[]? Parse(byte[] body, out long version)
    {
        version = 0;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("v", out var v) && v.ValueKind == JsonValueKind.Number) version = v.GetInt64();
            if (!root.TryGetProperty("cols", out var cols) || cols.ValueKind != JsonValueKind.Array) return null;
            if (!root.TryGetProperty("rows", out var rows) || rows.ValueKind != JsonValueKind.Array) return null;

            int cId = -1, cGroup = -1, cSub = -1, cX = -1, cY = -1, cZ = -1, cStatus = -1, cCfg = -1, cUuid = -1, cLabel = -1, cRep = -1;
            int i = 0;
            foreach (var c in cols.EnumerateArray())
            {
                switch (c.GetString())
                {
                    case "id": cId = i; break;
                    case "group": cGroup = i; break;
                    case "subtype": cSub = i; break;
                    case "x": cX = i; break;
                    case "y": cY = i; break;
                    case "z": cZ = i; break;
                    case "status": cStatus = i; break;
                    case "config_id": cCfg = i; break;
                    case "uuid": cUuid = i; break;
                    case "label": cLabel = i; break;
                    case "reports": cRep = i; break;
                }
                i++;
            }
            if (cId < 0 || cGroup < 0 || cX < 0 || cZ < 0) return null;

            var list = new List<AtlasPoint>(rows.GetArrayLength());
            foreach (var r in rows.EnumerateArray())
            {
                if (r.ValueKind != JsonValueKind.Array) continue;
                int n = r.GetArrayLength();
                if (n <= Math.Max(cId, Math.Max(cGroup, Math.Max(cX, cZ)))) continue;
                var p = new AtlasPoint
                {
                    Id = Long(r[cId]),
                    Group = Str(r[cGroup]),
                    Subtype = cSub >= 0 && cSub < n ? Str(r[cSub]) : "",
                    X = Float(r[cX]),
                    Z = Float(r[cZ]),
                };
                if (cY >= 0 && cY < n && r[cY].ValueKind == JsonValueKind.Number) { p.Y = Float(r[cY]); p.HasY = true; }
                string status = cStatus >= 0 && cStatus < n ? Str(r[cStatus]) : "";
                if (status == "removed") continue;
                p.Stale = status == "stale";
                p.Verified = status == "verified";
                if (cCfg >= 0 && cCfg < n) p.ConfigId = (int)Long(r[cCfg]);
                if (cUuid >= 0 && cUuid < n) p.Uuid = Long(r[cUuid]);
                if (cLabel >= 0 && cLabel < n) p.Label = Str(r[cLabel]);
                if (cRep >= 0 && cRep < n) p.Reports = (int)Long(r[cRep]);
                p.Chip = AtlasGroups.FromServer(p.Group, p.Subtype);
                list.Add(p);
            }
            return list.ToArray();
        }
        catch (Exception) { return null; }
    }

    private static string Str(JsonElement e) => e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : "";

    private static float Float(JsonElement e) => e.ValueKind == JsonValueKind.Number ? (float)e.GetDouble() : 0f;

    // Numbers, or decimal strings (uuid is a string on the wire: 64-bit).
    private static long Long(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Number) return e.TryGetInt64(out var l) ? l : (long)e.GetDouble();
        if (e.ValueKind == JsonValueKind.String && long.TryParse(e.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s)) return s;
        return 0;
    }
}
