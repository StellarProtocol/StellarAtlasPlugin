namespace Stellar.Atlas;

// The five magical creatures of Asteria Plains (scene 7) — approved prototype _scratch\prototypes\atlas-creatures.tpl.html.
// Research (Auto-Gather-and-Interaction.md "Magical creatures for Atlas", release_4.7, NOT yet validated in-game): normal
// monsters (EntMonster = 1 via (uuid >> 6) & 31) whose config id is ZEntity.BaseId; the game draws NO map flag for them
// (MonsterHuntListTable SceneTag 0), so Atlas' own flags can't duplicate one. Lovely / Breezy Boarlet spawn at a fixed
// point on a fixed daily schedule; the Nappos and Loyal Boarlet at random, server-side.
//
// Index = the SHOW chip order (user-approved): Golden Nappo, Silver Nappo, Loyal Boarlet, Lovely Boarlet, Breezy Boarlet.
// Each chip is one bit of the per-character SHOW mask ABOVE the chest bits (MaskShift): chest / book chips use bits 0..15
// (AtlasGroups.Chip), so these never collide and an old saved mask has them all off (= the new-character default).
// Names stay ENGLISH in every locale (user decision): they are the names players use for them across servers.
internal static class Creatures
{
    public const int Count     = 5;
    public const int Scene     = 7;     // Asteria Plains
    public const int MaskShift = 16;
    public const int AllBits   = ((1 << Count) - 1) << MaskShift;

    public static readonly int[] Ids = { 10900, 10901, 10904, 10902, 10903 };

    public static readonly string[] Names = { "Golden Nappo", "Silver Nappo", "Loyal Boarlet", "Lovely Boarlet", "Breezy Boarlet" };

    public static int Bit(int idx) => 1 << (MaskShift + idx);

    public static int Index(int monsterId) => monsterId switch
    {
        10900 => 0, 10901 => 1, 10904 => 2, 10902 => 3, 10903 => 4,
        _ => -1,
    };

    // MonsterEntityGlobalTable 70019276 / 70019277 (Lovely / Breezy Boarlet): the one spot each spawns at. Used to guide to
    // and to draw an ABSENT boarlet (its next spawn time). The random spawners have none.
    public static bool FixedSpot(int idx, out float x, out float y, out float z)
    {
        switch (idx)
        {
            case 3: x = 107.16f; y = 146.48f; z = -329.85f; return true;   // Lovely Boarlet 10902
            case 4: x = 2.74f;   y = 154.93f; z = -291.72f; return true;   // Breezy Boarlet 10903
            default: x = y = z = 0f; return false;
        }
    }

    public static bool HasSchedule(int idx) => idx == 3 || idx == 4;
}
