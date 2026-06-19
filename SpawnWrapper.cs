using Terraria;
using Terraria.ModLoader;

namespace ManyMoreMobs
{
    /// <summary>
    /// Debug/manual spawn helpers. The previous <c>TownNPCSpawnGate : GlobalNPC</c> override of
    /// <c>CanSpawn</c> was removed: that hook does not exist on <see cref="GlobalNPC"/> in this tModLoader
    /// build (so it could not compile), and its logic was inverted — gating on the Town count there would
    /// have blocked natural <em>enemy</em> spawns once the town filled up. Per-category enforcement now
    /// lives in <see cref="NewNpcGate"/>, and live accounting in <see cref="CategoryCounts"/>.
    /// </summary>
    public static class SpawnWrapper
    {
        /// <summary>
        /// Manually spawns a town NPC at the player, respecting the Town cap. Used by the debug command;
        /// real town move-ins flow through vanilla housing logic and the <see cref="NewNpcGate"/> (which
        /// never blocks Town).
        /// </summary>
        public static bool TrySpawnTownNPC(Player player, int npcType)
        {
            var config = ModContent.GetInstance<ManyMoreMobsConfig>();
            int townCap = config?.GetEffectiveCaps().town ?? 0;

            if (CategoryCounts.Get(NpcCategory.Town) >= townCap)
            {
                Main.NewText("Town NPC spawn blocked (Town cap reached).");
                return false;
            }

            int index = NPC.NewNPC(
                player.GetSource_Misc("MMM_DebugTownSpawn"),
                (int)player.Center.X,
                (int)player.Center.Y,
                npcType
            );

            if (index < 0 || index >= EngineState.NpcCap || !Main.npc[index].active)
            {
                Main.NewText("Town NPC spawn failed (no free slot).");
                return false;
            }

            return true;
        }
    }
}
