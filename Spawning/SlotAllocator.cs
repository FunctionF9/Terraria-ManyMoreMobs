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

        public static void SetCategoryHint(NpcCategory category) => _categoryHint = category;

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
                return orig(type, startIndex); // never break spawning
            }
        }

        private static int FreeAscending(int cap)
        {
            for (int i = 0; i < cap; i++)
                if (!Main.npc[i].active) return i;
            return -1;
        }

        private static int FreeDescending(int cap)
        {
            for (int i = cap - 1; i >= 0; i--)
                if (!Main.npc[i].active) return i;
            return -1;
        }

        // Pass 2 never hands out a boss-chain member's slot. Chained-worm bodies (e.g. Eater of Worlds) are
        // flagged CanBeReplacedByOtherNPCs by vanilla, so in a COMPLETELY full array the replace fallback could
        // otherwise overwrite a worm boss's own segments — the Pass-2 twin of the EntityEvictor's guard.
        private static int ReplaceableAscending(int cap)
        {
            for (int i = 0; i < cap; i++)
                if (Main.npc[i].CanBeReplacedByOtherNPCs && !NewNpcGate.IsBossChain(i)) return i;
            return -1;
        }

        private static int ReplaceableDescending(int cap)
        {
            for (int i = cap - 1; i >= 0; i--)
                if (Main.npc[i].CanBeReplacedByOtherNPCs && !NewNpcGate.IsBossChain(i)) return i;
            return -1;
        }
    }
}
