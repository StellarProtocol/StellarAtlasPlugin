using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.Atlas;

// Creature lines dialog (user-approved 2026-11-24): a child window for ONE creature, opened by clicking its row in the
// MAGICAL CREATURES panel (which no longer guides directly). It lists the creature's lines from the existing
// /v1/creatures cache (no extra request): "Alive only" (default) or "All <M>" (only when the server sends line_count).
// A row is selected by a click; "Guide me there" puts the game's guide marker on that line's position (creature guide
// path: Asteria Plains goal from any map, existing height rules). Double-click is NOT used: SelectableElement has no
// native double-click.
//
// Window: house style, opens centred on the Atlas window and in front (WindowCenterer.OpenCentred; it fronts a window
// that is already visible — CLAUDE.md rule 5 exception). Clicking another creature row while it is open switches the
// creature in place (fronted, not re-centred: the player may have moved it). Closed on logout with the main window.
//
// Data (this file): the shown rows are rebuilt only when their inputs change — the status object (a new fetch), the
// creature, the mode, the player's line / scene — into a reused array (grown on demand, never per tick). The view
// (Plugin.CreatureLines.View.cs) and its cached strings (Plugin.CreatureLines.Text.cs) index it.
public sealed partial class Plugin
{
    private const float ClWidth = 440f;

    private enum ClKind : byte { Alive, Absent, NotSeen, Mine }

    private struct ClRow
    {
        public int Line;
        public ClKind Kind;
        public bool Player, HasPos;
        public float X, Y, Z;
        public long Hp, MaxHp, UpdatedAt;
        public string Spot;
    }

    private IWindowControl? _clWindow;
    private WindowCenterer? _clCenterer;
    private int _clIdx;                     // Creatures index shown
    private bool _clAll;                    // "All <M>" chosen (else "Alive only")
    private int _clSel;                     // selected line, 0 = none
    private ClRow[] _clRows = new ClRow[64];
    private int _clN, _clFirst;
    private int[] _clByLine = new int[64];  // line → index into the creature's Lines (All mode), -1 = no data
    private object? _clBuiltFor;            // status object the rows were built from
    private int _clBuiltIdx = -1, _clBuiltLine = -1, _clVersion;
    private bool _clBuiltAll, _clBuiltIn;

    private bool ClShown => _clWindow is { IsShown: true };

    private void RegisterCreatureLines()
    {
        _clWindow = _services.Windows.Register(new WindowRegistration(
            Spec: new WindowSpec(
                Id:          "atlas.creature",
                Title:       Creatures.Names[0],
                DefaultRect: new WindowRect(260f, 160f, ClWidth, 0f),
                Category:    WindowCategory.Tools,
                Style:       WindowPanelStyle.GlassMenu)
            { Draggable = true, Closable = true, StartVisible = false,
              TitleProvider = () => Creatures.Names[_clIdx],   // creature names stay English (user decision)
              Prebuild = true,                                 // hidden: 0 rows (_clN stays 0 until opened)
              ShouldRender = InWorld },
            Root: BuildCreatureLinesRoot(),
            OnClose: CloseCreatureLines));
        _clCenterer = new WindowCenterer(_services.Framework, _clWindow, WindowCenterer.CentreOf(_window));
    }

    // Panel row click: open for this creature, or switch an open dialog to it.
    private void OpenCreatureLines(int idx)
    {
        if (_clWindow == null) return;
        if (idx != _clIdx) { _clIdx = idx; _clSel = 0; _clBuiltFor = null; }
        RebuildClRows();
        if (ClShown) { _clWindow.BringToFront(); return; }
        _clCenterer!.OpenCentred();
    }

    private void CloseCreatureLines()
    {
        _clCenterer?.Stop();
        _clWindow?.SetVisible(false);
    }

    private void SetClMode(bool all)
    {
        if (all == _clAll) return;
        _clAll = all;
        RebuildClRows();
    }

    // ScanTick (2 Hz) while the dialog is shown: rebuild only when an input changed.
    private void TickCreatureLines()
    {
        if (!ClShown) return;
        var st = _cre.Status;
        if (!ReferenceEquals(st, _clBuiltFor) || _clIdx != _clBuiltIdx || _clAll != _clBuiltAll || CreLine != _clBuiltLine
            || InCreatureScene != _clBuiltIn)
            RebuildClRows();
    }

    private int ClLineCount => _cre.Status?.LineCount ?? 0;
    private bool ClAllMode => _clAll && ClLineCount > 0;

    private void RebuildClRows()
    {
        var st = _cre.Status;
        _clBuiltFor = st; _clBuiltIdx = _clIdx; _clBuiltAll = _clAll; _clBuiltLine = CreLine; _clBuiltIn = InCreatureScene;
        _clVersion++;
        _clN = 0;
        var info = st?.ByIdx[_clIdx];
        var lines = info?.Lines ?? Array.Empty<AtlasClient.CreatureLine>();
        if (ClAllMode)
        {
            int m = ClLineCount;
            if (_clByLine.Length <= m) _clByLine = new int[m * 2 + 1];
            Array.Fill(_clByLine, -1, 0, m + 1);
            for (int i = 0; i < lines.Length; i++) if (lines[i].Line >= 1 && lines[i].Line <= m) _clByLine[lines[i].Line] = i;
            EnsureClRows(m);
            for (int line = 1; line <= m; line++)
            {
                int k = _clByLine[line];
                if (k >= 0) AddClRow(lines[k]);
                else _clRows[_clN++] = new ClRow { Line = line, Kind = ClKind.NotSeen, Spot = "" };
            }
            return;
        }
        // Alive only: alive lines by line number, then the player's own line when it is not among them
        EnsureClRows(lines.Length + 1);
        bool mineAlive = false;
        int mine = CreLine;
        foreach (var l in lines)
        {
            if (!l.Alive) continue;
            if (l.Line == mine) mineAlive = true;
            int k = _clN;
            AddClRow(l);
            var row = _clRows[k];
            while (k > 0 && _clRows[k - 1].Line > row.Line) { _clRows[k] = _clRows[k - 1]; k--; }
            _clRows[k] = row;
        }
        if (mine > 0 && !mineAlive) _clRows[_clN++] = new ClRow { Line = mine, Kind = ClKind.Mine, Spot = "" };
    }

    private void EnsureClRows(int n)
    {
        if (_clRows.Length < n) _clRows = new ClRow[Math.Max(n, _clRows.Length * 2)];
    }

    private void AddClRow(AtlasClient.CreatureLine l)
    {
        var r = new ClRow
        {
            Line = l.Line, Kind = l.Alive ? ClKind.Alive : ClKind.Absent, Player = l.Player, HasPos = l.HasPos,
            X = l.X, Y = l.Y, Z = l.Z, Hp = l.Hp, MaxHp = l.MaxHp, UpdatedAt = l.UpdatedAt, Spot = l.Spot,
        };
        if (!r.HasPos && Creatures.FixedSpot(_clIdx, out r.X, out r.Y, out r.Z)) r.HasPos = true;   // boarlets: their spot
        _clRows[_clN++] = r;
    }

    // The selected line's data (from the cache, whatever the mode shows). False when that line has no data.
    private bool ClSelected(out AtlasClient.CreatureLine? line)
    {
        line = null;
        if (_clSel <= 0) return false;
        var info = _cre.Status?.ByIdx[_clIdx];
        if (info == null) return false;
        foreach (var l in info.Lines) if (l.Line == _clSel) { line = l; return true; }
        return false;
    }

    // Where a line's creature is: the server position, else the fixed spot (scheduled boarlets).
    private bool LinePos(int idx, int lineNo, out float x, out float y, out float z)
    {
        var info = _cre.Status?.ByIdx[idx];
        if (info != null)
            foreach (var l in info.Lines)
                if (l.Line == lineNo && l.HasPos) { x = l.X; y = l.Y; z = l.Z; return true; }
        return Creatures.FixedSpot(idx, out x, out y, out z);
    }

    private bool ClCanGuide => _clSel > 0 && ClSelected(out _) && LinePos(_clIdx, _clSel, out _, out _, out _);

    // "Guide me there": the creature guide path pinned to the selected line.
    private void GuideToSelectedLine()
    {
        if (!ClCanGuide || !LinePos(_clIdx, _clSel, out float x, out float y, out float z)) return;
        bool away = _scene != Creatures.Scene;
        _guide = new GuideState { Active = true, Cre = _clIdx + 1, Line = _clSel, Scene = Creatures.Scene, X = x, Y = y, Z = z,
                                  HasY = away || y != 0f };
        if (!_guide.HasY) TryGroundHeight(_services.Framework.TimeNow);
        SetMarker();
    }
}
