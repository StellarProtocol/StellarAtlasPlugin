namespace Stellar.Atlas;

// Scheduling of the read-book record (ReadBooks.cs). SlowTick (1 Hz, world-ready) does nothing unless:
//   • forced — character change, scene change, a book just read (OnLiveOpened): the ids are re-read;
//   • the scene's point array was replaced (disk cache / server fetch): uncached CollectionTable ids are resolved and the
//     hidden count re-logged;
//   • ReadBooksEvery passed: ONE Lua call that only counts the record (the ids are built only if the count moved), so a
//     book read while nothing else happens is picked up within ~12 s.
// A changed set bumps ReadBooks.Version, which OpenedStore.Version includes → RefreshHidden re-marks the points on its
// next pass (NEAREST, the map's 2 s re-check, the guide's "next nearest").
public sealed partial class Plugin
{
    private const float ReadBooksEvery = 12f;

    private readonly ReadBooks _readBooks = new();
    private bool _rbForce = true, _rbChainMore;
    private float _rbNextAt;
    private AtlasPoint[]? _rbPoints;
    private int _rbLogScene = -1, _rbLogCount = -2, _rbLogHidden = -1;

    private void NoteReadBooksDirty() => _rbForce = true;

    private void ResetReadBooks()
    {
        _readBooks.Clear();
        _rbForce = true;
        _rbPoints = null;
        _rbLogScene = -1;
    }

    private void TickReadBooks(float now)
    {
        if (_scene <= 0 || !CharReady || !_services.Lua.Ready) return;
        var pts = _model.Points;
        bool newPoints = !ReferenceEquals(pts, _rbPoints);
        if (!_rbForce && !newPoints && !_rbChainMore && now < _rbNextAt) return;
        _rbPoints = pts;

        bool changed = false;
        if (newPoints || _rbChainMore) changed = _rbChainMore = _readBooks.ResolveChain(pts, _lua);
        if (_rbForce || now >= _rbNextAt)
        {
            int r = _readBooks.Refresh(_lua, _rbForce);
            if (r < 0) return;   // CharSerialize not readable yet → retried next second (force kept)
            _rbForce = false;
            _rbNextAt = now + ReadBooksEvery;
            changed |= r > 0;
        }
        if (!changed && !newPoints) return;

        int hidden = _readBooks.HiddenIn(pts);
        if (_scene != _rbLogScene || _readBooks.Count != _rbLogCount || hidden != _rbLogHidden)
        {
            _rbLogScene = _scene; _rbLogCount = _readBooks.Count; _rbLogHidden = hidden;
            _services.Log.Info($"[Atlas] read books: {_readBooks.Count} (hidden {hidden} points in scene {_scene})");
        }
        if (changed) RecomputeNearest();
    }
}
