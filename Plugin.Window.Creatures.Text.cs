using System;
using System.Runtime.CompilerServices;

namespace Stellar.Atlas;

// The MAGICAL CREATURES panel's strings. Every Func here is polled by the window refresh, so each string is cached and
// rebuilt only when what it shows changes: a per-slot signature of (creature, kind, line, lines set, spot, HP %, the
// minute buckets of "in 1 h 12 m" / "4 min ago", fighting) + the language. Times: NowMs() (Plugin.Creatures.cs).
public sealed partial class Plugin
{
    private readonly string[] _creTag = new string[Creatures.Count], _creDetail = new string[Creatures.Count];
    private readonly long[] _creSig = new long[Creatures.Count];
    private string? _creLang;

    // tag (true) or the detail after it (false) for display slot s
    private string CreText(int s, bool tag)
    {
        if (s < 0 || s >= _cre.Count) return "";
        string lang = _loc.Language;
        if (!ReferenceEquals(lang, _creLang)) { _creLang = lang; Array.Clear(_creSig, 0, _creSig.Length); _capSig = 0; _emptySig = 0; }
        ref var r = ref _cre.Out[s];
        long nowMs = NowMs();
        int spawnMin = r.NextSpawnAt > 0 ? MinutesUntil(r.NextSpawnAt, nowMs) : int.MinValue;
        int seenMin = r.Kind == CreatureModel.Kind.Player ? MinutesSince(r.SeenAt, nowMs) : 0;
        long sig = 17;
        sig = sig * 31 + r.Idx; sig = sig * 31 + (int)r.Kind; sig = sig * 31 + r.Line; sig = sig * 31 + r.HpPct;
        sig = sig * 31 + RuntimeHelpers.GetHashCode(r.Lines); sig = sig * 31 + RuntimeHelpers.GetHashCode(r.Spot);
        sig = sig * 31 + spawnMin; sig = sig * 31 + r.NextSpawnAt; sig = sig * 31 + seenMin; sig = sig * 31 + (r.Fighting ? 1 : 0);
        if (sig == 0) sig = 1;
        if (sig != _creSig[s] || _creTag[s] == null)
        {
            _creSig[s] = sig;
            BuildCreText(in r, spawnMin, seenMin, out _creTag[s], out _creDetail[s]);
        }
        return tag ? _creTag[s] : _creDetail[s];
    }

    private void BuildCreText(in CreatureModel.Row r, int spawnMin, int seenMin, out string tag, out string detail)
    {
        string hp = r.HpPct >= 0 ? _loc.TFormat("at.cre.hp", r.HpPct) : "";
        switch (r.Kind)
        {
            case CreatureModel.Kind.Live:
                tag = _loc.T("at.cre.tag.live");
                detail = r.Fighting && hp.Length > 0 ? Join(hp, _loc.T("at.cre.fighting")) : hp;
                break;
            case CreatureModel.Kind.Here:
                tag = _loc.T("at.cre.tag.here");
                detail = Join(r.Spot, hp);
                break;
            case CreatureModel.Kind.Other:
                tag = r.LineCount > 1 ? _loc.TFormat("at.cre.tag.lines", r.Lines) : _loc.TFormat("at.cre.tag.line", r.Line);
                detail = r.LineCount > 1 ? _loc.T("at.cre.notYourLine") : Join(r.Spot, hp);
                break;
            case CreatureModel.Kind.Player:
                tag = _loc.T("at.cre.tag.player");
                detail = _loc.TFormat("at.cre.playerSeen", r.Line, AgeMinutesText(seenMin));
                break;
            default:
                tag = _loc.T("at.cre.tag.absent");
                detail = NextSpawnText(r.NextSpawnAt, spawnMin);
                break;
        }
    }

    // "next spawn 14:00 (in 1 h 12 m)" / "next spawn 14:00" once the time has passed / "" without a schedule.
    private string NextSpawnText(long atMs, int minutes)
    {
        if (atMs <= 0) return "";
        string time = _cre.TimeText(atMs);
        return minutes > 0 ? _loc.TFormat("at.cre.nextSpawn", time, InText(minutes)) : _loc.TFormat("at.cre.nextSpawnAt", time);
    }

    private string InText(int minutes) => minutes >= 60
        ? _loc.TFormat("at.cre.inHM", minutes / 60, minutes % 60)
        : _loc.TFormat("at.cre.inM", minutes);

    private string AgeMinutesText(int minutes) => minutes >= 60 * 48 ? _loc.TFormat("at.age.d", minutes / (60 * 24))
        : minutes >= 60 ? _loc.TFormat("at.age.h", minutes / 60)
        : _loc.TFormat("at.age.min", Math.Max(1, minutes));

    private static int MinutesUntil(long atMs, long nowMs) => (int)Math.Clamp((atMs - nowMs + 59999) / 60000, -1, 100000);
    private static int MinutesSince(long atMs, long nowMs) => atMs <= 0 ? 0 : (int)Math.Clamp((nowMs - atMs) / 60000, 0, 100000);

    private static string Join(string a, string b) => a.Length == 0 ? b : b.Length == 0 ? a : a + " · " + b;

    // ── caption: "Your line 4 · updated 20 s ago" (muted) / "Offline · updated 5 min ago" ──────────────────────────
    private long _capSig;
    private string _capText = "";

    private string CreCaption()
    {
        long updated = _cre.Status?.UpdatedAt ?? 0;
        if (!_creHasData || updated <= 0) { updated = 0; }
        int age = updated > 0 ? AgeSecondsBucket((NowMs() - updated) / 1000) : -1;
        long sig = 17L * 31 + CreLine; sig = sig * 31 + age; sig = sig * 31 + (_creOffline ? 1 : 0);
        sig = sig * 31 + RuntimeHelpers.GetHashCode(_loc.Language);
        if (sig == _capSig) return _capText;
        _capSig = sig;
        string ago = age < 0 ? "" : AgeBucketText(age);
        if (_creOffline && ago.Length > 0) _capText = _loc.TFormat("at.cre.capOffline", ago);
        else if (ago.Length == 0) _capText = CreLine > 0 ? _loc.TFormat("at.cre.capLineOnly", CreLine) : "";
        else _capText = CreLine > 0 ? _loc.TFormat("at.cre.capLine", CreLine, ago) : _loc.TFormat("at.cre.cap", ago);
        return _capText;
    }

    // Seconds bucketed so the caption changes at most every 5 s under a minute, then per minute / hour
    // (encoded: s, 1000 + min, 2000 + h).
    private static int AgeSecondsBucket(long s)
    {
        if (s < 0) s = 0;
        if (s < 60) return (int)(s / 5 * 5);
        if (s < 3600) return 1000 + (int)(s / 60);
        return 2000 + (int)Math.Min(999, s / 3600);
    }

    private string AgeBucketText(int b) => b >= 2000 ? _loc.TFormat("at.age.h", b - 2000)
                                         : b >= 1000 ? _loc.TFormat("at.age.min", b - 1000)
                                         : _loc.TFormat("at.age.s", b);

    // ── empty tile lines ──────────────────────────────────────────────────────────────────────────────────────────
    private long _emptySig;
    private string _empty1 = "", _empty2 = "";

    private string CreEmptyText(bool first)
    {
        var view = CurrentCreView();
        int nextIdx = -1;
        long nextAt = long.MaxValue, nowMs = NowMs();
        if (view == CreView.NoneAlive)
            for (int i = 0; i < _cre.Count; i++)
            {
                ref var r = ref _cre.Out[i];
                if (r.NextSpawnAt > nowMs && r.NextSpawnAt < nextAt) { nextAt = r.NextSpawnAt; nextIdx = r.Idx; }
            }
        int mins = nextIdx >= 0 ? MinutesUntil(nextAt, nowMs) : 0;
        long sig = 17L * 31 + (int)view; sig = sig * 31 + nextIdx; sig = sig * 31 + mins; sig = sig * 31 + (nextIdx >= 0 ? nextAt : 0);
        sig = sig * 31 + RuntimeHelpers.GetHashCode(_loc.Language);
        if (sig != _emptySig)
        {
            _emptySig = sig;
            switch (view)
            {
                case CreView.Offline: _empty1 = _loc.T("at.cre.offline"); _empty2 = _loc.T("at.cre.offlineChests"); break;
                case CreView.NoneAlive:
                    _empty1 = _loc.T("at.cre.noneAlive");
                    _empty2 = nextIdx >= 0
                        ? _loc.TFormat("at.cre.noneNext", Creatures.Names[nextIdx], _cre.TimeText(nextAt), InText(Math.Max(1, mins))) : "";
                    break;
                default: _empty1 = _loc.T("at.cre.loading"); _empty2 = ""; break;
            }
        }
        return first ? _empty1 : _empty2;
    }
}
