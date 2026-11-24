using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Stellar.Atlas;

// Creature sightings (approved behaviour 5): once the player clicked "Got it" on the shared-map notice (the same gate as
// chest reports), a creature in view is reported to POST /v1/creature-sightings —
//   alive  on first sight, then at most every ResendSecs per uuid, or sooner when its HP dropped ≥ HpStepPct of max since
//          the last one sent;
//   gone   when its HP reads 0, or when its uuid leaves entityDict_ while the player was within GoneRadius of its last
//          position (further away it just left view range — no report).
// The server key is (server, line, monster_id, uuid) with the line read at the time of the sighting.
//
// One request in flight, at least MinGap seconds apart (≤ 30 / min; the server allows 60 / min per install). Pending
// items are coalesced per uuid (a newer alive replaces an older one; gone replaces alive), so a long outage can't pile
// up. Backoff mirrors ReportQueue: Retry doubles 30 s → 15 min, 429 waits ≥ 2 min, 401 drops the install credentials
// (registration happens again on the next send), 403 / no key stop. Bodies are built only when sent (≤ 1 per 2 s).
internal sealed class CreatureSightings
{
    private const float ResendSecs = 60f;
    private const int   HpStepPct  = 10;
    public  const float GoneRadius = 50f;
    private const float MinGap     = 2f;
    private const float MinBackoff = 30f, MaxBackoff = 900f, RateLimitWait = 120f;
    private const int   MaxPending = 16;

    private struct Item
    {
        public long Uuid;
        public int Idx, Line;
        public bool Gone;
        public float X, Y, Z;
        public long Hp, MaxHp, SeenAt;
    }

    private sealed class Sent { public float At = -1000f; public long Hp = -1; public bool Gone; }

    private readonly AtlasClient _client;
    private readonly Func<float> _now;
    private readonly Func<(string Id, string Secret)> _getCreds;
    private readonly Action<string, string> _setCreds;
    private readonly Action<string> _info;
    private readonly List<Item> _pending = new(MaxPending);
    private readonly Dictionary<long, Sent> _sent = new();
    private readonly StringBuilder _sb = new(256);
    private float _notBefore, _backoff = MinBackoff, _lastSendAt = -100f;
    private bool _inFlight, _stopped, _loggedFirst;
    private int _sentTotal, _reRegisters;

    public CreatureSightings(AtlasClient client, Func<float> now, Func<(string, string)> getCreds, Action<string, string> setCreds,
        Action<string> info)
    {
        _client = client; _now = now; _getCreds = getCreds; _setCreds = setCreds; _info = info;
    }

    public string Status() => $"{_pending.Count} pending, {_sentTotal} sent" + (_stopped ? ", STOPPED" : "");

    // Scene change / logout: what was seen there is not followed any more (pending items still go out).
    public void ResetSeen() => _sent.Clear();

    // 1 Hz per creature in view (≤ a handful): decides whether an "alive" sighting is due. O(1), no allocation once the
    // uuid is known.
    public void Observe(LiveCreature c, int line, long nowMs)
    {
        if (_stopped || !c.HasPos || line <= 0 || c.MaxHp <= 0) return;
        if (!_sent.TryGetValue(c.Uuid, out var s)) { s = new Sent(); _sent[c.Uuid] = s; }
        if (s.Gone) return;
        if (c.Dead) { Gone(c, line); return; }
        float now = _now();
        bool due = s.Hp < 0 || now - s.At >= ResendSecs || (s.Hp - c.Hp) * 100 >= HpStepPct * c.MaxHp;
        if (!due) return;
        s.At = now; s.Hp = c.Hp;
        Add(new Item { Uuid = c.Uuid, Idx = c.Idx, Line = line, X = c.X, Y = c.Y, Z = c.Z, Hp = c.Hp, MaxHp = c.MaxHp, SeenAt = nowMs });
    }

    // Killed (HP 0) or left view while the player stood within GoneRadius (the caller checks the distance).
    public void Gone(LiveCreature c, int line)
    {
        if (_stopped || line <= 0) return;
        if (_sent.TryGetValue(c.Uuid, out var s))
        {
            if (s.Gone) return;
            s.Gone = true;
        }
        else _sent[c.Uuid] = new Sent { Gone = true };
        Add(new Item { Uuid = c.Uuid, Idx = c.Idx, Line = line, Gone = true });
    }

    private void Add(Item it)
    {
        for (int i = 0; i < _pending.Count; i++)
            if (_pending[i].Uuid == it.Uuid) { if (it.Gone || !_pending[i].Gone) _pending[i] = it; return; }
        if (_pending.Count >= MaxPending) _pending.RemoveAt(0);
        _pending.Add(it);
    }

    // ~1 Hz from the plugin tick (main thread).
    public void Tick(string server)
    {
        if (_stopped || _inFlight || _pending.Count == 0 || server.Length == 0) return;
        float now = _now();
        if (now < _notBefore || now - _lastSendAt < MinGap) return;
        var it = _pending[0];
        _pending.RemoveAt(0);
        var body = Encoding.UTF8.GetBytes(Build(server, in it));
        var creds = _getCreds();
        _inFlight = true;
        _lastSendAt = now;
        _client.PostSighting(body, creds.Id, creds.Secret, o => OnOutcome(o, it));
    }

    private string Build(string server, in Item it)
    {
        var sb = _sb.Clear();
        sb.Append("{\"server\":\"").Append(server).Append('"');
        if (!it.Gone) sb.Append(",\"scene_id\":").Append(Creatures.Scene.ToString(CultureInfo.InvariantCulture));
        sb.Append(",\"line\":").Append(it.Line.ToString(CultureInfo.InvariantCulture))
          .Append(",\"monster_id\":").Append(Creatures.Ids[it.Idx].ToString(CultureInfo.InvariantCulture))
          .Append(",\"uuid\":\"").Append(it.Uuid.ToString(CultureInfo.InvariantCulture)).Append('"');
        if (it.Gone) sb.Append(",\"gone\":true");
        else
            sb.Append(",\"x\":").Append(F(it.X)).Append(",\"y\":").Append(F(it.Y)).Append(",\"z\":").Append(F(it.Z))
              .Append(",\"hp\":").Append(AtlasClient.Ms(it.Hp)).Append(",\"max_hp\":").Append(AtlasClient.Ms(it.MaxHp))
              .Append(",\"seen_at\":").Append(AtlasClient.Ms(it.SeenAt));
        return sb.Append('}').ToString();
    }

    private void OnOutcome(AtlasClient.ReportOutcome o, Item it)
    {
        _inFlight = false;
        if (o.NewInstallId != null && o.NewInstallSecret != null) _setCreds(o.NewInstallId, o.NewInstallSecret);
        float now = _now();
        switch (o.Status)
        {
            case AtlasClient.ReportStatus.Ok:
                _sentTotal++;
                _backoff = MinBackoff;
                if (!_loggedFirst || it.Gone)
                {
                    _loggedFirst = true;
                    _info($"[Atlas] creature sighting sent: {Creatures.Names[it.Idx]} line {it.Line} {(it.Gone ? "gone" : "alive")}");
                }
                break;
            case AtlasClient.ReportStatus.Drop:   // 422 plausibility / 400: never accepted — don't loop on it
                _info($"[Atlas] creature sighting refused by the server: {Creatures.Names[it.Idx]} line {it.Line}");
                break;
            case AtlasClient.ReportStatus.RateLimited:
                Requeue(it);
                _notBefore = now + MathF.Max(RateLimitWait, _backoff);
                break;
            case AtlasClient.ReportStatus.ReRegister:
                _setCreds("", "");
                if (++_reRegisters > 3) { Stop("the server keeps refusing this install (401)"); break; }
                Requeue(it);
                _notBefore = now + MinBackoff;
                break;
            case AtlasClient.ReportStatus.Banned: Stop("install banned (403)"); break;
            case AtlasClient.ReportStatus.NoKey:  Stop("this build has no Atlas client key"); break;
            default:
                Requeue(it);
                _notBefore = now + _backoff;
                _backoff = MathF.Min(MaxBackoff, _backoff * 2f);
                break;
        }
    }

    // Back in front, unless a newer item for the same uuid arrived meanwhile.
    private void Requeue(Item it)
    {
        foreach (var p in _pending) if (p.Uuid == it.Uuid) return;
        if (_pending.Count >= MaxPending) return;
        _pending.Insert(0, it);
    }

    private void Stop(string reason)
    {
        _stopped = true;
        _pending.Clear();
        _info("[Atlas] creature sightings stopped for this session: " + reason);
    }

    private static string F(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);
}
