using System;
using System.Reflection;
using Terraria;
using Terraria.ModLoader;

namespace ManyMoreMobs
{
    /// <summary>
    /// Category-zoned NPC slot allocation — the key to making Town/Boss work natively past the cap raise.
    /// <para/>
    /// Detours <c>NPC.GetAvailableNPCSlot</c> and chooses slots by category:
    /// <list type="bullet">
    /// <item><b>Town / Boss</b> search from the BOTTOM up — they cluster in the low slots (0..199).</item>
    /// <item><b>Enemy / Critter</b> search from the TOP down — they fill the bonus zone (200..cap).</item>
    /// </list>
    /// Because Town NPCs and boss heads end up in slots 0..199, every vanilla subsystem that only looks at
    /// the first 200 slots — housing, world save, boss health bars, map icons, event/boss-active checks,
    /// travel-merchant logic — keeps working with NO loop patching. Enemies/critters live in the bonus zone,
    /// which the patched gameplay loops (update/draw/combat) cover. Worm-boss segments are Enemy-category so
    /// they go high, but the boss HEAD stays low (so its health bar and "is a boss active" checks work); the
    /// segments still draw and take damage via the patched loops.
    /// <para/>
    /// Falls back to the opposite zone if the preferred one is full, so spawning never fails early. Returns -1
    /// only when the whole array is full (matching vanilla, so NewNPC then returns its dummy slot).
    /// </summary>
    public static class SlotAllocator
    {
        private delegate int OrigGetAvailableNPCSlot(int type, int startIndex);
        private delegate int HookGetAvailableNPCSlot(OrigGetAvailableNPCSlot orig, int type, int startIndex);

        // The category the NewNpcGate computed for the spawn currently in flight. It is SOURCE-aware, so
        // boss segments/adds (spawned by a boss) come through as Boss and get placed in the low/boss zone —
        // which CategorizeType (which only sees the segment's own type) could not detect. Consumed once.
        [System.ThreadStatic] private static NpcCategory? _categoryHint;

        // The slot of the NPC a body part was spawned from, or -1. Consumed once, like the category hint.
        [System.ThreadStatic] private static int _chainParentHint = -1;

        public static void SetCategoryHint(NpcCategory category) => _categoryHint = category;

        /// <summary>
        /// Tell the next allocation that it is a part of the body living in <paramref name="parentSlot"/>, so
        /// it can be placed the way the engine expects: ABOVE its parent.
        /// <para/>
        /// This is not cosmetic. <c>Main.UpdateNPCs</c> walks slots in ascending order, and a body part follows
        /// whatever is in front of it using that thing's CURRENT position. Vanilla guarantees the order by
        /// passing the head's own slot as <c>NewNPC</c>'s Start, so the head always moves before the parts
        /// behind it do. Zoning by category ignores Start, and Enemy fills downward, so parts were landing
        /// BELOW their head and updating before it — see <see cref="NewNpcGate"/> for what that costs.
        /// </summary>
        public static void SetChainParentHint(int parentSlot) => _chainParentHint = parentSlot;

        public static void Apply(Mod mod)
        {
            MethodInfo method = typeof(NPC).GetMethod("GetAvailableNPCSlot", BindingFlags.NonPublic | BindingFlags.Static);
            if (method == null)
            {
                mod.Logger.Error("[MMM] NPC.GetAvailableNPCSlot not found; slot-zoning not applied.");
                return;
            }

            try
            {
                MonoModHooks.Add(method, new HookGetAvailableNPCSlot(GetSlot_Hook));
                mod.Logger.Info("[MMM] Slot-zoning applied (Town/Boss low, Enemy/Critter high).");
            }
            catch (Exception e)
            {
                mod.Logger.Error($"[MMM] Slot-zoning failed (skipped): {e.Message}");
            }
        }

        private static int GetSlot_Hook(OrigGetAvailableNPCSlot orig, int type, int startIndex)
        {
            try
            {
                int cap = EngineState.NpcCap; // exclusive upper bound; index 'cap' is the dummy slot

                // Prefer the source-aware category from the gate (catches boss segments/adds); fall back to
                // the type-only category for any spawn that didn't go through the gate.
                NpcCategory category = _categoryHint ?? NpcCategorizer.CategorizeType(type);
                _categoryHint = null;

                int chainParent = _chainParentHint;
                _chainParentHint = -1;

                // A body part goes straight after its parent when anything up there is free, which restores
                // the update order vanilla relies on. When the zone above is full it falls through to the
                // ordinary search and the part is placed wherever it fits — still correct, just a frame
                // behind, and the spawn separation in NewNpcGate is what keeps that survivable.
                if (chainParent >= 0)
                {
                    int above = FirstFreeAbove(chainParent, cap);
                    if (above >= 0) return above;
                }

                bool lowFirst = category == NpcCategory.Town || category == NpcCategory.Boss;

                // Pass 1: a truly free slot, preferred direction then the other.
                int slot = lowFirst ? FreeAscending(cap) : FreeDescending(cap);
                if (slot >= 0) return slot;
                slot = lowFirst ? FreeDescending(cap) : FreeAscending(cap);
                if (slot >= 0) return slot;

                // Pass 2: a replaceable (low-priority) slot, same preference.
                slot = lowFirst ? ReplaceableAscending(cap) : ReplaceableDescending(cap);
                if (slot >= 0) return slot;
                slot = lowFirst ? ReplaceableDescending(cap) : ReplaceableAscending(cap);
                if (slot >= 0) return slot;

                return -1; // whole array full — NewNPC will return its dummy slot
            }
            catch (Exception e)
            {
                MmmLog.Report(e, $"SlotAllocator (type {type})");

                // Deliberately NOT orig(type, startIndex). Vanilla's search ends on `i != 200`, so from any
                // startIndex above 199 it can never reach its own terminator and walks off the end of Main.npc
                // instead. Every enemy lives above 199 under a raised cap, and a worm asks for its segments
                // with the HEAD's slot as the start — the highest occupied index there is, since Enemy spawns
                // fill downward — so this fallback would fail exactly where a body is being assembled.
                return SafeScan(EngineState.NpcCap);
            }
        }

        /// <summary>
        /// A whole-array search that cannot throw and cannot be confused by the raised cap: the fallback for
        /// when the zoned search above fails. Free slots first, then replaceable ones, ignoring zoning — at
        /// this point placing the NPC at all matters more than placing it well.
        /// </summary>
        private static int SafeScan(int cap)
        {
            int end = Math.Min(cap, Main.npc.Length);

            for (int i = end - 1; i >= 0; i--)
                if (Main.npc[i] != null && !Main.npc[i].active) return i;

            for (int i = end - 1; i >= 0; i--)
                if (Main.npc[i] != null && Main.npc[i].CanBeReplacedByOtherNPCs && !SegmentChain.IsMember(i)) return i;

            return -1;
        }

        /// <summary>First free slot strictly above <paramref name="parentSlot"/>, or -1 if there is none.</summary>
        private static int FirstFreeAbove(int parentSlot, int cap)
        {
            int end = Math.Min(cap, Main.npc.Length);
            for (int i = parentSlot + 1; i < end; i++)
                if (Main.npc[i] != null && !Main.npc[i].active) return i;
            return -1;
        }

        // All four passes bound themselves by the ARRAY, not just the cap. The two are meant to agree (the
        // resizer makes Main.npc cap+1 long), but if they ever drift, an out-of-range read here would throw
        // the whole allocation into the fallback above — and a worm mid-assembly would lose its remaining
        // segments to it. Costs one Math.Min per spawn.
        private static int FreeAscending(int cap)
        {
            int end = Math.Min(cap, Main.npc.Length);
            for (int i = 0; i < end; i++)
                if (Main.npc[i] != null && !Main.npc[i].active) return i;
            return -1;
        }

        private static int FreeDescending(int cap)
        {
            for (int i = Math.Min(cap, Main.npc.Length) - 1; i >= 0; i--)
                if (Main.npc[i] != null && !Main.npc[i].active) return i;
            return -1;
        }

        // Pass 2 never hands out a boss-chain member's slot. Chained-worm bodies (e.g. Eater of Worlds) are
        // flagged CanBeReplacedByOtherNPCs by vanilla, so in a COMPLETELY full array the replace fallback could
        // otherwise overwrite a worm boss's own segments — the Pass-2 twin of the EntityEvictor's guard.
        private static int ReplaceableAscending(int cap)
        {
            int end = Math.Min(cap, Main.npc.Length);
            for (int i = 0; i < end; i++)
                if (Main.npc[i] != null && Main.npc[i].CanBeReplacedByOtherNPCs && !SegmentChain.IsMember(i)) return i;
            return -1;
        }

        private static int ReplaceableDescending(int cap)
        {
            for (int i = Math.Min(cap, Main.npc.Length) - 1; i >= 0; i--)
                if (Main.npc[i] != null && Main.npc[i].CanBeReplacedByOtherNPCs && !SegmentChain.IsMember(i)) return i;
            return -1;
        }
    }
}
