using System.Collections.Generic;
using System.Globalization;
using Stellar.Abstractions.Services;

namespace Stellar.Atlas;

// Every game Lua call Atlas makes. Each helper is defined ONCE as a _G function and then called with a one-line chunk
// (Lua-Injection-from-CSharp.md "Polling Lua cheaply: compile once"; Plugin-Performance-Rules.md §4). A call chunk sets a
// `__atlas_has` sentinel; when it comes back nil the globals were lost (Lua reset) → re-define and retry once. Numbers
// are formatted invariant into the call — every call here is an EVENT (new config id, scene change, a click), never a
// per-frame poll. Results are parked in globals and read back type-strictly (Framework-Interop-Foundation.md §2).
//   • CollectionTable row (Name / Type) + LifeCollectListTable LifeProId, via Z.TableMgr.GetRow("<Mgr>", id) — the
//     single-call form the game uses (Auto-Gather-and-Interaction.md §2). Names come back in the client's language.
//   • Scene name: Z.TableMgr.GetTable("SceneTableMgr").GetRow(id).Name (chat_hyperlink_localposition.lua:59).
//   • Guide marker: the game's position-share guide (validated in-game 2026-11-23, KB §Chest Finder):
//     Z.VMMgr.GetVM("goal_guide").SetGuideGoals(E.GoalGuideSource.PositionShare, {GoalPosInfo.New(...)}) / (src, nil).
//     Enum args must be the userdata enums (Z.GoalPosType.Position), raw ints are rejected.
internal sealed class GameLua
{
    public readonly struct CfgInfo
    {
        public readonly string Name; public readonly int Type; public readonly int LifePro; public readonly bool Known;
        public CfgInfo(string name, int type, int lifePro, bool known) { Name = name; Type = type; LifePro = lifePro; Known = known; }
    }

    private const string Defs =
        "rawset(_G,'__atlas_cfg',function(id, life)" +
        " rawset(_G,'__at_ok',nil) rawset(_G,'__at_name',nil) rawset(_G,'__at_type',-1) rawset(_G,'__at_life',0)" +
        " pcall(function()" +
        "  local row = Z.TableMgr.GetRow(\"CollectionTableMgr\", id)" +
        "  if row == nil then return end" +
        "  rawset(_G,'__at_type', row.Type) rawset(_G,'__at_name', row.CollectionName) rawset(_G,'__at_ok', true)" +
        " end)" +
        " if life then pcall(function()" +
        "  local lr = Z.TableMgr.GetRow(\"LifeCollectListTableMgr\", id)" +
        "  if lr ~= nil then rawset(_G,'__at_life', lr.LifeProId) end" +
        " end) end" +
        " end)" +
        // Many ids in ONE call (prestage name resolution): a negative id also asks LifeCollectListTable. Result =
        // "id<TAB>ok<TAB>type<TAB>life<TAB>name" lines in __at_cfgs (tabs / newlines in names replaced by spaces).
        " rawset(_G,'__atlas_cfgs',function(...)" +
        " local out, args = {}, {...}" +
        " for i = 1, #args do local id = args[i] local life = id < 0 if life then id = -id end" +
        "  local ok, nm, ty, lp = 0, '', -1, 0" +
        "  pcall(function()" +
        "   local row = Z.TableMgr.GetRow(\"CollectionTableMgr\", id)" +
        "   if row ~= nil then ok = 1 ty = row.Type nm = row.CollectionName end" +
        "  end)" +
        "  if life then pcall(function()" +
        "   local lr = Z.TableMgr.GetRow(\"LifeCollectListTableMgr\", id)" +
        "   if lr ~= nil then lp = lr.LifeProId end" +
        "  end) end" +
        "  out[#out + 1] = id .. '\\t' .. ok .. '\\t' .. tostring(ty) .. '\\t' .. tostring(lp) .. '\\t' .. (string.gsub(tostring(nm or ''), '[\\t\\n]', ' '))" +
        " end" +
        " rawset(_G,'__at_cfgs', table.concat(out, '\\n'))" +
        " end)" +
        // Books this character has read = the game's own per-character record, keys of
        // Z.ContainerMgr.CharSerialize.handbookData.unlockNoteReadingBookMap (non-nil = read: HandbookVM.IsUnlock,
        // ui/view_model/handbook_vm.lua:76). The container keeps its entries in .__data__ (zcontainer/handbook_data.lua:79,
        // 339–346). Keyed by NoteReadingBookTable id. __at_rn = count (-1 = not readable yet); the ids are joined into
        // __at_rids ONLY when the count differs from `last`, so the slow re-check costs one number readback.
        " rawset(_G,'__atlas_readbooks',function(last)" +
        " rawset(_G,'__at_rn',-1) rawset(_G,'__at_rids',nil)" +
        " pcall(function()" +
        "  local m = Z.ContainerMgr.CharSerialize.handbookData.unlockNoteReadingBookMap" +
        "  local d = m.__data__ or m" +
        "  local n = 0 for _ in pairs(d) do n = n + 1 end" +
        "  if n ~= last then local out = {} for k in pairs(d) do out[#out + 1] = tostring(k) end rawset(_G,'__at_rids', table.concat(out, ',')) end" +
        "  rawset(_G,'__at_rn', n)" +
        " end)" +
        " end)" +
        // CollectionTable id of a book → NoteReadingBookTable id: row.AwardId[1] (an array: map_info_right_view.lua:337
        // passes it to GetAllAwardPreListByIds) → AwardTable row (awardpreview_vm.lua:87) GroupContent[1][1] = the item id
        // (life_profession_cast_main_view.lua:723), e.g. 55018 → award 33000190 → 1031019. "id:book" pairs in __at_bks
        // (book 0 = no chain).
        " rawset(_G,'__atlas_bookof',function(...)" +
        " local out, args = {}, {...}" +
        " for i = 1, #args do local id, bk = args[i], 0" +
        "  pcall(function()" +
        "   local row = Z.TableMgr.GetRow(\"CollectionTableMgr\", id)" +
        "   local aw = row and row.AwardId and row.AwardId[1]" +
        "   local ar = aw and Z.TableMgr.GetTable(\"AwardTableMgr\").GetRow(aw)" +
        "   local g = ar and ar.GroupContent and ar.GroupContent[1]" +
        "   if g and g[1] then bk = g[1] end" +
        "  end)" +
        "  out[#out + 1] = id .. ':' .. tostring(bk)" +
        " end" +
        " rawset(_G,'__at_bks', table.concat(out, ','))" +
        " end)" +
        " rawset(_G,'__atlas_scene',function(id)" +
        " rawset(_G,'__at_scene',nil)" +
        " pcall(function()" +
        "  local row = Z.TableMgr.GetTable(\"SceneTableMgr\").GetRow(id)" +
        "  if row ~= nil then rawset(_G,'__at_scene', row.Name) end" +
        " end)" +
        " end)" +
        // Guide marker. A goal in ANOTHER scene is guided the way the game guides a quest goal there
        // (quest_goal_guide_vm.getGuideGoalInfo): the shown goal is the current scene's teleport zone towards that scene
        // (Z.GoalGuideMgr:GetTpZoneUidBetweenScene(cur, to) → ZoneEntityTableMgr row Position, GoalPosType.Zone), the real
        // goal goes in as the sourceGoalList. No such zone → nothing is set (mode 0 = pending; the plugin re-sets it on
        // arrival). Result: __at_gmode 1 = direct, 2 = via teleport zone __at_gtp, 0 = pending, nil = failed.
        " rawset(_G,'__atlas_guide',function(sid, x, y, z)" +
        " rawset(_G,'__at_gmode',nil) rawset(_G,'__at_gtp',0)" +
        " pcall(function()" +
        "  local vm = Z.VMMgr.GetVM(\"goal_guide\")" +
        "  local src = E.GoalGuideSource.PositionShare" +
        "  if sid == nil then vm.SetGuideGoals(src, nil) rawset(_G,'__at_gmode',1) return end" +
        "  local info = Panda.ZGame.GoalPosInfo.New(src, sid, -1, Z.GoalPosType.Position, Vector3.New(x, y, z))" +
        "  local cur = Z.StageMgr.GetCurrentSceneId()" +
        "  if cur == sid then vm.SetGuideGoals(src, {info}) rawset(_G,'__at_gmode',1) return end" +
        "  local uid = Z.GoalGuideMgr:GetTpZoneUidBetweenScene(cur, sid)" +
        "  local row = uid > 0 and Z.TableMgr.GetTable(\"ZoneEntityTableMgr\").GetRow(uid) or nil" +
        "  if row == nil then rawset(_G,'__at_gmode',0) return end" +
        "  local p = row.Position" +
        "  local tp = Panda.ZGame.GoalPosInfo.New(src, cur, uid, Z.GoalPosType.Zone, Vector3.New(p[1], p[2], p[3]))" +
        "  vm.SetGuideGoals(src, {tp}, {info})" +
        "  rawset(_G,'__at_gtp',uid) rawset(_G,'__at_gmode',2)" +
        " end)" +
        " end)";

    private readonly IPluginServices _services;
    private readonly Dictionary<int, CfgInfo> _cfg = new();
    private readonly Dictionary<int, string> _scene = new();
    private bool _defined;

    public GameLua(IPluginServices services) => _services = services;

    private ILua Lua => _services.Lua;

    // Language switch → the game re-localizes its tables; drop cached NAMES (types stay valid but re-ask is cheap).
    public void OnLanguageChanged() { _cfg.Clear(); _scene.Clear(); }

    private bool Call(string chunk)
    {
        if (!Lua.Ready) return false;
        if (!_defined) { Lua.DoString(Defs); _defined = true; }
        Lua.DoString("rawset(_G,'__atlas_has',nil) if rawget(_G,'__atlas_cfg') then rawset(_G,'__atlas_has',true) " + chunk + " end");
        if (Lua.TryReadGlobalBool("__atlas_has", out var has) && has) return true;
        Lua.DoString(Defs);   // globals lost (Lua reset) → re-define once and retry
        Lua.DoString("rawset(_G,'__atlas_has',nil) if rawget(_G,'__atlas_cfg') then rawset(_G,'__atlas_has',true) " + chunk + " end");
        return Lua.TryReadGlobalBool("__atlas_has", out has) && has;
    }

    // Collection row for a config id; cached (misses included) so each id costs one Lua call per session/language.
    // The LifeCollectListTable row is asked only for non-chest ids (chests have none; a miss may log in the game).
    // Already resolved (no Lua call)? Lets the prestage build limit first-time lookups per tick.
    public bool HasCfg(int configId) => configId <= 0 || _cfg.ContainsKey(configId);

    public CfgInfo Cfg(int configId)
    {
        if (configId <= 0) return default;
        if (_cfg.TryGetValue(configId, out var info)) return info;
        bool withLife = !AtlasGroups.IsChestId(configId, -1) && !AtlasGroups.IsBookCfg(configId);   // books have no life row either
        if (!Call("__atlas_cfg(" + configId.ToString(CultureInfo.InvariantCulture) + (withLife ? ",true)" : ",false)")))
            return default;   // Lua not ready → not cached, retried on the next ask
        if (Lua.TryReadGlobalBool("__at_ok", out var ok) && ok)
        {
            Lua.TryReadGlobalNumber("__at_type", out var type);
            Lua.TryReadGlobalNumber("__at_life", out var life);
            // Some book titles carry a rich-text line break ("Thoughts of the Long-Lived:<br>Some Ramblings About Life",
            // CollectionTable 55001) — one line in a NEAREST row / map label. Once per id, cached.
            info = new CfgInfo((Lua.ReadGlobalString("__at_name") ?? "").Replace("<br>", " "), (int)type, (int)life, true);
        }
        else info = new CfgInfo("", -1, 0, true);   // no row: Known=true so it is not re-asked
        _cfg[configId] = info;
        return info;
    }

    // Resolves ids[start .. start+count) in ONE Lua call and caches them (misses included, like Cfg). Returns how many
    // came back, or -1 when Lua isn't ready (nothing cached → asked again later). Event-driven (a prestage build tick),
    // so the chunk / readback allocation is fine.
    public int CfgBatch(IReadOnlyList<int> ids, int start, int count)
    {
        if (count <= 0) return 0;
        var sb = new System.Text.StringBuilder("rawset(_G,'__at_cfgs',nil) __atlas_cfgs(");
        for (int i = start; i < start + count; i++)
        {
            int id = ids[i];
            if (i > start) sb.Append(',');
            sb.Append((AtlasGroups.IsChestId(id, -1) ? id : -id).ToString(CultureInfo.InvariantCulture));
        }
        if (!Call(sb.Append(')').ToString())) return -1;
        var text = Lua.ReadGlobalString("__at_cfgs");
        if (text == null) return -1;
        int got = 0;
        foreach (var line in text.Split('\n'))
        {
            var f = line.Split('\t', 5);
            if (f.Length < 5 || !int.TryParse(f[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int id)) continue;
            int.TryParse(f[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int type);
            int.TryParse(f[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int life);
            _cfg[id] = f[1] == "1" ? new CfgInfo(f[4], type, life, true) : new CfgInfo("", -1, 0, true);
            got++;
        }
        return got;
    }

    // Read-book record: returns the count (-1 = Lua / CharSerialize not ready). `ids` is the comma-joined id list, set
    // only when the count differs from `lastCount` (pass -1 to force it). Event / slow cadence only (Plugin.ReadBooks.cs).
    public int ReadBooks(int lastCount, out string? ids)
    {
        ids = null;
        if (!Call("__atlas_readbooks(" + lastCount.ToString(CultureInfo.InvariantCulture) + ")")
            || !Lua.TryReadGlobalNumber("__at_rn", out double n) || n < 0) return -1;
        if ((int)n != lastCount) ids = Lua.ReadGlobalString("__at_rids") ?? "";
        return (int)n;
    }

    // CollectionTable ids → NoteReadingBookTable ids in ONE call, written into `into` (0 = no chain; misses cached too).
    // False when Lua isn't ready (nothing written → asked again later).
    public bool BookIds(List<int> cfgs, Dictionary<int, int> into)
    {
        if (cfgs.Count == 0) return true;
        var sb = new System.Text.StringBuilder("rawset(_G,'__at_bks',nil) __atlas_bookof(");
        for (int i = 0; i < cfgs.Count; i++) { if (i > 0) sb.Append(','); sb.Append(cfgs[i].ToString(CultureInfo.InvariantCulture)); }
        if (!Call(sb.Append(')').ToString())) return false;
        var text = Lua.ReadGlobalString("__at_bks");
        if (text == null) return false;
        foreach (var pair in text.Split(','))
        {
            int c = pair.IndexOf(':');
            if (c <= 0 || !int.TryParse(pair.Substring(0, c), NumberStyles.Integer, CultureInfo.InvariantCulture, out int id)) continue;
            double.TryParse(pair.Substring(c + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out double bk);
            into[id] = (int)bk;
        }
        foreach (int id in cfgs) into.TryAdd(id, 0);   // a garbled reply must not make it re-ask forever
        return true;
    }

    public string SceneName(int sceneId)
    {
        if (sceneId <= 0) return "";
        if (_scene.TryGetValue(sceneId, out var name)) return name;
        if (!Call("__atlas_scene(" + sceneId.ToString(CultureInfo.InvariantCulture) + ")")) return "";
        name = Lua.ReadGlobalString("__at_scene") ?? "";
        _scene[sceneId] = name;
        return name;
    }

    // Sets (or with sceneId <= 0 clears) the game's guide marker. Returns 1 = set directly, 2 = set via the current
    // scene's teleport zone towards `sceneId` (tpZone = its uid), 0 = another scene with no teleport zone (nothing set:
    // pending), -1 = failed (Lua not ready / error).
    public int SetGuide(int sceneId, float x, float y, float z, out int tpZone)
    {
        tpZone = 0;
        string chunk = sceneId <= 0
            ? "__atlas_guide(nil)"
            : "__atlas_guide(" + sceneId.ToString(CultureInfo.InvariantCulture) + "," + F(x) + "," + F(y) + "," + F(z) + ")";
        if (!Call(chunk) || !Lua.TryReadGlobalNumber("__at_gmode", out double mode)) return -1;
        if (Lua.TryReadGlobalNumber("__at_gtp", out double tp)) tpZone = (int)tp;
        return (int)mode;
    }

    public void ClearGuide() => SetGuide(0, 0f, 0f, 0f, out _);

    public void OnLogout() => _defined = false;   // a new Lua state may come with the next login

    private static string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}
