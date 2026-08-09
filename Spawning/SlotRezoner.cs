using System;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace ManyMoreMobs
{
    /// <summary>
    /// Repairs slot zoning for NPCs whose category changed <b>after</b> they were placed.
    /// <para/>
    /// <see cref="SlotAllocator"/> zones by category at spawn time, but that decision is made once, from the
    /// type being spawned. Two things can invalidate it later:
    /// <list type="bullet">
    /// <item><b>In-place transforms.</b> <c>NPC.Transform</c> mutates the NPC where it stands — it never
    /// re-allocates a slot. A regular slime is an Enemy, so it lives in the high zone; hand it a Copper
    /// Shortsword and it becomes the Copper Town Slime <i>at the same high index</i>, a Town NPC stranded
    /// where the un-patched vanilla loops can't see it. Modded NPCs use <c>Transform</c> freely, so this is
    /// not a two-id special case.</item>
    /// <item><b>Zone-full fallbacks.</b> <see cref="SlotAllocator"/> deliberately falls back to the opposite
    /// zone rather than failing a spawn, so a Town NPC created while 0-199 happened to be full lands high and
    /// stays there even after the low zone drains.</item>
    /// </list>
    /// So once every <see cref="SweepInterval"/> ticks we look above the horizon for Town NPCs and move them
    /// down. What that buys, concretely: the housing overlay (<c>Main.DrawNPCHousesInWorld</c> /
    /// <c>DrawNPCHousesInUI</c>) and the town-NPC census in <c>Main.UpdateTime_SpawnTownNPCs</c> are all
    /// hardcoded 0-199 and deliberately left un-patched, because zoning is supposed to make patching them
    /// unnecessary. Without this sweep the guarantee has a hole and the fix is "reload the world", which is
    /// no fix at all when the whole point of moving a town pet is pylon/happiness bookkeeping.
    /// <para/>
    /// <b>Town only, on purpose.</b> Boss is deliberately not rezoned: a boss mid-fight is referenced by index
    /// from its own segments (<c>realLife</c>), its minions' <c>ai[]</c> and homing projectiles, none of which
    /// we can rewrite reliably — and unlike a town NPC there is no in-place transform that produces one. A
    /// boss only lands high if 0-199 was completely full, which the reservation already prevents.
    /// </summary>
    public class SlotRezoner : ModSystem
    {
        /// <summary>
        /// The index every un-patched vanilla NPC loop stops at. A Town NPC at or above this is invisible to
        /// all of them, which is the entire problem this class exists to fix.
        /// </summary>
        private const int VanillaHorizon = 200;

        /// <summary>Ticks between sweeps. Half a second is imperceptible for a transform and keeps the scan off the per-frame path.</summary>
        private const int SweepInterval = 30;

        private int _tick;

        // Every other subsystem logs a line when it installs itself, which is how a log read can answer "did
        // this actually apply?" — the question that turned out to matter when a broadcast patch was silently
        // matching nothing. A ModSystem installs no hook, so it has to say so explicitly.
        public override void OnModLoad()
            => Mod.Logger.Info($"[MMM] Slot rezoner active (Town NPCs above slot {VanillaHorizon} return to the low zone).");

        public override void PostUpdateNPCs()
        {
            // Slot assignment is server-authoritative. A client relocating on its own would disagree with the
            // server (and every other client) about which index holds which NPC; it receives the move as the
            // two SyncNPC packets Relocate sends instead.
            if (Main.netMode == NetmodeID.MultiplayerClient)
                return;

            if (++_tick < SweepInterval)
                return;
            _tick = 0;

            // A throwing sweep must never break the NPC update loop — log once and skip this pass.
            try { Sweep(); }
            catch (Exception e) { MmmLog.Report(e, "SlotRezoner sweep"); }
        }

        private static void Sweep()
        {
            int cap = EngineState.NpcCap;
            if (cap <= VanillaHorizon)
                return; // Default cap mode: there is no zone above the horizon, so nothing can be misplaced

            for (int i = VanillaHorizon; i < cap; i++)
            {
                NPC n = Main.npc[i];
                if (n == null || !n.active)
                    continue;

                // ClassifyByFlags on the live instance, not the type table: it reads the CURRENT flags, which
                // is the whole point after a transform, and it also catches the rescue NPCs (Enemy by flags,
                // Town by our own rule) that fell back into the high zone.
                if (NpcCategorizer.ClassifyByFlags(n) != NpcCategory.Town)
                    continue;

                if (!IsSafeToMove(n, i))
                    continue;

                int destination = FindFreeSlotBelowHorizon();
                if (destination < 0)
                    return; // town zone genuinely full — leave everything put and retry next sweep

                Relocate(i, destination);
            }
        }

        /// <summary>
        /// Whether this NPC can be re-indexed without stranding a reference to its old slot.
        /// <para/>
        /// Deliberately absent: rewriting other NPCs' <c>target</c> fields. NPC-on-NPC targeting encodes the
        /// index as <c>whoAmI + 300</c> and <c>NPC.HasNPCTarget</c> only accepts <c>300 &lt;= target &lt; 500</c>,
        /// so an NPC in a slot ≥ 200 can never have been a valid NPC target in the first place — there is no
        /// such reference to fix up. (That 500 ceiling is its own latent cap bug, just not this one.)
        /// </summary>
        private static bool IsSafeToMove(NPC n, int slot)
        {
            // Shared-health bodies address their head by index; moving either end breaks the link.
            if (n.realLife >= 0)
                return false;

            // Never re-index part of a multi-part body: segments address their neighbours by slot through
            // ai[0]/ai[1], so moving one severs the worm. Nothing reaching here should be a member (they don't
            // classify as Town), but eviction and Pass-2 replacement guard the same way for the same reason.
            if (SegmentChain.IsMember(slot))
                return false;

            // An open chat/shop is addressed by NPC index, and in multiplayer the CLIENT owns its copy of
            // talkNPC — we can fix ours but not theirs, so the UI would point at an empty slot. Talking is
            // brief; deferring the move until they walk away costs nothing.
            for (int p = 0; p < Main.player.Length; p++)
            {
                Player player = Main.player[p];
                if (player != null && player.active && player.talkNPC == slot)
                    return false;
            }

            return true;
        }

        /// <summary>First inactive slot below the horizon, or -1 if the low zone is full.</summary>
        private static int FindFreeSlotBelowHorizon()
        {
            for (int i = 0; i < VanillaHorizon; i++)
            {
                NPC n = Main.npc[i];
                if (n != null && !n.active)
                    return i;
            }
            return -1;
        }

        /// <summary>
        /// Moves the NPC object itself from one slot to the other and re-stamps its <c>whoAmI</c>.
        /// <para/>
        /// Replacing an entry of <c>Main.npc</c> is vanilla's own idiom, not a trick: <c>NPC.NewNPC</c> does
        /// <c>Main.npc[slot] = new NPC()</c> before every single spawn, and <c>Main.UpdateNPCs</c> does it
        /// again to recover from a throwing update. Moving the live object (rather than copying fields) also
        /// keeps every <c>ModNPC</c> / <c>GlobalNPC</c> instance and its data attached, since those hang off
        /// the object and never off the index.
        /// </summary>
        private static void Relocate(int from, int to)
        {
            NPC moving = Main.npc[from];
            string name = moving.TypeName;
            bool server = Main.netMode == NetmodeID.Server;

            // Retire the old index while the NPC is still sitting in it, so the packet carries its real type
            // with life 0 — the ordinary "this NPC is gone" sync every client already knows how to apply, and
            // the same shape EntityEvictor uses. Announcing the vacated slot BEFORE the new one matters: a
            // client that applied them the other way round would briefly hold the same town NPC twice.
            if (server)
            {
                moving.active = false;
                NetMessage.SendData(MessageID.SyncNPC, -1, -1, null, from);
                moving.active = true;
            }

            NPC vacated = new NPC();
            vacated.SetDefaults(NPCID.None);
            vacated.whoAmI = from;
            vacated.active = false;
            Main.npc[from] = vacated;

            Main.npc[to] = moving;
            moving.whoAmI = to;
            moving.netUpdate = true;

            SegmentChain.Move(from, to);

            if (server)
                NetMessage.SendData(MessageID.SyncNPC, -1, -1, null, to);

            MmmLog.Info($"Rezoned town NPC '{name}' (type {moving.type}) from slot {from} to {to}.");
        }
    }
}
