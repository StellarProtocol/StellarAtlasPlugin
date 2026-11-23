using System;
using System.Globalization;
using System.Text;

namespace Stellar.Atlas;

// The Lua side of a push: staging batches (PushStep), the map refresh dispatch, and the literal formatting they use.
internal sealed partial class MapMarkers
{
    // The game rebuilds BOTH maps' flag lists synchronously inside this Dispatch (RefreshMap → our wrapper → ~MapCap
    // tables); that cost is the game's, but it lands in our call, so it runs alone on its own tick.
    private void Dispatch()
    {
        _dispatchPending = false;
        _lastDispatchAt = _lastSelectAt = _nowForDispatch;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        Call("__atlas_mdispatch()");
        Slow("map refresh dispatch (" + _paths.Length.ToString(CultureInfo.InvariantCulture) + " flags)", t0);
        if (_dispatchCurrent) { _dispatchCurrent = false; Settle(_nowForDispatch, "dispatched"); }
    }

    // Push format (2026-11-23, in-game "push batch 10.0 ms" for 25 entries): FLAT numbers — {n,x,y,z,k, n,x,y,z,k, …}
    // with k an index into a per-login name table (__atlas_names) that each name is sent to ONCE — so a batch builds
    // no per-entry Lua table and no repeated name strings; the wrapper turns them into the game's flag tables only when
    // the game builds a map. Prestage batches are ADAPTIVE: each batch's ms/entry (smoothed) sizes the next one to
    // PushTargetMs, starting at PushStartN, min 1. The current scene keeps BatchSize batches within StageBudgetMs
    // (at least one per tick) — it has priority and must not crawl.
    private const int    PushStartN = 5, PushMaxN = 50;
    private const double PushTargetMs = 1.5;
    private int _pushN = PushStartN;
    private double _pushMsPerEntry = -1;
    private readonly System.Collections.Generic.Dictionary<string, int> _nameIdx = new();
    private readonly StringBuilder _sbNames = new(512);

    private void PushStep()
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        double worstMs = 0; int worstN = 0, worstLen = 0, worstNew = 0;
        for (int b = 0; _jobPos < _job.Count && (b == 0 || (!_jobExtra && ElapsedMs(t0) < StageBudgetMs)); b++)
        {
            int end = Math.Min(_job.Count, _jobPos + (_jobExtra ? _pushN : BatchSize));
            string chunk = BuildPush(_jobPos, end, out int newNames);
            long c0 = System.Diagnostics.Stopwatch.GetTimestamp();
            if (!Call(chunk)) { _jobPos = -1; _nameIdx.Clear(); return; }   // names may be half-registered → resend all
            double ms = ElapsedMs(c0);
            int n = end - _jobPos;
            if (ms > worstMs) { worstMs = ms; worstN = n; worstLen = chunk.Length; worstNew = newNames; }
            if (_jobExtra) _pushN = Adapt(ref _pushMsPerEntry, ms, n, PushTargetMs, PushMaxN);
            _jobPos = end;
        }
        if (Slow(_jobExtra ? "push batch (prestage)" : "push batch", t0))
            _services.Log.Info($"[Atlas] push detail: worst call {worstMs:F1} ms for {worstN} entries " +
                $"({(worstN > 0 ? worstMs / worstN : 0):F2} ms/entry, {worstLen} chars, {worstNew} new names); next prestage batch {_pushN}");
        if (_jobPos < _job.Count) return;
        Call("__atlas_mcommit(" + _jobScene.ToString(CultureInfo.InvariantCulture) + ")");
        _jobPos = -1;
        if (_jobExtra) { if (CommittedStaged(_jobScene)) _dispatchPending = true; return; }   // silent unless on screen
        _dispatchPending = true;
        _dispatchCurrent = true;
        _lastSig = _jobSig;
        _lastSigAll = _jobSigAll;
        CommittedCurrent(_jobScene);
        CommitIcons(_jobScene, _job, extra: false);   // MapMarkers.Icons.cs: unit names + chips the icon pass looks for
    }

    // "__atlas_mpush({n,x,y,z,k,…}[,{k,"name",…}])" — the second table carries only names not sent before this login.
    private string BuildPush(int from, int end, out int newNames)
    {
        var sb = _sb.Clear().Append("__atlas_mpush({");
        _sbNames.Clear();
        newNames = 0;
        for (int i = from; i < end; i++)
        {
            var e = _job[i];
            if (!_nameIdx.TryGetValue(e.Name, out int k))
            {
                k = _nameIdx.Count + 1;
                _nameIdx[e.Name] = k;
                if (newNames++ > 0) _sbNames.Append(',');
                _sbNames.Append(CultureInfo.InvariantCulture, $"{k},");
                AppendLuaString(_sbNames, e.Name);
            }
            if (i > from) sb.Append(',');
            sb.Append(CultureInfo.InvariantCulture, $"{e.N},{e.X:0.##},{e.Y:0.##},{e.Z:0.##},{k}");
        }
        sb.Append('}');
        if (newNames > 0) sb.Append(",{").Append(_sbNames).Append('}');
        return sb.Append(')').ToString();
    }

    // Smoothed ms per item → the next batch size that fits `targetMs` (min 1, max `max`). One spike shrinks the batch,
    // a few normal calls grow it back.
    private static int Adapt(ref double ema, double ms, int n, double targetMs, int max)
    {
        if (n <= 0) return 1;
        double per = ms / n;
        ema = ema < 0 ? per : ema * 0.7 + per * 0.3;
        if (ema <= 0.0001) return max;
        return Math.Clamp((int)(targetMs / ema), 1, max);
    }

    // Lua string literal: quotes, backslashes and control characters escaped (names come from game tables / the server).
    private static void AppendLuaString(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (char ch in s)
        {
            if (ch == '"' || ch == '\\') sb.Append('\\').Append(ch);
            else if (ch < 0x20) sb.Append('\\').Append(((int)ch).ToString("D3", CultureInfo.InvariantCulture));
            else sb.Append(ch);
        }
        sb.Append('"');
    }

}
