using System;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace ManyMoreMobs
{
    /// <summary>
    /// Grows the engine arrays that are indexed by NPC slot to fit the raised cap. These are the arrays
    /// verified (by decompiling this tModLoader build) to be sized to 200 and indexed by NPC <c>whoAmI</c>
    /// <b>without</b> bounds guards — leaving any of them at 200 means an <c>IndexOutOfRangeException</c> the
    /// moment an NPC in a slot ≥200 is touched (updated, drawn, or hit by a projectile).
    /// <para/>
    /// <list type="bullet">
    /// <item><c>Main.npc</c> — the entity array itself (size cap+1; the extra slot is the failure/dummy slot)</item>
    /// <item><c>NPC.lazyNPCOwnedProjectileSearchArray</c> — indexed by <c>whoAmI</c></item>
    /// <item>every <c>Projectile.localNPCImmunity</c> — per-projectile hit cooldown, indexed by NPC slot</item>
    /// <item>every <c>Projectile.perIDStaticNPCImmunity[type]</c> — per-projectile-type hit cooldown</item>
    /// <item>every <c>Player.meleeNPCHitCooldown</c> — per-player melee hit cooldown, indexed by NPC slot</item>
    /// </list>
    /// The per-projectile <c>localNPCImmunity</c> is also a field initializer (<c>new int[200]</c>) that runs
    /// for every projectile created later, so its allocation is additionally IL-patched in
    /// <see cref="EngineILPatcher"/>; this method only fixes the ~1000 projectiles that already exist.
    /// </summary>
    internal static class EngineArrayResizer
    {
        public static void Grow(int total, Mod mod)
        {
            int npcArrayLen = total + 1; // +1 for the dummy/failure slot at index == maxNPCs

            // Main.npc — preserve existing entries, populate the new slots with real (inactive) NPCs.
            if (Main.npc.Length < npcArrayLen)
            {
                int oldLen = Main.npc.Length;
                Array.Resize(ref Main.npc, npcArrayLen);
                for (int i = oldLen; i < npcArrayLen; i++)
                {
                    var n = new NPC();
                    n.SetDefaults(NPCID.None);
                    n.whoAmI = i;
                    n.active = false;
                    Main.npc[i] = n;
                }
            }

            // NPC.lazyNPCOwnedProjectileSearchArray — static, indexed by whoAmI.
            if (NPC.lazyNPCOwnedProjectileSearchArray.Length < total)
                Array.Resize(ref NPC.lazyNPCOwnedProjectileSearchArray, total);

            // Per-projectile localNPCImmunity — fix the projectiles that already exist.
            for (int i = 0; i < Main.projectile.Length; i++)
            {
                Projectile p = Main.projectile[i];
                if (p?.localNPCImmunity != null && p.localNPCImmunity.Length < total)
                    Array.Resize(ref p.localNPCImmunity, total);
            }

            // Projectile.perIDStaticNPCImmunity[type] — one array per projectile type, indexed by NPC slot.
            uint[][] perId = Projectile.perIDStaticNPCImmunity;
            if (perId != null)
            {
                for (int t = 0; t < perId.Length; t++)
                {
                    if (perId[t] != null && perId[t].Length < total)
                        Array.Resize(ref perId[t], total);
                }
            }

            // Per-player meleeNPCHitCooldown — fix the players that already exist.
            for (int i = 0; i < Main.player.Length; i++)
            {
                Player pl = Main.player[i];
                if (pl?.meleeNPCHitCooldown != null && pl.meleeNPCHitCooldown.Length < total)
                    Array.Resize(ref pl.meleeNPCHitCooldown, total);
            }

            mod.Logger.Info($"[MMM] Grew NPC-indexed engine arrays to fit {total} NPCs (Main.npc length {Main.npc.Length}).");
        }
    }
}
