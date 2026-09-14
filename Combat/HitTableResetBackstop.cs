using System;
using Terraria;
using Terraria.ModLoader;

namespace ManyMoreMobs
{
    /// <summary>
    /// Clears slots 200+ of the two per-enemy hit tables from our own code, at the moments the engine resets
    /// them, and counts every time the engine's reset had left something there.
    /// <para/>
    /// Both engine resets are IL-patched to the raised cap already: <c>Projectile.ResetLocalNPCHitImmunity</c>,
    /// run from <c>Projectile.SetDefaults</c> on every spawn, and <c>Player.ResetMeleeHitCooldowns</c>, run once
    /// per swing from <c>ItemCheck_StartActualUse</c>. This is a second line behind those patches, added because
    /// in one reproduction the patch was registered and the reset still left slots 200+ untouched. The mod had
    /// been enabled mid-session and reloaded without a restart; a whip and Night's Edge then hit each enemy once
    /// and never again, while <c>/mmm info</c> reported every patch applied.
    /// <para/>
    /// Suspected, not proven: both methods are small enough for the JIT to inline, and a caller optimised before
    /// our patch registered would keep the old copy for the life of the process. tModLoader reloads mods inside
    /// the same process. Whether a normal fresh launch can get there is unmeasured. Nothing below depends on that
    /// explanation being right; it only looks at the tables.
    /// <para/>
    /// Why these weapons: a whip or projectile-swung sword marks each enemy it hits with -1 in its projectile's
    /// table, meaning "not again this life", and the engine's per-tick countdown only touches positive entries.
    /// The spawn reset is the only thing that clears a -1. The swing swords' melee cooldown is likewise only
    /// cleared at swing start (see <see cref="SwingCooldownDrain"/>). Weapons with a positive cooldown recover
    /// through the countdown on their own.
    /// <para/>
    /// Both hooks are our own code, compiled after the mod loads and reached through tModLoader's hook lists
    /// rather than through a patched method. They only write what the patched reset would have written, so while
    /// the patch works they find nothing to do. The cost is one scan of the table per spawn and per swing; not
    /// measured.
    /// </summary>
    internal static class HitTableResetBackstop
    {
        // The engine's hardcoded bound, i.e. the first slot an unpatched reset never reaches.
        private const int VanillaSlots = 200;

        /// <summary>Projectile spawns whose hit table still had entries above slot 199 after the engine's reset.</summary>
        internal static int ProjectileMisses { get; private set; }

        /// <summary>Swing starts whose melee cooldowns still had entries above slot 199 after the engine's reset.</summary>
        internal static int SwingMisses { get; private set; }

        internal static int Misses => ProjectileMisses + SwingMisses;

        private static bool _announced;

        /// <summary>Zeroes slots 200+ of a table, returning whether any of them were non-zero.</summary>
        internal static bool ClearAboveVanillaSlots(int[] table)
        {
            if (table == null || table.Length <= VanillaSlots)
                return false;
            Span<int> above = table.AsSpan(VanillaSlots);
            if (!above.ContainsAnyExcept(0))
                return false;
            above.Clear();
            return true;
        }

        /// <summary>Whether slots 0-199 are all zero, which the engine's reset always leaves, patched or not.</summary>
        internal static bool VanillaSlotsClear(int[] table) =>
            table != null && table.Length >= VanillaSlots && !table.AsSpan(0, VanillaSlots).ContainsAnyExcept(0);

        internal static void NoteProjectileMiss(int projectileType)
        {
            ProjectileMisses++;
            Announce($"projectile {HitTracker.ProjectileName(projectileType)} (type {projectileType})");
        }

        internal static void NoteSwingMiss(int itemType)
        {
            SwingMisses++;
            Announce($"swing of item type {itemType}");
        }

        // A miss means "patch in, effect missing" only while both reset patches are actually in place. If either one
        // failed to apply, or registration stopped before reaching them, a miss is just that failure showing and a
        // restart will not change it, so the advice must not claim it will.
        private static bool ResetPatchFailed =>
            EngineILPatcher.RegistrationAborted != null
            || EngineILPatcher.PatchProblems.Exists(p =>
                p.Contains(nameof(Projectile.ResetLocalNPCHitImmunity)) || p.Contains(nameof(Player.ResetMeleeHitCooldowns)));

        // Once per session: /mmm info keeps the running count, the log only needs the first one.
        private static void Announce(string firstSeenOn)
        {
            if (_announced)
                return;
            _announced = true;
            string cause = ResetPatchFailed
                ? "Its patch did not apply this session (see the patch errors in client.log), so this is that failure " +
                  "showing, and a restart will not change it."
                : "Its patch reported as applied. This has been seen after enabling the mod mid-session without " +
                  "restarting the game. A full restart is suggested, since other small patched engine methods may be " +
                  "skipped the same way.";
            MmmLog.Warn($"The game's own hit-table reset left enemy slots 200+ uncleared (first seen on a {firstSeenOn}). " +
                        $"Many More Mobs clears them itself, so weapons should keep hitting. {cause}");
        }

        /// <summary>The next step for <c>/mmm info</c>, which depends on whether the reset patches applied.</summary>
        internal static string Advice() => ResetPatchFailed
            ? "its patch did not apply (see the patches line); weapons should still be covered, and a restart will not change it"
            : "seen after enabling the mod mid-session; weapons should still be covered, but a full game restart (not Reload Mods) is suggested";

        /// <summary>One line for <c>/mmm info</c>; empty when nothing has been missed.</summary>
        internal static string Summary() => Misses == 0 ? "" :
            $"the game's own reset left enemy slots 200+ uncleared on {ProjectileMisses} projectile spawn(s) " +
            $"and {SwingMisses} swing(s); Many More Mobs cleared them";
    }

    /// <summary>The projectile half of <see cref="HitTableResetBackstop"/>.</summary>
    public class ProjectileHitTableBackstop : GlobalProjectile
    {
        // Runs from SetDefaults_End (ProjectileLoader.SetDefaults) at the end of every Projectile.SetDefaults, so on
        // every spawn and slot reuse, after the engine's own reset near its top. Nothing in vanilla writes the table
        // in between. A modded projectile's own SetDefaults, or another mod's GlobalProjectile.SetDefaults that runs
        // before ours, could; anything it wrote above slot 199 would be cleared here and counted as a miss.
        public override void SetDefaults(Projectile projectile)
        {
            if (HitTableResetBackstop.ClearAboveVanillaSlots(projectile.localNPCImmunity))
                HitTableResetBackstop.NoteProjectileMiss(projectile.type);
        }
    }

    /// <summary>The swing half of <see cref="HitTableResetBackstop"/>.</summary>
    public class SwingHitTableBackstop : GlobalItem
    {
        // ItemLoader.UseAnimation is the first statement of Player.ApplyItemAnimation, and the engine's only call to
        // that is in ItemCheck_StartActualUse, on the line right after ResetMeleeHitCooldowns(). So this runs once
        // per swing start, after the engine's reset and before anything in the swing can hit.
        public override void UseAnimation(Item item, Player player)
        {
            // The engine's reset only runs for the local player, since the cooldowns are client-side. This hook
            // also fires for remote players and on the server.
            if (player.whoAmI != Main.myPlayer)
                return;

            int[] cooldowns = player.meleeNPCHitCooldown;

            // A partial guard against mods that call ApplyItemAnimation mid-swing (vanilla never does). The engine's
            // reset always zeroes slots 0-199, so a non-zero entry there means it did not just run, and clearing
            // slots 200+ then would hand out extra hits. It cannot catch every such call: with enemies living in
            // 200+, slots 0-199 are often all zero mid-swing as well.
            if (!HitTableResetBackstop.VanillaSlotsClear(cooldowns))
                return;

            if (HitTableResetBackstop.ClearAboveVanillaSlots(cooldowns))
                HitTableResetBackstop.NoteSwingMiss(item.type);
        }
    }
}
