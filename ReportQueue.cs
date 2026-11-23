using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Stellar.Atlas;

// "What I found and where" (Share on): every tracked live entity is queued ONCE per session (dedupe by game uuid — chest
// uuids are stable across lines and relogs, KB §Chest Finder) and sent in batches: every BatchSeconds, or as soon as
// BatchSize items wait. v1 sends NO "not seen" votes. Body fields are exactly the server's reportBody (it rejects unknown
// fields): scene_id + seen[{group, subtype, config_id, uuid (decimal string), x, y, z}] — no names, no UIDs.
//
// Enqueue is O(1) (called from the 2 Hz scan's Appeared event); the JSON is built only when a batch is sent (every ≥30 s).
// Backoff: Retry doubles 30 s → 15 min; 429 waits ≥ 2 min; 401 drops the install credentials and registers again (at
// most 3 times a session, the server allows 5 registrations per IP per hour); 403 / no client key stop reporting.
internal sealed class ReportQueue
{
    private const int   BatchSize    = 20;
    private const float BatchSeconds = 30f;
    private const int   MaxPending   = 200;
    private const float MinBackoff   = 30f, MaxBackoff = 900f, RateLimitWait = 120f;
    private const int   MaxReRegister = 3;

    private struct Obs
    {
        public int Scene, Cfg;
        public long Uuid;
        public string Group, Subtype;
        public float X, Y, Z;
    }

    private readonly AtlasClient _client;
    private readonly Func<float> _now;
    private readonly Func<(string Id, string Secret)> _getCreds;
    private readonly Action<string, string> _setCreds;
    private readonly List<Obs> _pending = new();
    private readonly HashSet<long> _queued = new();   // session dedupe
    private readonly StringBuilder _sb = new(2048);
    private float _firstAt = -1f, _notBefore, _backoff = MinBackoff;
    private int _inFlight, _reRegisters;
    private bool _stopped;

    public ReportQueue(AtlasClient client, Func<float> now, Func<(string, string)> getCreds, Action<string, string> setCreds)
    {
        _client = client; _now = now; _getCreds = getCreds; _setCreds = setCreds;
    }

    public int Pending => _pending.Count;

    public void Enqueue(int scene, LiveEnt e)
    {
        if (_stopped || scene <= 0 || !e.Tracked || !e.HasPos || e.Uuid == 0) return;
        if (!_queued.Add(e.Uuid)) return;
        if (_pending.Count >= MaxPending) _pending.RemoveAt(0);   // rare: only while the server is unreachable for long
        _pending.Add(new Obs { Scene = scene, Cfg = e.Cfg, Uuid = e.Uuid, Group = e.Group, Subtype = e.Subtype, X = e.X, Y = e.Y, Z = e.Z });
        if (_firstAt < 0f) _firstAt = _now();
    }

    // Share turned off → forget what is waiting (it must not be sent later).
    public void Clear() { _pending.Clear(); _firstAt = -1f; }

    // Called at ~1 Hz from the plugin tick (main thread).
    public void Tick(bool force = false)
    {
        if (_stopped || _inFlight > 0 || _pending.Count == 0) return;
        float now = _now();
        if (now < _notBefore) return;
        if (!force && _pending.Count < BatchSize && now - _firstAt < BatchSeconds) return;
        Send();
    }

    private void Send()
    {
        // One scene per body: take the first item's scene, up to BatchSize items of it.
        int scene = _pending[0].Scene, n = 0;
        var sb = _sb.Clear();
        sb.Append("{\"scene_id\":").Append(scene.ToString(CultureInfo.InvariantCulture)).Append(",\"seen\":[");
        var sent = new List<long>(BatchSize);
        for (int i = 0; i < _pending.Count && n < BatchSize; i++)
        {
            var o = _pending[i];
            if (o.Scene != scene) continue;
            if (n > 0) sb.Append(',');
            sb.Append("{\"group\":\"").Append(o.Group).Append("\",\"subtype\":\"").Append(o.Subtype).Append('"');
            if (o.Cfg > 0) sb.Append(",\"config_id\":").Append(o.Cfg.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"uuid\":\"").Append(o.Uuid.ToString(CultureInfo.InvariantCulture)).Append('"')
              .Append(",\"x\":").Append(F(o.X)).Append(",\"y\":").Append(F(o.Y)).Append(",\"z\":").Append(F(o.Z)).Append('}');
            sent.Add(o.Uuid);
            n++;
        }
        sb.Append("]}");
        var body = Encoding.UTF8.GetBytes(sb.ToString());
        var creds = _getCreds();
        _inFlight++;
        _client.PostReport(body, creds.Id, creds.Secret, outcome => OnOutcome(outcome, sent));
    }

    private void OnOutcome(AtlasClient.ReportOutcome o, List<long> sent)
    {
        _inFlight--;
        if (o.NewInstallId != null && o.NewInstallSecret != null) _setCreds(o.NewInstallId, o.NewInstallSecret);
        float now = _now();
        switch (o.Status)
        {
            case AtlasClient.ReportStatus.Ok:
            case AtlasClient.ReportStatus.Drop:
                Remove(sent);
                _backoff = MinBackoff;
                break;
            case AtlasClient.ReportStatus.RateLimited:
                _notBefore = now + MathF.Max(RateLimitWait, _backoff);
                break;
            case AtlasClient.ReportStatus.ReRegister:
                _setCreds("", "");
                if (++_reRegisters > MaxReRegister) { _stopped = true; Clear(); break; }
                _notBefore = now + MinBackoff;
                break;
            case AtlasClient.ReportStatus.Banned:
            case AtlasClient.ReportStatus.NoKey:
                _stopped = true; Clear();
                break;
            default:   // Retry
                _notBefore = now + _backoff;
                _backoff = MathF.Min(MaxBackoff, _backoff * 2f);
                break;
        }
        _firstAt = _pending.Count > 0 ? now : -1f;
    }

    private void Remove(List<long> sent)
    {
        // Batches are ≤ 20 → the nested scan is tiny, and it runs once per send (≥ 30 s apart).
        for (int i = _pending.Count - 1; i >= 0; i--)
            if (sent.Contains(_pending[i].Uuid)) _pending.RemoveAt(i);
    }

    private static string F(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);
}
