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
    /// Multi-part bodies inherit their head's category by two routes, because one of them is not enough:
    /// <list type="bullet">
    /// <item><see cref="SegmentChain"/> — a tag the spawn gate writes as each segment is created. This is the
    /// only route that works for the Eater of Worlds, which leaves <see cref="NPC.realLife"/> unset on its
    /// segments so it can split, and whose body/tail types carry no boss flag of any kind.</item>
    /// <item><see cref="NPC.realLife"/> — most worms (The Destroyer and friends) point it at the head, which
    /// still covers anything that reaches us without having passed the gate.</item>
    /// </list>
    /// Either way a full worm counts entirely under its head's category instead of leaking into the Enemy
    /// budget. Before 0.7.6.6 only the second route existed, and a 66-slot Eater of Worlds counted as one Boss
    /// plus sixty-five Enemies.
    /// </summary>
    public static class NpcCategorizer
    {
        /// <summary>Categorize a live NPC instance (used when counting active NPCs).</summary>
        public static NpcCategory Categorize(NPC npc)
        {
            // The chain tag first: it is the only thing that knows a body belongs to a head when the engine
            // itself doesn't say so. The Eater of Worlds is the case that matters — it deliberately leaves
            // realLife unset on its segments so it can split, and its body/tail types carry no boss flag, so
            // by realLife and flags alone a 66-slot boss counted as 1 Boss and 65 Enemies. See SegmentChain.
            if (SegmentChain.IsMember(npc.whoAmI))
                return SegmentChain.CategoryOf(npc.whoAmI);

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
        /// Rescue / transform-into-town NPCs: the bound Goblin/Wizard/Mechanic, Webbed Stylist, Sleeping
        /// Angler, Unconscious Man, stranded Golfer, the Tortured Soul, and the bound town slimes.
        /// <para/>
        /// The engine does NOT town-flag these (<c>ActsLikeTownNPC</c> covers only the Skeleton Merchant), so
        /// by flags they'd classify as Enemy — zoned into the high slots where the mouse-interaction scan
        /// (<c>Main.HoverOverNPCs</c>, hardcoded 0-199) can never hover or right-click them, subject to the
        /// Enemy cap (often FULL at horde spawn rates, silently blocking their spawn), and evictable by
        /// despawn-to-make-room. Worst of all, rescuing transforms them IN THE SAME SLOT — a high-slot rescue
        /// would strand the resulting town NPC above 200 where housing can't see it. So: classify them as
        /// Town — placed in the low zone, never evicted, guaranteed to spawn.
        /// </summary>
        public static bool IsRescueNpc(int type) => type switch
        {
            NPCID.BoundGoblin => true,          // 105 -> Goblin Tinkerer
            NPCID.BoundWizard => true,          // 106 -> Wizard
            NPCID.BoundMechanic => true,        // 123 -> Mechanic
            NPCID.WebbedStylist => true,        // 354 -> Stylist
            NPCID.SleepingAngler => true,       // 376 -> Angler
            NPCID.DemonTaxCollector => true,    // 534 Tortured Soul -> Tax Collector (Purification Powder, in place)
            NPCID.BartenderUnconscious => true, // 579 Unconscious Man -> Tavernkeep
            NPCID.GolferRescue => true,         // 589 -> Golfer
            NPCID.BoundTownSlimeOld => true,    // 685 Elder Slime (Old Shaking Chest)
            NPCID.BoundTownSlimePurple => true, // 686
            NPCID.BoundTownSlimeYellow => true, // 687
            _ => false,
        };

        /// <summary>
        /// Classify directly from an NPC's flags (no worm-segment indirection). Public so the type-table
        /// builder can reuse the exact same logic on a freshly <c>SetDefaults</c>-ed scratch NPC.
        /// </summary>
        public static NpcCategory ClassifyByFlags(NPC npc)
        {
            // Rescue NPCs first: their engine flags say Enemy, but they must live in the town zone
            // (see IsRescueNpc). Checked before the flag tests so nothing else can misfile them.
            if (IsRescueNpc(npc.type))
                return NpcCategory.Town;

            if (npc.isLikeATownNPC)
                return NpcCategory.Town;

            if (npc.boss || IsEventMiniboss(npc.type)
                || ((uint)npc.type < (uint)NPCID.Sets.ShouldBeCountedAsBoss.Length && NPCID.Sets.ShouldBeCountedAsBoss[npc.type]))
                return NpcCategory.Boss;

            if (npc.CountsAsACritter)
                return NpcCategory.Critter;

            return NpcCategory.Enemy;
        }

        /// <summary>Category from an NPC type id alone (no instance), via the prebuilt type table.</summary>
        public static NpcCategory CategorizeType(int type) => NpcTypeCategories.Get(type);

        /// <summary>
        /// A RARE critter — Prismatic Lacewing, Truffle Worm, gold/gem critters (see
        /// <see cref="NpcTypeCategories.IsSpecial"/>). They stay in the Critter category (they shouldn't eat
        /// town slots) but are exempt from the critter ceiling and from eviction: they are one-off spawns and
        /// several of them gate progression — the Lacewing summons the Empress of Light, the Truffle Worm
        /// summons Duke Fishron. At horde spawn rates CritterCap sits permanently full of bunnies, so without
        /// this exemption the gate silently refuses them and those bosses become unsummonable.
        /// <para/>
        /// Bounded: vanilla already guards these with its own "only one at a time" checks (e.g.
        /// <c>!AnyNPCs(661)</c> before spawning a Lacewing), so the exemption can only overshoot the cap by a
        /// couple of slots.
        /// </summary>
        public static bool IsSpecialCritter(int type) => NpcTypeCategories.IsSpecial(type);

        /// <inheritdoc cref="IsSpecialCritter(int)"/>
        public static bool IsSpecialCritter(NPC npc) => npc != null && NpcTypeCategories.IsSpecial(npc.type);

        /// <summary>
        /// Old One's Army wave minibosses. Vanilla gives them <b>no</b> boss flag at all — a Dark Mage is just
        /// an enemy with 500 HP, an Ogre one with 13,000 — so by flags alone they land in the Enemy category
        /// and are treated as ordinary trash: subject to the Enemy ceiling, and evictable to make room.
        /// <para/>
        /// That is wrong for a wave-ending miniboss. If the Enemy cap happens to be saturated when the wave
        /// triggers, the spawn gate refuses it outright and the wave can never be completed. Categorising them
        /// as Boss gives them the same guaranteed-slot-via-eviction and never-evicted treatment as Plantera or
        /// the Empress, which is plainly what they deserve — and it costs only a handful of the boss budget,
        /// and only while an Old One's Army is actually running.
        /// <para/>
        /// It also helps multiplayer as a side effect: the boss zone is the low 0-199 range, and several of
        /// vanilla's server-side "announce this spawn to clients" checks refuse to broadcast a slot ≥ 200 (see
        /// <c>MMMultiplayer</c>). It is NOT the multiplayer fix though — the ordinary wave enemies are Enemy
        /// by design and stay high, so the netcode patch is still what makes the event visible.
        /// <para/>
        /// Betsy is listed for completeness; she may already qualify via <c>ShouldBeCountedAsBoss</c>, in which
        /// case naming her here is a harmless no-op rather than a behaviour change.
        /// </summary>
        public static bool IsEventMiniboss(int type) => type switch
        {
            NPCID.DD2DarkMageT1 or NPCID.DD2DarkMageT3 => true,
            NPCID.DD2OgreT2 or NPCID.DD2OgreT3 => true,
            NPCID.DD2Betsy => true,
            _ => false,
        };

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
