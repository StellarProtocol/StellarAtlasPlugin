using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Stellar.Atlas;

// Creature lines dialog strings. Polled by the window refresh → every string is cached and rebuilt only when what it
// shows changes (per pool slot for the list: row identity + minute / 5 s buckets + language; one signature each for the
// header status and the detail line). Distances only in Asteria Plains (lines are per scene; another map's coordinates
// give no distance) — elsewhere the game's own name for the map.
public sealed partial class Plugin
{
    private readonly long[] _clSpotSig = new long[ClPool], _clAgeSig = new long[ClPool];
    private readonly string[] _clSpot = new string[ClPool], _clAge = new string[ClPool];
    private readonly Dictionary<int, string> _clLineText = new();
    private string? _clLang;
    private string _clSuffix = "", _clSuffixDot = "";
    private long _clStatusSig, _clDetailSig, _clAllSig;
    private string _clStatus = "", _clDetail = "", _clAll2 = "", _clServerUpper = "", _clServerFor = "";

    private void ClCheckLang()
    {
        string lang = _loc.Language;
        if (ReferenceEquals(lang, _clLang)) return;
        _clLang = lang;
        _clLineText.Clear();
        Array.Clear(_clSpotSig, 0, ClPool); Array.Clear(_clAgeSig, 0, ClPool);
        _clStatusSig = _clDetailSig = _clAllSig = 0;
        _clSuffix = _loc.T("at.cl.seenByPlayer");
        _clSuffixDot = " · " + _clSuffix;
    }

    private string ClLineText(int line)
    {
        ClCheckLang();
        if (_clLineText.TryGetValue(line, out var s)) return s;
        return _clLineText[line] = _loc.TFormat("at.cl.line", line);
    }

    private string ClAllLabel()
    {
        ClCheckLang();
        int m = ClLineCount;
        if (m == _clAllSig && _clAll2.Length > 0) return _clAll2;
        _clAllSig = m;
        return _clAll2 = _loc.TFormat("at.cl.all", m);
    }

    private string ClSpotText(int s)
    {
        int i = _clFirst + s;
        if (i >= _clN) return "";
        ClCheckLang();
        ref var r = ref _clRows[i];
        long next = r.Kind == ClKind.Absent ? _cre.Status?.ByIdx[_clIdx]?.NextSpawnAt ?? 0 : 0;
        long nowMs = NowMs();
        int mins = next > 0 ? MinutesUntil(next, nowMs) : 0;
        long sig = 17L * 31 + r.Line; sig = sig * 31 + (int)r.Kind; sig = sig * 31 + RuntimeHelpers.GetHashCode(r.Spot);
        sig = sig * 31 + next; sig = sig * 31 + mins; sig = sig * 31 + _clVersion;
        if (sig == 0) sig = 1;
        if (sig == _clSpotSig[s]) return _clSpot[s];
        _clSpotSig[s] = sig;
        return _clSpot[s] = r.Kind switch
        {
            ClKind.Alive   => r.Spot ?? "",
            ClKind.NotSeen => _loc.T("at.cl.notSeen"),
            ClKind.Mine    => _loc.T("at.cl.notOnYours"),
            _ => next <= 0 ? _loc.T("at.cre.tag.absent")
                 : mins > 0 ? _loc.TFormat("at.cl.nextSpawn", _cre.TimeText(next), InText(mins))
                 : _loc.TFormat("at.cl.nextSpawnAt", _cre.TimeText(next)),
        };
    }

    private string ClPlayerSuffix(int s)
    {
        int i = _clFirst + s;
        if (i >= _clN) return "";
        ref var r = ref _clRows[i];
        if (!r.Player || r.Kind != ClKind.Alive) return "";
        ClCheckLang();
        return string.IsNullOrEmpty(r.Spot) ? _clSuffix : _clSuffixDot;
    }

    private string ClAgeText(int s)
    {
        int i = _clFirst + s;
        if (i >= _clN) return "";
        ref var r = ref _clRows[i];
        if (r.UpdatedAt <= 0) return "";
        ClCheckLang();
        int b = AgeSecondsBucket((NowMs() - r.UpdatedAt) / 1000);
        long sig = 17L * 31 + r.Line; sig = sig * 31 + b; sig = sig * 31 + r.UpdatedAt;
        if (sig == 0) sig = 1;
        if (sig == _clAgeSig[s]) return _clAge[s];
        _clAgeSig[s] = sig;
        return _clAge[s] = _loc.TFormat("at.cl.ago", AgeBucketText(b));
    }

    // "SEA · alive on 3 of 40 lines · you are on line 4 · updated 20 s ago"
    private string ClStatusText()
    {
        ClCheckLang();
        var st = _cre.Status;
        var info = st?.ByIdx[_clIdx];
        int alive = 0;
        if (info != null) foreach (var l in info.Lines) if (l.Alive) alive++;
        int m = ClLineCount, line = CreLine;
        int age = st != null && st.UpdatedAt > 0 ? AgeSecondsBucket((NowMs() - st.UpdatedAt) / 1000) : -1;
        long sig = 17L * 31 + alive; sig = sig * 31 + m; sig = sig * 31 + line; sig = sig * 31 + age;
        sig = sig * 31 + _clIdx; sig = sig * 31 + RuntimeHelpers.GetHashCode(_creServer);
        if (sig == 0) sig = 1;
        if (sig == _clStatusSig) return _clStatus;
        _clStatusSig = sig;
        if (!ReferenceEquals(_clServerFor, _creServer)) { _clServerFor = _creServer; _clServerUpper = _creServer.ToUpperInvariant(); }
        string text = _clServerUpper;
        if (st != null) text = Join(text, m > 0 ? _loc.TFormat("at.cl.aliveOf", alive, m) : _loc.TFormat("at.cl.alive", alive));
        if (line > 0) text = Join(text, _loc.TFormat("at.cl.youAre", line));
        if (age >= 0) text = Join(text, _loc.TFormat("at.cl.updated", AgeBucketText(age)));
        return _clStatus = text;
    }

    // "Line 5 · Scout E · HP 2% · 399 m SE" (Asteria Plains: distance + compass; elsewhere the map name)
    private string ClDetailText()
    {
        ClCheckLang();
        if (_clSel <= 0) return _loc.T("at.cl.pick");
        bool has = ClSelected(out var l);
        bool pos = LinePos(_clIdx, _clSel, out float x, out float y, out float z);
        bool near = InCreatureScene && _scanner.HasPlayerPos && pos;
        int dist = 0, dir = 0;
        if (near)
        {
            var p = _scanner.PlayerPos;
            dist = (int)MathF.Round(NearestModel.Dist(x, y, z, true, p.x, p.y, p.z));
            dir = NearestModel.Compass(x - p.x, z - p.z);
        }
        int hp = has && l!.Alive && l.MaxHp > 0 ? (int)Math.Clamp(l.Hp * 100 / l.MaxHp, 0, 100) : -1;
        long sig = 17L * 31 + _clSel; sig = sig * 31 + hp; sig = sig * 31 + (has ? RuntimeHelpers.GetHashCode(l!.Spot) : 0);
        sig = sig * 31 + (has && l!.Alive ? 1 : 0); sig = sig * 31 + dist; sig = sig * 31 + dir; sig = sig * 31 + (near ? 1 : 0);
        // the guide to this line from another map: via the teleport zone towards Asteria Plains, or pending until arrival
        int gstate = _guide.Active && _guide.Cre == _clIdx + 1 && _guide.Line == _clSel ? (_guide.Pending ? 1 : _guide.ViaTeleport ? 2 : 0) : 0;
        sig = sig * 31 + _clIdx; sig = sig * 31 + (pos ? 1 : 0); sig = sig * 31 + gstate;
        if (sig == 0) sig = 1;
        if (sig == _clDetailSig) return _clDetail;
        _clDetailSig = sig;
        string text = ClLineText(_clSel);
        if (!has) text = Join(text, _clSel == CreLine ? _loc.T("at.cl.notOnYours") : _loc.T("at.cl.notSeen"));
        else if (!l!.Alive) text = Join(text, _loc.T("at.cre.tag.absent"));
        else text = Join(Join(text, l.Spot ?? ""), hp >= 0 ? _loc.TFormat("at.cre.hp", hp) : "");
        if (near) text = Join(text, DistText(dist) + " " + _loc.T(DirKey(dir)));
        else if (pos) text = Join(text, CreSceneName());
        if (gstate == 1) text = Join(text, _loc.TFormat("at.cl.guidePending", CreSceneName()));
        else if (gstate == 2) text = Join(text, _loc.TFormat("at.cl.guideVia", CreSceneName()));
        return _clDetail = text;
    }
}
