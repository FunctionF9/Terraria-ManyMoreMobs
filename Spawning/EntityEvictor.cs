using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;

namespace ManyMoreMobs
{
    /// <summary>
    /// Despawn-to-make-room. Frees a single slot for a guaranteed (Town / Boss / boss-segment) spawn by
    /// silently removing the lowest-priority active NPC.
    /// <para/>
    /// Removal priority: <b>Critters first, then Enemies</b> — never Town, Boss, or worm segments (those
    /// classify as Boss via <see cref="NpcCategorizer.Categorize"/>). Among equal priority the NPC FARTHEST
    /// from any player is removed first, so off-screen mobs vanish before on-screen ones; if only on-screen
    /// candidates exist one is still removed, because the high-priority spawn is guaranteed.
    /// <para/>
    /// This is room-making, not a kill: no loot, no death animation. (Single-player / server authority only —
    /// the gate never calls this on a multiplayer client; the server syncs the removal.)
    /// </summary>
    internal static class EntityEvictor
    {
        /// <summary>Despawns one low-priority NPC to free a slot; returns the freed slot index, or -1 if none.</summary>
        public static int TryFreeSlot()
        {
            int bestSlot = -1;
            int bestPriority = int.MaxValue; // 0 = critter (evict first), 1 = enemy
            float bestDistSq = -1f;

            int cap = EngineState.NpcCap;
            for (int i = 0; i < cap; i++)
            {
                NPC n = Main.npc[i];
                if (n == null || !n.active)
                    continue;

                NpcCategory cat = NpcCategorizer.Categorize(n);
                if (cat == NpcCategory.Town || cat == NpcCategory.Boss || NewNpcGate.IsBossChain(i))
                    continue; // never evict town/boss, nor boss-chain segments (chained-worm bodies, e.g. an
                              // Eater of Worlds body, classify as Enemy on their own but must be protected)

                if (NpcCategorizer.IsSpecialCritter(n))
                    continue; // rare critters (Prismatic Lacewing, Truffle Worm, gold critters) are one-off
                              // and often gate a boss summon — despawning one can cost a whole boss fight

                int priority = cat == NpcCategory.Critter ? 0 : 1;
                float distSq = DistanceSqToNearestPlayer(n);

                // Prefer lower priority (critter), then farther from any player.
                if (priority < bestPriority || (priority == bestPriority && distSq > bestDistSq))
                {
                    bestPriority = priority;
                    bestDistSq = distSq;
                    bestSlot = i;
                }
            }

            if (bestSlot < 0)
                return -1;

            NPC victim = Main.npc[bestSlot];
            victim.active = false;
            // Keep clients in sync if we're the server (MP is otherwise deferred; harmless in single-player).
            if (Main.netMode == NetmodeID.Server)
                NetMessage.SendData(MessageID.SyncNPC, -1, -1, null, bestSlot);
            return bestSlot;
        }

        private static float DistanceSqToNearestPlayer(NPC n)
        {
            float best = float.MaxValue;
            for (int p = 0; p < Main.player.Length; p++)
            {
                Player pl = Main.player[p];
                if (pl == null || !pl.active || pl.dead)
                    continue;
                float d = Vector2.DistanceSquared(pl.Center, n.Center);
                if (d < best)
                    best = d;
            }
            return best == float.MaxValue ? 0f : best;
        }
    }
}
