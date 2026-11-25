using System.Collections.Generic;
using System.Globalization;

namespace Stellar.Atlas;

// Books THIS character has already read, from the game's own per-character record (GameLua.ReadBooks:
// CharSerialize.handbookData.unlockNoteReadingBookMap, keyed by NoteReadingBookTable id; 141 entries on the test
// character, checked live via the bridge 2026-11-25). A Books point whose book id is in the set is hidden like an opened
// chest (OpenedStore.Matches / MatchesIn → NearestModel.RefreshHidden, the map, the prestaged scenes, the guide's list).
//
// Book id of a point: the server's "reading" rows already carry the NoteReadingBookTable id as game_config_id (all 340
// cached rows, 2026-11-25: 1031001 "Sea Lyrics Collection" …), so it is used as is. A smaller id is a CollectionTable id
// (book rows 55000–55409+) and goes through the CollectionTable → AwardTable chain once per id (GameLua.BookIds, cached
// incl. misses). Never a baked list: the live game has newer books than the release_4.7 dump. Engram / resonance points
// (config id 0) can't be checked and stay shown. Live book entities are never tracked by the scanner (ClassifyLive has no
// Books branch), so only server points are affected.
//
// Cost: IsRead is one HashSet lookup (+ one dictionary lookup for a chain id); Refresh / ResolveChain are event or slow
// cadence only (Plugin.ReadBooks.cs).
internal sealed class ReadBooks
{
    private const int DirectMin = 1000000;   // NoteReadingBookTable ids are 7-digit (1031001+); below = CollectionTable id
    private const int ChainBatch = 32;

    private HashSet<int> _read = new(), _scratch = new();
    private readonly Dictionary<int, int> _bookOf = new();
    private readonly List<int> _need = new();

    public int Version { get; private set; }      // bumps when the read set (or a resolved chain id) changes
    public int Count { get; private set; } = -1;  // the record's entry count; -1 = not read yet for this character

    public void Clear()
    {
        if (_read.Count > 0) Version++;
        _read.Clear();
        Count = -1;
    }

    public bool IsRead(AtlasPoint p)
    {
        if (p.Chip != Chip.Books || p.ConfigId <= 0 || _read.Count == 0) return false;
        int cfg = p.ConfigId;
        int book = cfg >= DirectMin ? cfg : (_bookOf.TryGetValue(cfg, out int b) ? b : 0);
        return book > 0 && _read.Contains(book);
    }

    // -1 = not readable yet (Lua / CharSerialize), 0 = unchanged, 1 = the set changed. `force` re-reads the ids even when
    // the count is the same (character / scene change, a book just read).
    public int Refresh(GameLua lua, bool force)
    {
        int n = lua.ReadBooks(force ? -1 : Count, out var ids);
        if (n < 0) return -1;
        Count = n;
        if (ids == null) return 0;
        _scratch.Clear();
        foreach (var s in ids.Split(','))
            if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) && id > 0) _scratch.Add(id);
        if (_scratch.SetEquals(_read)) return 0;
        (_read, _scratch) = (_scratch, _read);
        Version++;
        return 1;
    }

    // Books points carrying a CollectionTable id not resolved yet → up to ChainBatch of them in one Lua call. True when
    // something was resolved (more may remain: the caller asks again on its next tick).
    public bool ResolveChain(AtlasPoint[] points, GameLua lua)
    {
        _need.Clear();
        foreach (var p in points)
        {
            int cfg = p.ConfigId;
            if (p.Chip != Chip.Books || cfg <= 0 || cfg >= DirectMin || _bookOf.ContainsKey(cfg) || _need.Contains(cfg)) continue;
            _need.Add(cfg);
            if (_need.Count >= ChainBatch) break;
        }
        if (_need.Count == 0 || !lua.BookIds(_need, _bookOf)) return false;
        Version++;
        return true;
    }

    public int HiddenIn(AtlasPoint[] points)
    {
        int n = 0;
        foreach (var p in points) if (IsRead(p)) n++;
        return n;
    }
}
