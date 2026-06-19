using Terraria;
using Terraria.DataStructures;
using Terraria.ModLoader;

namespace ManyMoreMobs
{
    /// <summary>
    /// Clears stale per-NPC hit-immunity when an NPC spawns into a slot.
    /// <para/>
    /// NPC slots are reused, but the engine never resets two slot-keyed immunity stores when a new NPC takes a
    /// slot: <see cref="Projectile.perIDStaticNPCImmunity"/> (per projectile-TYPE; some weapons use the
    /// "hit once ever" cooldown of -1, which writes a permanent timestamp) and the NPC's own per-player i-frame
    /// array. In the fast-churning bonus zone (200+) a fresh enemy therefore INHERITS the dead occupant's
    /// immunity and becomes permanently un-hittable by multi-hit projectile weapons (Terra Blade beam, flails,
    /// Night's Edge swing) while still being hittable by single-hit / true-melee weapons. Resetting on spawn
    /// guarantees every new enemy starts fully hittable. (A fresh NPC having no prior immunity is always
    /// correct, so this can't break vanilla "hit once per life" behavior, which still accrues during the life.)
    /// </summary>
    public class SlotImmunityReset : GlobalNPC
    {
        public override void OnSpawn(NPC npc, IEntitySource source)
        {
            int slot = npc.whoAmI;
            if (slot < 0)
                return;

            // Per-projectile-TYPE static immunity, keyed by NPC slot.
            uint[][] perID = Projectile.perIDStaticNPCImmunity;
            if (perID != null)
            {
                for (int t = 0; t < perID.Length; t++)
                {
                    uint[] a = perID[t];
                    if (a != null && slot < a.Length)
                        a[slot] = 0u;
                }
            }

            // The NPC's own per-player i-frame array.
            int[] immune = npc.immune;
            if (immune != null)
            {
                for (int p = 0; p < immune.Length; p++)
                    immune[p] = 0;
            }
        }
    }
}
