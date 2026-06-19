using Terraria;
using Terraria.ID;
using Terraria.DataStructures;

namespace ManyMoreMobs
{
    /// <summary>
    /// Single source of truth for sorting an NPC into a <see cref="NpcCategory"/>.
    /// <para/>
    /// Classification uses the engine flags verified against this tModLoader build:
    /// <list type="bullet">
    /// <item><c>npc.isLikeATownNPC</c> (covers <see cref="NPCID.Sets.ActsLikeTownNPC"/> + town pets/merchants) → Town</item>
    /// <item><c>npc.boss || NPCID.Sets.ShouldBeCountedAsBoss[type]</c> → Boss</item>
    /// <item><c>npc.CountsAsACritter</c> → Critter</item>
    /// <item>everything else → Enemy</item>
    /// </list>
    /// Worm-boss segments (The Destroyer etc.) point <see cref="NPC.realLife"/> at the head segment, so
    /// they inherit the head's category and a full worm is counted entirely as Boss instead of leaking
    /// into the Enemy budget.
    /// </summary>
    public static class NpcCategorizer
    {
        /// <summary>Categorize a live NPC instance (used when counting active NPCs).</summary>
        public static NpcCategory Categorize(NPC npc)
        {
            // Multi-segment bodies share their health with a "real life" segment (the head). Inherit from
            // it so every segment lands in the head's bucket. Single level of indirection only.
            int head = npc.realLife;
            if (head >= 0 && head != npc.whoAmI && head < Main.npc.Length)
            {
                NPC headNpc = Main.npc[head];
                if (headNpc != null && headNpc.active && headNpc.whoAmI != npc.whoAmI)
                    return ClassifyByFlags(headNpc);
            }

            return ClassifyByFlags(npc);
        }

        /// <summary>
        /// Classify directly from an NPC's flags (no worm-segment indirection). Public so the type-table
        /// builder can reuse the exact same logic on a freshly <c>SetDefaults</c>-ed scratch NPC.
        /// </summary>
        public static NpcCategory ClassifyByFlags(NPC npc)
        {
            if (npc.isLikeATownNPC)
                return NpcCategory.Town;

            if (npc.boss || ((uint)npc.type < (uint)NPCID.Sets.ShouldBeCountedAsBoss.Length && NPCID.Sets.ShouldBeCountedAsBoss[npc.type]))
                return NpcCategory.Boss;

            if (npc.CountsAsACritter)
                return NpcCategory.Critter;

            return NpcCategory.Enemy;
        }

        /// <summary>Category from an NPC type id alone (no instance), via the prebuilt type table.</summary>
        public static NpcCategory CategorizeType(int type) => NpcTypeCategories.Get(type);

        /// <summary>
        /// Structural Old One's Army objects — the Eternia Crystal and the Lane Portals. These must live in the
        /// low 0-199 zone: vanilla draws them, the "interact to skip wait" right-click finds them, the event's
        /// "is the crystal still alive?" stop-check and its loot/cleanup scans all assume 0-199. The spawn gate
        /// always allows them and hints the low zone for placement.
        /// </summary>
        public static bool IsStructuralLowZone(int type)
            => type == NPCID.DD2EterniaCrystal || type == NPCID.DD2LanePortal;

        /// <summary>
        /// Best-effort category for an NPC that is about to be created, for use by the spawn gate (which
        /// only has the type + spawn source, not a live instance yet).
        /// <para/>
        /// <paramref name="bossParented"/> is true only when the spawn is a piece of an existing boss — a worm
        /// segment or a boss-summoned add (detected generically via the <see cref="EntitySource_Parent"/>
        /// chain, so it also covers modded bosses). Those must never be blocked, or a worm boss would spawn
        /// incomplete. The base Town/Boss spawns themselves ARE subject to their caps (so the configured caps
        /// are authoritative); the caps sum to the total, so hard reservation still guarantees their space.
        /// </summary>
        public static NpcCategory CategorizeSpawn(int type, IEntitySource source, out bool bossParented)
        {
            if (source is EntitySource_Parent parent && parent.Entity is NPC parentNpc && Categorize(parentNpc) == NpcCategory.Boss)
            {
                bossParented = true;
                return NpcCategory.Boss;
            }

            bossParented = false;
            return CategorizeType(type);
        }
    }
}
