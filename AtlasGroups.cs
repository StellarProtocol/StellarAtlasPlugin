using Stellar.Abstractions.Domain;

namespace Stellar.Atlas;

// The SHOW chips, the server's group/subtype names (StellarAtlasServer README "Point model") and how a live game entity is
// classified into them. One chip per filterable bucket; a point's chip is derived once when it is parsed/seen, so the
// per-refresh filter is a single bit test.
internal enum Chip : byte
{
    Common, Exquisite, Luxurious, OtherChests, Herbs, Ore, Fishing,
    // "More…" groups (hidden behind the More… chip, all OFF by default)
    Npc, Monster, Reading, Traversal, Teleport, Activity, Engram, Resonance, Other,
}

internal static class AtlasGroups
{
    public const int ChipCount = 16;
    // Bit 31 of the persisted mask = the More… chip is expanded (not a filter).
    public const int MoreOpenBit = 1 << 31;
    // Approved prototype: the four chest chips ON, everything else OFF.
    public const int DefaultMask = (1 << (int)Chip.Common) | (1 << (int)Chip.Exquisite) | (1 << (int)Chip.Luxurious)
                                   | (1 << (int)Chip.OtherChests);

    // Server group names (model.KnownGroups).
    public const string GChest = "chest", GOre = "ore", GHerb = "herb", GFishing = "fishing";

    public static readonly Chip[] MainChips = { Chip.Common, Chip.Exquisite, Chip.Luxurious, Chip.OtherChests, Chip.Herbs, Chip.Ore, Chip.Fishing };
    public static readonly Chip[] MoreChips = { Chip.Npc, Chip.Monster, Chip.Reading, Chip.Traversal, Chip.Teleport, Chip.Activity, Chip.Engram, Chip.Resonance, Chip.Other };

    public static bool IsChest(Chip c) => c <= Chip.OtherChests;

    // Prototype dot colours (fixed semantic colours, SDL §3 "Chips"). The More… groups are new (not in the prototype).
    public static ColorRgba Color(Chip c) => c switch
    {
        Chip.Common      => Rgb(0xc9c9c9),
        Chip.Exquisite   => Rgb(0x9cc8ff),
        Chip.Luxurious   => Rgb(0xffcc73),
        Chip.OtherChests => Rgb(0xff9580),
        Chip.Herbs       => Rgb(0x8ce68c),
        Chip.Ore         => Rgb(0xb385ff),
        Chip.Fishing     => Rgb(0x66d9ff),
        Chip.Npc         => Rgb(0xf0a8d0),
        Chip.Monster     => Rgb(0xff6b6b),
        Chip.Reading     => Rgb(0xd9c48c),
        Chip.Traversal   => Rgb(0x7fe0c8),
        Chip.Teleport    => Rgb(0xa0a8ff),
        Chip.Activity    => Rgb(0xffb366),
        Chip.Engram      => Rgb(0xe6e66b),
        Chip.Resonance   => Rgb(0xc79bff),
        _                => Rgb(0xb0b0b0),
    };

    public static readonly ColorRgba MoreDot = Rgb(0x9b8aa5);   // the More… chip's own dot (prototype)

    public static string LabelKey(Chip c) => c switch
    {
        Chip.Common      => "at.chip.common",
        Chip.Exquisite   => "at.chip.exquisite",
        Chip.Luxurious   => "at.chip.luxurious",
        Chip.OtherChests => "at.chip.otherChests",
        Chip.Herbs       => "at.chip.herbs",
        Chip.Ore         => "at.chip.ore",
        Chip.Fishing     => "at.chip.fishing",
        Chip.Npc         => "at.chip.npc",
        Chip.Monster     => "at.chip.monster",
        Chip.Reading     => "at.chip.reading",
        Chip.Traversal   => "at.chip.traversal",
        Chip.Teleport    => "at.chip.teleport",
        Chip.Activity    => "at.chip.activity",
        Chip.Engram      => "at.chip.engram",
        Chip.Resonance   => "at.chip.resonance",
        _                => "at.chip.other",
    };

    // Server row (group, subtype) → chip. Subtypes per the README mapping table; anything unknown → Other.
    public static Chip FromServer(string group, string subtype) => group switch
    {
        GChest => subtype switch
        {
            "common"    => Chip.Common,
            "exquisite" => Chip.Exquisite,
            "luxurious" => Chip.Luxurious,
            _           => Chip.OtherChests,
        },
        GHerb       => Chip.Herbs,
        GOre        => Chip.Ore,
        GFishing    => Chip.Fishing,
        "npc"       => Chip.Npc,
        "monster"   => Chip.Monster,
        "reading"   => Chip.Reading,
        "traversal" => Chip.Traversal,
        "teleport"  => Chip.Teleport,
        "activity"  => Chip.Activity,
        "engram"    => Chip.Engram,
        "resonance" => Chip.Resonance,
        _           => Chip.Other,
    };

    // Only these groups carry a CollectionTable id in game_config_id (questlog "collectable"), so only they may resolve
    // a localized name through CollectionTableMgr — an npc/monster id would look up an unrelated collection row.
    public static bool HasCollectionConfig(Chip c) => c <= Chip.Ore;

    // ── live entity classification (Auto-Gather-and-Interaction.md §2 + §Chest Finder) ────────────────────────────
    // Chests: CollectionTable.Type == 2, plus the Type-0 chest ids (never Type==2 alone). The three Wild subtypes are
    // keyed by config id, NOT by name: the CollectionTable name is in the client's language.
    public static bool IsChestId(int cfg, int type) =>
        type == 2 || cfg == 52004 || cfg == 52005 || (cfg >= 52011 && cfg <= 52014) || (cfg >= 120101 && cfg <= 120203)
        || (cfg >= 130001 && cfg <= 130007) || cfg == 40001;

    public static string ChestSubtype(int cfg) => cfg switch
    {
        52001 or 52006 or 52016 => "common",
        52002 or 52007 or 52017 => "exquisite",
        52003 or 52008 or 52018 or >= 52011 and <= 52014 => "luxurious",
        _ => "other",
    };

    // Fish gathered as collection nodes (CollectionTable "Tris Sea Fish" / "Pale Blue Fish", LifeProId 101). Not the
    // questlog fishing *spots*, so they report subtype "fish" and never merge into a questlog spot.
    public static bool IsFishNode(int cfg) => cfg == 20000013 || cfg == 20000014;

    // A live collection → (group, subtype, chip), or false when Atlas doesn't track it. `lifeProId` = LifeCollectListTable
    // row (0 = none): 101 botany, 102 mining (common ore), 103 gem/stone ore.
    public static bool ClassifyLive(int cfg, int type, int lifeProId, out string group, out string subtype, out Chip chip)
    {
        if (IsChestId(cfg, type))
        {
            group = GChest; subtype = ChestSubtype(cfg); chip = FromServer(group, subtype);
            return true;
        }
        if (IsFishNode(cfg)) { group = GFishing; subtype = "fish"; chip = Chip.Fishing; return true; }
        if (lifeProId == 101) { group = GHerb; subtype = "botany"; chip = Chip.Herbs; return true; }
        if (lifeProId == 102) { group = GOre; subtype = "mineral"; chip = Chip.Ore; return true; }
        if (lifeProId == 103) { group = GOre; subtype = "gem"; chip = Chip.Ore; return true; }
        if (type == 1) { group = GOre; subtype = "mineral"; chip = Chip.Ore; return true; }   // Stokes Mine: Type 1, no life row
        group = subtype = ""; chip = Chip.Other;
        return false;
    }

    private static ColorRgba Rgb(uint rgb) =>
        new(((rgb >> 16) & 0xff) / 255f, ((rgb >> 8) & 0xff) / 255f, (rgb & 0xff) / 255f, 1f);
}
