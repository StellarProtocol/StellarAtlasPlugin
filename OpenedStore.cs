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
//
// "Not here for this character" (2026-11-23): server CHEST points this character stood near while the game showed no
// chest there (Plugin.Absent.cs) — almost always chests opened before Atlas was installed. Lines "A scene pointId x z";
// matched by server point id. Local only, never reported to the server.
internal sealed class OpenedStore
{
    private const float MatchRadius = 3f;

    private struct Entry { public int Scene; public long Uuid; public float X, Z; }

    private readonly AtlasClient _io;
    private readonly List<Entry> _all = new();
    private readonly HashSet<long> _uuids = new();
    private readonly List<Entry> _absent = new();      // Uuid field holds the SERVER POINT id
    private readonly HashSet<long> _absentIds = new();
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
        _all.Clear(); _uuids.Clear(); _absent.Clear(); _absentIds.Clear();
        if (charId != 0 && _io.ReadFile(FileName(charId)) is { } bytes)
        {
            foreach (var line in Encoding.UTF8.GetString(bytes).Split('\n'))
            {
                var p = line.Trim().Split(' ');
                bool absent = p.Length >= 5 && p[0] == "A";
                if (absent) p = p[1..];
                if (p.Length < 4) continue;
                if (!int.TryParse(p[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int s)
                    || !long.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long u)
                    || !float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
                    || !float.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float z)) continue;
                if (absent) { _absent.Add(new Entry { Scene = s, Uuid = u, X = x, Z = z }); _absentIds.Add(u); continue; }
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
        Save();
    }

    public void AddAbsent(int scene, long pointId, float x, float z)
    {
        if (_charId == 0 || pointId == 0 || !_absentIds.Add(pointId)) return;
        _absent.Add(new Entry { Scene = scene, Uuid = pointId, X = x, Z = z });
        Version++;
        Save();
    }

    // "Live wins" (user-approved 2026-11-23): an opened chest seen live again is un-hidden.
    public bool RemoveOpened(long uuid)
    {
        if (!_uuids.Remove(uuid)) return false;
        _all.RemoveAll(e => e.Uuid == uuid);
        _scene.RemoveAll(e => e.Uuid == uuid);
        Version++;
        Save();
        return true;
    }

    // Not-here entries of the current scene within `radius` (x, z) of a live chest → removed; their point ids go to `removed`.
    public void RemoveAbsentNear(float x, float z, float radius, List<long> removed)
    {
        int before = removed.Count;
        for (int i = _absent.Count - 1; i >= 0; i--)
        {
            var e = _absent[i];
            if (e.Scene != _sceneId) continue;
            float dx = e.X - x, dz = e.Z - z;
            if (dx * dx + dz * dz > radius * radius) continue;
            _absent.RemoveAt(i);
            _absentIds.Remove(e.Uuid);
            removed.Add(e.Uuid);
        }
        if (removed.Count == before) return;
        Version++;
        Save();
    }

    public bool HasAbsentInScene()
    {
        foreach (var e in _absent) if (e.Scene == _sceneId) return true;
        return false;
    }

    private void Save()
    {
        var sb = new StringBuilder((_all.Count + _absent.Count) * 34);
        foreach (var a in _all) Line(sb, "", a);
        foreach (var a in _absent) Line(sb, "A ", a);
        _io.WriteFileAsync(FileName(_charId), Encoding.UTF8.GetBytes(sb.ToString()));
    }

    private static void Line(StringBuilder sb, string prefix, Entry a) =>
        sb.Append(prefix).Append(a.Scene.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(a.Uuid.ToString(CultureInfo.InvariantCulture))
          .Append(' ').Append(a.X.ToString("0.##", CultureInfo.InvariantCulture)).Append(' ')
          .Append(a.Z.ToString("0.##", CultureInfo.InvariantCulture)).Append('\n');

    public bool HasUuid(long uuid) => uuid != 0 && _uuids.Contains(uuid);

    // A server point of the current scene: opened if its uuid matches, or (uuid unknown) a chest within MatchRadius of
    // an opened chest. The scene list is tiny (chests opened here by this character), so the scan is cheap.
    public bool Matches(AtlasPoint p)
    {
        if (_absentIds.Contains(p.Id)) return true;
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
