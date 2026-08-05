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
                EnsureBossChainSize();

                // Eternia Crystal / DD2 Lane Portals: structural Old One's Army objectives. Always spawn them
                // and place them in the LOW (0-199) zone — vanilla draw, the right-click skip-wait interaction,
                // and the event's crystal stop-check / loot scans all assume 0-199. Guaranteed a slot.
                if (NpcCategorizer.IsStructuralLowZone(type))
                {
                    SlotAllocator.SetCategoryHint(NpcCategory.Boss);
                    return Mark(SpawnGuaranteed(orig, source, x, y, type, start, ai0, ai1, ai2, ai3, target, NpcCategory.Boss), bossChain: true);
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

                // Boss-chain propagation. Chained worm bosses (Eater of Worlds) parent each segment to the
                // PREVIOUS segment — not the boss head — and don't set realLife (they can split), and their
                // body/tail types aren't boss-flagged. So only the head + first segment look boss-parented and
                // the rest get blocked, truncating the worm. If a spawn's parent is itself an active member of a
                // boss chain (we tag every guaranteed boss spawn below), treat this spawn as part of the boss too.
                if (!bossParented && source is EntitySource_Parent ep && ep.Entity is NPC pn
                    && pn.active && (uint)pn.whoAmI < (uint)_bossChain.Length && _bossChain[pn.whoAmI])
                {
                    bossParented = true;
                    cat = NpcCategory.Boss;
                }

                bool isBoss = bossParented || cat == NpcCategory.Boss;
                bool isTown = cat == NpcCategory.Town;
                // Town and Boss (and any boss-parented segment) are GUARANTEED a slot — never blocked by a full
                // array; we despawn a low-priority NPC to fit them. Enemy/Critter are not guaranteed.
                bool guaranteed = isTown || isBoss;

                // Ceiling enforcement (a hard block). Boss-parented segments are never blocked (a worm must
                // spawn whole), and neither are rare critters — a permanently-full CritterCap would otherwise
                // make the Empress of Light and Duke Fishron unsummonable. See IsBlockedByCeiling.
                if (authority && !bossParented && !NpcCategorizer.IsSpecialCritter(type))
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

                // Tag the slot so the NEXT link in a worm chain (parented to this segment) is recognised as
                // boss too. Always write (true OR false) so a reused slot never inherits a stale tag.
                return Mark(slot, bossChain: isBoss);
            }
            catch (Exception e)
            {
                MmmLog.Report(e, $"NewNpcGate (type {type})");
            }

            return orig(source, x, y, type, start, ai0, ai1, ai2, ai3, target);
        }

        // Per-slot "is this NPC part of an active boss chain" tag, so chained-worm segments (which parent to the
        // previous segment, not the boss head) keep being recognised as boss down the whole chain. Sized to the
        // NPC array; every gate spawn rewrites its slot's tag so a reused slot can't carry a stale value.
        private static bool[] _bossChain = System.Array.Empty<bool>();

        private static void EnsureBossChainSize()
        {
            if (_bossChain.Length < Main.npc.Length)
                System.Array.Resize(ref _bossChain, Main.npc.Length);
        }

        private static int Mark(int slot, bool bossChain)
        {
            if ((uint)slot < (uint)EngineState.NpcCap && slot < _bossChain.Length)
                _bossChain[slot] = bossChain;
            return slot;
        }

        /// <summary>
        /// True if this slot holds a boss-chain member (a boss, or a chained-worm segment like an Eater of
        /// Worlds body — which classifies as Enemy on its own but must never be evicted, or the despawn-to-
        /// make-room system would cannibalise the very worm it's trying to fit).
        /// </summary>
        internal static bool IsBossChain(int slot) =>
            (uint)slot < (uint)_bossChain.Length && _bossChain[slot];

        /// <summary>
        /// Carries a slot's boss-chain tag across a relocation (see <see cref="SlotRezoner"/>) and clears the
        /// vacated slot. The rezoner never moves a chain member, so the tag being carried is currently always
        /// false — but the destination may still hold a stale tag from a previous occupant, and clearing that
        /// is what actually matters here.
        /// </summary>
        internal static void MoveBossChainTag(int from, int to)
        {
            EnsureBossChainSize();

            bool tag = (uint)from < (uint)_bossChain.Length && _bossChain[from];
            if ((uint)to < (uint)_bossChain.Length)
                _bossChain[to] = tag;
            if ((uint)from < (uint)_bossChain.Length)
                _bossChain[from] = false;
        }

        /// <summary>
        /// A spawn that must always succeed (Town / Boss / boss segment).
        /// <para/>
        /// <b>Expanded</b> — the reservation already guarantees zoned space, so honour the caller's preferred
        /// placement, with a whole-array fallback if that range happens to be full.
        /// <para/>
        /// <b>Default</b> — the array is shared and can be completely full. Vanilla's <c>GetAvailableNPCSlot</c>,
        /// finding no INACTIVE slot, falls back to REPLACING a "replaceable" NPC — and a chained worm's body is
        /// replaceable, so the search hands back the spawner segment's own slot and every segment overwrites it
        /// (head + tail only). To avoid that entirely we guarantee a genuinely free slot first: if the array is
        /// full, evict one low-priority NPC, then spawn with <c>start = 0</c> so the inactive-slot search (never
        /// the replace fallback) places it. Segment chains are ai[]-indexed, so the resulting placement is fine.
        /// </summary>
        private static int SpawnGuaranteed(OrigNewNPC orig, IEntitySource source, int x, int y, int type, int start,
            float ai0, float ai1, float ai2, float ai3, int target, NpcCategory cat)
        {
            if (ModContent.GetInstance<ManyMoreMobsConfig>()?.CapMode != NpcCapMode.Default)
            {
                int s = orig(source, x, y, type, start, ai0, ai1, ai2, ai3, target);
                if (s >= EngineState.NpcCap)
                    s = orig(source, x, y, type, 0, ai0, ai1, ai2, ai3, target);
                return s;
            }

            if (!HasFreeSlot())
                EntityEvictor.TryFreeSlot();
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
