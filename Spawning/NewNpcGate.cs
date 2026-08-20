using System;
using System.Collections.Generic;
using System.Reflection;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using Terraria.ModLoader;

namespace ManyMoreMobs
{
    /// <summary>
    /// Universal hard-cap enforcement for the Enemy and Critter categories.
    /// <para/>
    /// Every NPC in the game is created through <see cref="NPC.NewNPC"/>, so detouring it is the one
    /// chokepoint that catches all spawn paths (natural spawns, statues, summons, events) — unlike the
    /// natural-spawn pool, where vanilla enemies and critters both ride under aggregate <c>pool[0]</c> and
    /// can't be split. When the spawning NPC's category is already at its effective cap we return
    /// <c>Main.maxNPCs</c>, exactly mimicking vanilla's "no free slot" result so every caller handles it.
    /// <para/>
    /// Town and Boss spawns (including boss-parented worm segments/minions) are never blocked — hard
    /// reservation guarantees their space because the other categories' caps are clamped so the four caps
    /// never exceed the total (see <see cref="ManyMoreMobsConfig.GetEffectiveCaps"/>).
    /// <para/>
    /// The detour is registered through <see cref="MonoModHooks"/>, which auto-undoes it on mod unload.
    /// </summary>
    public class NewNpcGate : ModSystem
    {
        private delegate int OrigNewNPC(IEntitySource source, int x, int y, int type, int start,
            float ai0, float ai1, float ai2, float ai3, int target);

        private delegate int HookNewNPC(OrigNewNPC orig, IEntitySource source, int x, int y, int type, int start,
            float ai0, float ai1, float ai2, float ai3, int target);

        // Hardcoded per-type active caps. Frost Moon wave 20 removes vanilla's cap on these bosses, so they
        // churn under the raised cap; limit them (a bit higher than Pumpkin Moon's 2/2/3 since Frost is harder).
        //
        // ROOT CAUSE, found 0.7.6.4 and NOT yet fixed — this dictionary is a symptom patch. NPC.SpawnNPC opens
        // with a 0-199 loop summing `npcSlots` for exactly this family (Headless Horseman, Mourning Wood,
        // Pumpking + blades, Everscream, Ice Queen, Santa-NK1) into a budget that gates further spawns of them.
        // Every one of them is Enemy-category, so under the raised cap they sit above 199, the budget stays
        // near zero and vanilla's own density gate never trips. Widening that loop would restore the real
        // limiter and make these hardcoded numbers redundant — but it also changes Moon event pacing, so it is
        // a balance decision, not a hotfix. The audit reported that loop as PATCHED until 0.7.6.4, which is
        // why it was never triaged: NPC.SpawnNPC is hooked twice for unrelated reasons.
        private static readonly Dictionary<int, int> PerTypeActiveCaps = new()
        {
            [NPCID.Everscream] = 10,
            [NPCID.IceQueen] = 5,
            [NPCID.SantaNK1] = 8,
        };

        public override void OnModLoad()
        {
            MethodInfo target = typeof(NPC).GetMethod(nameof(NPC.NewNPC), BindingFlags.Public | BindingFlags.Static);
            MonoModHooks.Add(target, new HookNewNPC(NewNPC_Hook));
        }

        // Chain tags describe one world's slots, so they must not survive a world change — see SegmentChain.Reset.
        public override void OnWorldLoad() => SegmentChain.Reset();

        public override void OnWorldUnload() => SegmentChain.Reset();

        private static int NewNPC_Hook(OrigNewNPC orig, IEntitySource source, int x, int y, int type, int start,
            float ai0, float ai1, float ai2, float ai3, int target)
        {
            // Only the spawning authority (single player / server) decides whether a spawn is allowed;
            // a multiplayer client must never veto a server-authoritative spawn.
            // Wrapped defensively: a throwing gate must never break spawning — on error we log once and
            // fall through to the original NewNPC.
            try
            {
                bool authority = Main.netMode != NetmodeID.MultiplayerClient;
                SegmentChain.EnsureSize();

                // Eternia Crystal / DD2 Lane Portals: structural Old One's Army objectives. Always spawn them
                // and place them in the LOW (0-199) zone — vanilla draw, the right-click skip-wait interaction,
                // and the event's crystal stop-check / loot scans all assume 0-199. Guaranteed a slot.
                if (NpcCategorizer.IsStructuralLowZone(type))
                {
                    SlotAllocator.SetCategoryHint(NpcCategory.Boss);
                    return Mark(SpawnGuaranteed(orig, source, x, y, type, start, ai0, ai1, ai2, ai3, target, NpcCategory.Boss), chained: true, NpcCategory.Boss);
                }

                // Hard per-type active caps (performance). At Frost Moon wave 20 vanilla REMOVES the spawn cap
                // on its bosses, so with the raised cap they churn endlessly and tank the framerate. Re-impose
                // a sensible active limit per type. (Pumpkin Moon already self-limits to 2/2/3 via the now-fixed
                // CountNPCS.) Hardcoded for now; may become config in a later polish pass. Authority only.
                if (authority
                    && PerTypeActiveCaps.TryGetValue(type, out int typeCap)
                    && NPC.CountNPCS(type) >= typeCap)
                    return EngineState.NpcCap; // blocked: dummy/failure index

                // Source-aware category (detects boss segments/adds via their boss parent).
                NpcCategory cat = NpcCategorizer.CategorizeSpawn(type, source, out bool bossParented);
                bool chained = bossParented;

                // Multi-part bodies. A worm is a head plus dozens of segments, each its own NPC built one at a
                // time by the head's AI, and a segment refused by a cap produces a visibly chopped worm (or a
                // torso that immediately kills itself, since the AI despawns any segment whose neighbour is
                // gone). So every segment inherits its head's category, bypasses the ceiling, and is guaranteed
                // a slot. Two ways in:
                //
                //   INHERIT — the parent is already a tagged chain member. This carries the chain down bodies
                //   that parent each segment to the PREVIOUS segment rather than to the head (Eater of Worlds
                //   does this, and sets no realLife because it can split), where otherwise only the first
                //   segment would be recognised and the rest truncated.
                //
                //   SEED — the parent is an unchained head running the vanilla worm AI. This is what covers the
                //   ordinary worms and Wyverns, whose heads are plain Enemies: nothing about an Enemy parent
                //   marks its children as special, so before this they were capped like unrelated trash mobs.
                //   Boss-bodied worms (the Destroyer) already arrive via bossParented and never reach here.
                if (source is EntitySource_Parent parentSource && parentSource.Entity is NPC parentNpc && parentNpc.active)
                {
                    if (SegmentChain.IsMember(parentNpc.whoAmI))
                    {
                        chained = true;
                        cat = SegmentChain.CategoryOf(parentNpc.whoAmI);
                    }
                    else if (parentNpc.aiStyle == NPCAIStyleID.Worm)
                    {
                        chained = true;
                        cat = NpcCategorizer.Categorize(parentNpc);
                    }
                }

                bool isBoss = cat == NpcCategory.Boss;
                bool isTown = cat == NpcCategory.Town;
                // Town, Boss and any segment of a multi-part body are GUARANTEED a slot — never blocked by a
                // full array; we despawn a low-priority NPC to fit them. Lone Enemy/Critter are not guaranteed.
                bool guaranteed = isTown || isBoss || chained;

                // Ceiling enforcement (a hard block). Segments are never blocked (a worm must spawn whole), and
                // neither are rare critters — a permanently-full CritterCap would otherwise make the Empress of
                // Light and Duke Fishron unsummonable. See IsBlockedByCeiling.
                if (authority && !chained && !NpcCategorizer.IsSpecialCritter(type))
                {
                    var config = ModContent.GetInstance<ManyMoreMobsConfig>();
                    if (config != null && IsBlockedByCeiling(config, cat, isBoss))
                        return EngineState.NpcCap; // blocked: dummy/failure index (matches patched NewNPC)
                }

                // Hand the source-aware category to the slot allocator so boss segments/adds are placed in
                // the boss (low) zone, not the enemy (high) zone.
                SlotAllocator.SetCategoryHint(cat);

                int slot = guaranteed
                    ? SpawnGuaranteed(orig, source, x, y, type, start, ai0, ai1, ai2, ai3, target, cat)
                    : orig(source, x, y, type, start, ai0, ai1, ai2, ai3, target);

                // Tag the slot so the NEXT link in the chain (parented to this segment) is recognised. Bosses are
                // tagged even when standing alone, so their adds inherit correctly. Always write (true OR false)
                // so a reused slot never inherits a stale tag.
                return Mark(slot, chained || isBoss, cat);
            }
            catch (Exception e)
            {
                MmmLog.Report(e, $"NewNpcGate (type {type})");
            }

            return orig(source, x, y, type, start, ai0, ai1, ai2, ai3, target);
        }

        /// <summary>Record the spawned slot's chain membership (see <see cref="SegmentChain"/>) and pass it through.</summary>
        private static int Mark(int slot, bool chained, NpcCategory category)
        {
            if ((uint)slot < (uint)EngineState.NpcCap)
                SegmentChain.Mark(slot, chained, category);
            return slot;
        }

        /// <summary>
        /// A spawn that must always succeed (Town / Boss / boss segment).
        /// <para/>
        /// Try the caller's preferred placement first, then the whole array, and only if BOTH come back empty
        /// evict one low-priority NPC and try once more. Eviction is genuinely last-resort: it runs only when
        /// every slot in the array is occupied.
        /// <para/>
        /// <b>Why the eviction step is not optional in Expanded mode.</b> It used to be skipped there, on the
        /// reasoning that the reservation made it unnecessary — which quietly made the "worms always spawn
        /// whole" guarantee a no-op for everyone on the default settings, since Expanded IS the default.
        /// The reservation does guarantee each category its own zoned space — but a multi-part body bypasses
        /// its category ceiling on purpose, so a worm can legitimately need far more than the boss budget
        /// holds: three Destroyers want 246 slots against a BossCap of 100. The overflow spills into whatever
        /// is free, and once the array is genuinely full the retry cannot help, because
        /// <see cref="SlotAllocator"/> ignores <c>start</c> and runs the same four-pass search either way. So
        /// the second attempt was the first attempt again, and the segment was simply refused — a chopped worm,
        /// exactly the failure eviction exists to prevent.
        /// <para/>
        /// Getting a genuinely free slot also matters for its own sake. Vanilla's <c>GetAvailableNPCSlot</c>,
        /// finding no INACTIVE slot, falls back to REPLACING a "replaceable" NPC — and that path could hand
        /// back a slot inside the very worm being assembled. (Our allocator's Pass 2 already refuses chain
        /// members for this reason, and <c>CanBeReplacedByOtherNPCs</c> is false for almost everything, which
        /// is precisely why a full array is a hard stop rather than a soft one.)
        /// </summary>
        private static int SpawnGuaranteed(OrigNewNPC orig, IEntitySource source, int x, int y, int type, int start,
            float ai0, float ai1, float ai2, float ai3, int target, NpcCategory cat)
        {
            int cap = EngineState.NpcCap;

            if (ModContent.GetInstance<ManyMoreMobsConfig>()?.CapMode != NpcCapMode.Default)
            {
                int s = orig(source, x, y, type, start, ai0, ai1, ai2, ai3, target);
                if (s < cap)
                    return s;

                // The hint is consumed by each allocation, so re-arm it before retrying or the fallback
                // searches from the wrong end of the array.
                SlotAllocator.SetCategoryHint(cat);
                s = orig(source, x, y, type, 0, ai0, ai1, ai2, ai3, target);
                if (s < cap)
                    return s;
            }
            else if (HasFreeSlot())
            {
                SlotAllocator.SetCategoryHint(cat);
                return orig(source, x, y, type, 0, ai0, ai1, ai2, ai3, target);
            }

            // Array full. Only the spawning authority may evict: EntityEvictor syncs the removal from the
            // server, so a client evicting on its own would drop an NPC the server still believes is alive
            // (and would then disagree with it about who owns the slot). A client just lets the attempt fail
            // and takes the server's word for it on the next sync.
            if (Main.netMode != NetmodeID.MultiplayerClient)
                EntityEvictor.TryFreeSlot();

            // Attempted even if nothing was evictable: the allocator's Pass 2 can still replace a low-priority
            // NPC, and if that fails too it returns the failure slot on its own, exactly as vanilla would.
            SlotAllocator.SetCategoryHint(cat);
            return orig(source, x, y, type, 0, ai0, ai1, ai2, ai3, target);
        }

        /// <summary>True if any non-dummy NPC slot is currently inactive (a genuinely free slot exists).</summary>
        private static bool HasFreeSlot()
        {
            int cap = EngineState.NpcCap;
            for (int i = 0; i < cap; i++)
            {
                NPC n = Main.npc[i];
                if (n != null && !n.active)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Whether a (non-boss-parented) spawn is blocked by its category ceiling.
        /// <list type="bullet">
        /// <item><b>Town</b> — blocked at TownNPCCap (ceiling; within it, the spawn is guaranteed via eviction).</item>
        /// <item><b>Boss</b> — Default: never blocked (bosses always spawn). Expanded: respects the reserved
        /// BossCap so the low-zone budget holds.</item>
        /// <item><b>Critter / Enemy</b> — hard caps; these are never guaranteed and don't evict anyone.</item>
        /// </list>
        /// </summary>
        private static bool IsBlockedByCeiling(ManyMoreMobsConfig config, NpcCategory cat, bool isBoss)
        {
            var caps = config.GetEffectiveCaps();
            var counts = CategoryCounts.Snapshot();

            if (isBoss)
                return config.CapMode == NpcCapMode.Expanded && counts.boss >= caps.boss;

            return cat switch
            {
                NpcCategory.Town => counts.town >= caps.town,
                NpcCategory.Critter => counts.critter >= caps.critter,
                _ => counts.enemy >= caps.enemy, // Enemy
            };
        }
    }
}
