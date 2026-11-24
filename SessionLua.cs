using Stellar.Abstractions.Services;

namespace Stellar.Atlas;

// The two game facts the creature panel needs from Lua (Auto-Gather-and-Interaction.md "Magical creatures for Atlas",
// research 2026-11-24, NOT yet validated in-game):
//   • current line   Z.DataMgr.Get("sceneline_data").PlayerLineId — the display number (1-based, per scene), nil until
//                    the bridge sets it. There is NO line-changed event → polled at 1 Hz (Plugin.Creatures.cs).
//   • server region  Z.DataMgr.Get("server_data").NowSelectData.{description, zoneId, id} + NowSelectServerId — read once
//                    per world entry and LOGGED, because the SEA / JP values are not in the dumps.
// Same compile-once scheme as GameLua (Plugin-Performance-Rules.md §4): the helpers are defined ONCE as _G functions and
// called with constant one-line chunks (no string building per poll); a sentinel tells when the globals were lost.
// Values are coerced with tonumber / tostring in Lua so the type-strict readback (Framework-Interop-Foundation.md §2)
// always sees a number / string.
internal sealed class SessionLua
{
    private const string Defs =
        "rawset(_G,'__atlas_line',function()" +
        " rawset(_G,'__at_line',nil) rawset(_G,'__at_cur',nil)" +
        " pcall(function() rawset(_G,'__at_cur', tonumber(Z.StageMgr.GetCurrentSceneId())) end)" +
        " pcall(function()" +
        "  local d = Z.DataMgr.Get(\"sceneline_data\")" +
        "  if d ~= nil then rawset(_G,'__at_line', tonumber(d.PlayerLineId)) end" +
        " end)" +
        " end)" +
        " rawset(_G,'__atlas_srv',function()" +
        " rawset(_G,'__at_sdesc',nil) rawset(_G,'__at_szone',nil) rawset(_G,'__at_sid',nil) rawset(_G,'__at_ssel',nil)" +
        " pcall(function()" +
        "  local d = Z.DataMgr.Get(\"server_data\")" +
        "  if d == nil then return end" +
        "  local s = d.NowSelectData" +
        "  if s ~= nil then" +
        "   rawset(_G,'__at_sdesc', tostring(s.description or ''))" +
        "   rawset(_G,'__at_szone', tonumber(s.zoneId))" +
        "   rawset(_G,'__at_sid', tostring(s.id))" +
        "  end" +
        "  rawset(_G,'__at_ssel', tostring(d.NowSelectServerId))" +
        " end)" +
        " end)";

    private const string LineChunk =
        "rawset(_G,'__atlas_has2',nil) if rawget(_G,'__atlas_line') then rawset(_G,'__atlas_has2',true) __atlas_line() end";
    private const string SrvChunk =
        "rawset(_G,'__atlas_has2',nil) if rawget(_G,'__atlas_srv') then rawset(_G,'__atlas_has2',true) __atlas_srv() end";

    private readonly IPluginServices _services;
    private bool _defined;

    public SessionLua(IPluginServices services) => _services = services;

    private ILua Lua => _services.Lua;

    public void OnLogout() => _defined = false;

    private bool Call(string chunk)
    {
        if (!Lua.Ready) return false;
        if (!_defined) { Lua.DoString(Defs); _defined = true; }
        Lua.DoString(chunk);
        if (Lua.TryReadGlobalBool("__atlas_has2", out var has) && has) return true;
        Lua.DoString(Defs);   // globals lost (Lua reset) → re-define once and retry
        Lua.DoString(chunk);
        return Lua.TryReadGlobalBool("__atlas_has2", out has) && has;
    }

    // The current line number, or 0 when unknown (not set yet / Lua not ready). `scene` = the scene the player is
    // actually in (Z.StageMgr.GetCurrentSceneId), 0 when unknown — NOT ITable.CurrentSceneId, which follows the scene the
    // big map is browsing (in-game 2026-11-24: browsing Asteria Plains from Asterleeds turned it to 7).
    public int ReadLine(out int scene)
    {
        scene = 0;
        if (!Call(LineChunk)) return 0;
        if (Lua.TryReadGlobalNumber("__at_cur", out double cur) && cur > 0 && cur < 100000) scene = (int)cur;
        return Lua.TryReadGlobalNumber("__at_line", out double v) && v >= 1 && v < 100000 ? (int)v : 0;
    }

    public readonly struct ServerInfo
    {
        public readonly string Description, Id, SelectedId;
        public readonly int ZoneId;
        public readonly bool Known;
        public ServerInfo(string description, int zoneId, string id, string selectedId, bool known)
        { Description = description; ZoneId = zoneId; Id = id; SelectedId = selectedId; Known = known; }
    }

    // Once per world entry (an event, so the readback strings are fine).
    public ServerInfo ReadServer()
    {
        if (!Call(SrvChunk)) return default;
        string desc = Lua.ReadGlobalString("__at_sdesc") ?? "";
        string id = Lua.ReadGlobalString("__at_sid") ?? "";
        string sel = Lua.ReadGlobalString("__at_ssel") ?? "";
        int zone = Lua.TryReadGlobalNumber("__at_szone", out double z) ? (int)z : 0;
        bool known = desc.Length > 0 || zone != 0 || (sel.Length > 0 && sel != "nil");
        return new ServerInfo(desc, zone, id, sel, known);
    }
}
