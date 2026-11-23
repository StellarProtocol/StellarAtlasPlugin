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

    private void PushStep()
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        // as many 25-point batches as fit in StageBudgetMs (at least one) — prestaging dozens of scenes stays smooth
        for (int b = 0; _jobPos < _job.Count && (b == 0 || ElapsedMs(t0) < StageBudgetMs); b++)
        {
            var sb = _sb.Clear().Append("__atlas_mpush(");
            int end = Math.Min(_job.Count, _jobPos + BatchSize);
            for (int i = _jobPos; i < end; i++)
            {
                var e = _job[i];
                if (i > _jobPos) sb.Append(',');
                sb.Append('{').Append(e.N.ToString(CultureInfo.InvariantCulture)).Append(',').Append(F(e.X)).Append(',')
                  .Append(F(e.Y)).Append(',').Append(F(e.Z)).Append(',');
                AppendLuaString(sb, e.Name);
                sb.Append('}');
            }
            sb.Append(')');
            if (!Call(sb.ToString())) { _jobPos = -1; return; }
            _jobPos = end;
        }
        Slow("push batch", t0);
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

    private static string F(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);
}
