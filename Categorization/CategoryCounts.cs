using Terraria;

namespace ManyMoreMobs
{
    /// <summary>
    /// Live per-category accounting of active NPCs. A single pass over <c>Main.npc[0..Main.maxNPCs)</c>
    /// (≤200 now, ≤750 after the Stage 2 cap raise) is cheap and is recomputed on demand so the spawn gate
    /// always sees up-to-the-instant counts — including NPCs spawned earlier in the same tick.
    /// </summary>
    public static class CategoryCounts
    {
        /// <summary>Count every active NPC, bucketed by category. Worm-boss segments count under Boss.</summary>
        public static (int town, int boss, int critter, int enemy) Snapshot()
        {
            int town = 0, boss = 0, critter = 0, enemy = 0;

            // Count only the live capacity (EngineState.NpcCap), not the raw array length. The array can stay
            // grown (751) after the cap raise is toggled OFF and the mod reloaded; any NPCs left in slots
            // 200+ from the previous session are stale and must NOT count toward the caps — otherwise they
            // saturate the Enemy count and the gate blocks all new spawns. Clamp to the array length for safety.
            int max = System.Math.Min(Main.npc.Length, EngineState.NpcCap);

            for (int i = 0; i < max; i++)
            {
                NPC npc = Main.npc[i];
                if (npc == null || !npc.active)
                    continue;

                switch (NpcCategorizer.Categorize(npc))
                {
                    case NpcCategory.Town: town++; break;
                    case NpcCategory.Boss: boss++; break;
                    case NpcCategory.Critter: critter++; break;
                    default: enemy++; break;
                }
            }

            return (town, boss, critter, enemy);
        }

        /// <summary>Live count for a single category.</summary>
        public static int Get(NpcCategory cat)
        {
            var s = Snapshot();
            return cat switch
            {
                NpcCategory.Town => s.town,
                NpcCategory.Boss => s.boss,
                NpcCategory.Critter => s.critter,
                _ => s.enemy,
            };
        }
    }
}
