using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;

namespace ManyMoreMobs
{
    /// <summary>
    /// Despawn-to-make-room. Frees a single slot for a guaranteed (Town / Boss / boss-segment) spawn by
    /// silently removing the lowest-priority active NPC.
    /// <para/>
    /// Removal priority, in order: <b>lone critters, lone enemies, then segments of multi-part bodies</b> as a
    /// last resort. Town and Boss are never removed — and since 0.7.6.6 a worm-boss body counts as Boss whole,
    /// so an Eater of Worlds is protected segment by segment rather than just at the head. Among equal priority
    /// the NPC FARTHEST from any player goes first, so off-screen mobs vanish before on-screen ones; if only
    /// on-screen candidates exist one is still removed, because the high-priority spawn is guaranteed.
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
            int bestPriority = int.MaxValue; // see the tiers below; lower is evicted first
            float bestDistSq = -1f;

            int cap = EngineState.NpcCap;
            for (int i = 0; i < cap; i++)
            {
                NPC n = Main.npc[i];
                if (n == null || !n.active)
                    continue;

                NpcCategory cat = NpcCategorizer.Categorize(n);
                if (cat == NpcCategory.Town || cat == NpcCategory.Boss)
                    continue; // never evict town or boss (worm-boss segments now categorize as Boss too)

                if (NpcCategorizer.IsSpecialCritter(n))
                    continue; // rare critters (Prismatic Lacewing, Truffle Worm, gold critters) are one-off
                              // and often gate a boss summon — despawning one can cost a whole boss fight

                // Tiers: lone critters go first, then lone enemies, and only if the world holds nothing else
                // do we touch a multi-part body. Segments are last because removing one link makes the worm's
                // AI despawn everything beyond it, so freeing one slot can silently cost twenty — and because
                // this runs FROM the guaranteed-spawn path, where a worm mid-assembly could otherwise be told
                // to eat its own tail to fit its next segment.
                //
                // They are not exempt outright, though. A world saturated with worms would leave nothing at
                // all evictable, and the guarantee that a Town NPC or a boss can always find room predates
                // this and matters more. In that corner the worm loses; its AI tidies up the rest of itself.
                int priority = (cat == NpcCategory.Critter ? 0 : 1) + (SegmentChain.IsMember(i) ? 2 : 0);
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
