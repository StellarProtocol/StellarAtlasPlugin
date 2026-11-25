using Stellar.Abstractions.Domain;

namespace Stellar.Atlas;

// The SHOW chips, the server's group/subtype names (StellarAtlasServer README "Point model") and how a live game entity is
// classified into them. One chip per filterable bucket; a point's chip is derived once when it is parsed/seen, so the
// per-refresh filter is a single bit test.
//
// SHOW redesign (user-approved option A, 2026-11-23): two sub-rows, CHESTS (Common / Epic / Legendary / Other) and
// GATHERING & COLLECTIBLES (Herbs / Ore / Fishing / Books). NPCs, monsters, teleports, activities, traversal and misc
// server groups are DROPPED (the game's own map already marks them): they map to Chip.Hidden, which no chip can turn
// on, so they are never listed, guided to or drawn on the game map. Reporting is unaffected (live classification never
// yields Hidden).
//
// Enum values = bit positions in the persisted per-character mask, so they must never be renumbered: Common..Fishing keep
// their old bits 0-6; Books takes the old "Reading" bit 9 (a player who had Books/reading on keeps it).
internal enum Chip : byte
{
    Common = 0, Exquisite = 1, Luxurious = 2, OtherChests = 3, Herbs = 4, Ore = 5, Fishing = 6,
    Books = 9,
    Hidden = 15,
}

internal static class AtlasGroups
{
    public const int ChipCount = 16;   // array size for per-chip caches (indexed by the enum value)
    // A new character starts with EVERY chip off (user-approved 2026-11-23): nothing is listed or put on the game's maps
    // until the player turns a type on. A character with a saved filter keeps it (MigrateMask).
    public const int DefaultMask = 0;
    public const int ShownBits = KeptBits;
    // SHOW option B (user-approved 2026-11-23): only the four chest chips + Books can be turned on. Herbs / Ore / Fishing
    // keep their enum values (live classification + REPORTING are unchanged), but no chip shows them, so they never
    // reach NEAREST, the guide, the big map, the minimap or the prestaged sets.
    private const int KeptBits = (1 << (int)Chip.Common) | (1 << (int)Chip.Exquisite) | (1 << (int)Chip.Luxurious)
                                 | (1 << (int)Chip.OtherChests) | (1 << (int)Chip.Books);

    // A saved mask: keep the bits of chips that still exist; drop the old More… groups, the More…-expanded flag (bit 31)
    // and the removed Herbs / Ore / Fishing chips.
    // The magical creature chips (bits 16..20, Creatures.cs) are kept too; a mask saved before them has them off.
    public static int MigrateMask(int saved) => saved & (KeptBits | Creatures.AllBits);

    // Server group names (model.KnownGroups).
    public const string GChest = "chest", GOre = "ore", GHerb = "herb", GFishing = "fishing";

    public static readonly Chip[] ChestChips   = { Chip.Common, Chip.Exquisite, Chip.Luxurious, Chip.OtherChests };
    public static readonly Chip[] CollectChips = { Chip.Books };

    public static bool IsChest(Chip c) => c <= Chip.OtherChests;

    // One-time per character: chests AND books. Books (CollectionTable "reading" rows 55000–55409) share the chest state
    // machine (StatusTransition [[0,1]], IsRefresh false, state 1 = EObjStateDead): reading one flips AttrObjState 0→1 and
    // the server then stops streaming it to that character. Every hide / opened / not-here / live-wins path uses this,
    // never IsChest (which stays for chest counts, names and ordering).
    public static bool IsOneTime(Chip c) => IsChest(c) || c == Chip.Books;

    // Fixed semantic colours (SDL §3 "Chips"). Chest rarities: silver / copper (game "Epic") / gold (game "Legendary") /
    // coral — the same colours as the map marker tiles (tools/make-map-icons.py) and chip dots (tools/icons/gen-dot-icons.py).
    public static ColorRgba Color(Chip c) => c switch
    {
        Chip.Common      => Rgb(0x98a2a7),
        Chip.Exquisite   => Rgb(0xc8763a),
        Chip.Luxurious   => Rgb(0xf2bf1a),
        Chip.OtherChests => Rgb(0xd9604a),
        Chip.Herbs       => Rgb(0x8ce68c),
        Chip.Ore         => Rgb(0xb385ff),
        Chip.Fishing     => Rgb(0x66d9ff),
        Chip.Books       => Rgb(0xe0c98a),
        _                => Rgb(0xb0b0b0),
    };

    // Embedded art name stem (Resources/map/map-<stem>-<px>.png, Resources/dots/dot-<stem>-<px>.png).
    public static string MapIconStem(Chip c) => c.ToString().ToLowerInvariant();

    public static string LabelKey(Chip c) => c switch
    {
        Chip.Common      => "at.chip.common",
        Chip.Exquisite   => "at.chip.exquisite",
        Chip.Luxurious   => "at.chip.luxurious",
        Chip.OtherChests => "at.chip.otherChests",
        _                => "at.chip.books",   // Herbs / Ore / Fishing / Hidden are never displayed
    };

    // Server row (group, subtype) → chip. Subtypes per the README mapping table. "Collectibles" = reading material,
    // engrams and resonance points → Books. Every other group → Hidden (never shown).
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
        "reading" or "engram" or "resonance" => Chip.Books,
        _           => Chip.Hidden,
    };

    // Only these groups carry a CollectionTable id in game_config_id (questlog "collectable"), so only they may resolve
    // a localized name through CollectionTableMgr — a book/engram id would look up an unrelated collection row.
    public static bool HasCollectionConfig(Chip c) => c <= Chip.Ore;

    // ── live entity classification (Auto-Gather-and-Interaction.md §2 + §Chest Finder) ────────────────────────────
    // Chests: CollectionTable.Type == 2, plus the Type-0 chest ids (never Type==2 alone). The three Wild subtypes are
    // keyed by config id, NOT by name: the CollectionTable name is in the client's language.
    // The chest config ids that exist in CollectionTable (release_4.7 tables/CollectionTable.json: the IsChestId ranges
    // + the Type-2 rows 52001–52018), for the loading-screen name warm-up (Plugin.Tick.cs TryWarmUp). A missing id is
    // harmless (cached as "no row").
    public static readonly int[] WarmChestIds =
    {
        40001, 52001, 52002, 52003, 52004, 52005, 52006, 52007, 52008, 52011, 52012, 52013, 52014, 52016, 52017, 52018,
        120101, 120102, 120201, 120202, 120203, 130001, 130002, 130003, 130004, 130005, 130006, 130007,
    };

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
        group = subtype = ""; chip = Chip.Hidden;
        return false;
    }

    private static ColorRgba Rgb(uint rgb) =>
        new(((rgb >> 16) & 0xff) / 255f, ((rgb >> 8) & 0xff) / 255f, (rgb & 0xff) / 255f, 1f);
}
