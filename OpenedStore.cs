using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Stellar.Atlas;

// Chests THIS character opened, per character (the game itself stops sending an opened chest to that character for good,
// validated 2026-11-23 — so an opened chest must also leave Atlas's list for that character, but only for them).
// File: <plugin data>/opened/<charId>.txt, one "scene uuid x z" line per chest. Read once when the character changes;
// written (whole file, a few KB at most) off the main thread after each new entry.
//
// Matching: by uuid (stable per physical chest), and — for server rows that don't carry a uuid yet (questlog seeds) — by
// position: same scene, chest chip, within MatchRadius on (x, z).
internal sealed class OpenedStore
{
    private const float MatchRadius = 3f;

    private struct Entry { public int Scene; public long Uuid; public float X, Z; }

    private readonly AtlasClient _io;
    private readonly List<Entry> _all = new();
    private readonly HashSet<long> _uuids = new();
    private readonly List<Entry> _scene = new();   // entries of the current scene (the per-refresh position check)
    private long _charId;
    private int _sceneId;

    public OpenedStore(AtlasClient io) => _io = io;

    public int Version { get; private set; }   // bumps on any change (cheap "recount" trigger)

    private static string FileName(long charId) => "opened/" + charId.ToString(CultureInfo.InvariantCulture) + ".txt";

    public void SetCharacter(long charId)
    {
        if (charId == _charId) return;
        _charId = charId;
        _all.Clear(); _uuids.Clear();
        if (charId != 0 && _io.ReadFile(FileName(charId)) is { } bytes)
        {
            foreach (var line in Encoding.UTF8.GetString(bytes).Split('\n'))
            {
                var p = line.Trim().Split(' ');
                if (p.Length < 4) continue;
                if (!int.TryParse(p[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int s)
                    || !long.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long u)
                    || !float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
                    || !float.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float z)) continue;
                _all.Add(new Entry { Scene = s, Uuid = u, X = x, Z = z });
                _uuids.Add(u);
            }
        }
        RebuildScene();
    }

    public void SetScene(int sceneId) { if (sceneId != _sceneId) { _sceneId = sceneId; RebuildScene(); } }

    private void RebuildScene()
    {
        _scene.Clear();
        foreach (var e in _all) if (e.Scene == _sceneId) _scene.Add(e);
        Version++;
    }

    public void Add(int scene, long uuid, float x, float z)
    {
        if (_charId == 0 || uuid == 0 || !_uuids.Add(uuid)) return;
        var e = new Entry { Scene = scene, Uuid = uuid, X = x, Z = z };
        _all.Add(e);
        if (scene == _sceneId) _scene.Add(e);
        Version++;
        var sb = new StringBuilder(_all.Count * 32);
        foreach (var a in _all)
            sb.Append(a.Scene.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(a.Uuid.ToString(CultureInfo.InvariantCulture))
              .Append(' ').Append(a.X.ToString("0.##", CultureInfo.InvariantCulture)).Append(' ')
              .Append(a.Z.ToString("0.##", CultureInfo.InvariantCulture)).Append('\n');
        _io.WriteFileAsync(FileName(_charId), Encoding.UTF8.GetBytes(sb.ToString()));
    }

    public bool HasUuid(long uuid) => uuid != 0 && _uuids.Contains(uuid);

    // A server point of the current scene: opened if its uuid matches, or (uuid unknown) a chest within MatchRadius of
    // an opened chest. The scene list is tiny (chests opened here by this character), so the scan is cheap.
    public bool Matches(AtlasPoint p)
    {
        if (p.Uuid != 0) return _uuids.Contains(p.Uuid);
        if (!AtlasGroups.IsChest(p.Chip) || _scene.Count == 0) return false;
        foreach (var e in _scene)
        {
            float dx = e.X - p.X, dz = e.Z - p.Z;
            if (dx * dx + dz * dz <= MatchRadius * MatchRadius) return true;
        }
        return false;
    }
}
