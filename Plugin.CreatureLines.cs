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
// creature at the same place (one window per creature, see ClEnsureWindow; fronted, not re-centred: the player may have
// moved it). Closed on logout with the main window. From another map the guide goes to that scene's teleport zone
// towards Asteria Plains, or waits for arrival when there is none (GameLua __atlas_guide; Plugin.Guide.cs Remark).
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

    private int _clIdx;                     // Creatures index shown
    private bool _clAll;                    // "All <M>" chosen (else "Alive only")
    private int _clSel;                     // selected line, 0 = none
    private ClRow[] _clRows = new ClRow[64];
    private int _clN, _clFirst;
    private int[] _clByLine = new int[64];  // line → index into the creature's Lines (All mode), -1 = no data
    private object? _clBuiltFor;            // status object the rows were built from
    private int _clBuiltIdx = -1, _clBuiltLine = -1, _clVersion;
    private bool _clBuiltAll, _clBuiltIn;

    // ONE window per creature, registered on its first open. WindowSpec.TitleProvider is evaluated only when a window is
    // built (and on a theme / language change), never per refresh, so a single dialog kept the first creature's name in
    // its title bar (in-game 2026-11-24: "Golden Nappo" over Silver Nappo). Each creature's window has its own fixed title
    // (names are English everywhere); switching hides the shown one and opens the other at the same rect. No Prebuild:
    // five prebuilt dialogs would cost five builds at start-up for windows most sessions never open.
    private readonly IWindowControl?[] _clWindows = new IWindowControl?[Creatures.Count];
    private readonly WindowCenterer?[] _clCenterers = new WindowCenterer?[Creatures.Count];

    private IWindowControl? ClWindow => _clWindows[_clIdx];
    private bool ClShown => ClWindow is { IsShown: true };

    private IWindowControl ClEnsureWindow(int idx)
    {
        if (_clWindows[idx] is { } w) return w;
        w = _services.Windows.Register(new WindowRegistration(
            Spec: new WindowSpec(
                Id:          "atlas.creature." + Creatures.Ids[idx],
                Title:       Creatures.Names[idx],
                DefaultRect: new WindowRect(260f, 160f, ClWidth, 0f),
                Category:    WindowCategory.Tools,
                Style:       WindowPanelStyle.GlassMenu)
            { Draggable = true, Closable = true, StartVisible = false, ShouldRender = InWorld },
            Root: BuildCreatureLinesRoot(),
            OnClose: CloseCreatureLines));
        _clWindows[idx] = w;
        _clCenterers[idx] = new WindowCenterer(_services.Framework, w, WindowCenterer.CentreOf(_window));
        return w;
    }

    // Panel row click: open for this creature, or switch an open dialog to it (same place, in front).
    private void OpenCreatureLines(int idx)
    {
        var prev = ClWindow;
        bool prevShown = prev is { IsShown: true };
        if (idx != _clIdx) { _clIdx = idx; _clSel = 0; _clBuiltFor = null; }
        RebuildClRows();
        var w = ClEnsureWindow(idx);
        if (prevShown && !ReferenceEquals(prev, w))
        {
            var r = prev!.Rect;
            prev.SetVisible(false);
            w.SetRect(new WindowRect(r.X, r.Y, ClWidth, r.Height));
            w.SetVisible(true);
            w.BringToFront();
            return;
        }
        if (w.IsShown) { w.BringToFront(); return; }
        _clCenterers[idx]!.OpenCentred();
    }

    private void CloseCreatureLines()
    {
        for (int i = 0; i < Creatures.Count; i++)
        {
            _clCenterers[i]?.Stop();
            if (_clWindows[i] is { IsShown: true } w) w.SetVisible(false);
        }
    }

    private void RemoveCreatureLines()
    {
        for (int i = 0; i < Creatures.Count; i++) { _clCenterers[i]?.Stop(); _clWindows[i]?.Remove(); _clWindows[i] = null; }
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
        bool away = !InCreatureScene;
        _guide = new GuideState { Active = true, Cre = _clIdx + 1, Line = _clSel, Scene = Creatures.Scene, X = x, Y = y, Z = z,
                                  HasY = away || y != 0f };
        if (!_guide.HasY) TryGroundHeight(_services.Framework.TimeNow);
        SetMarker();
    }
}
