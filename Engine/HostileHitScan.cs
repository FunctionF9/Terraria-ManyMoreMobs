using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Terraria;
using Terraria.ModLoader;

namespace ManyMoreMobs
{
    /// <summary>
    /// Shortens the NPC hit scan inside <c>Projectile.Damage</c> for HOSTILE projectiles. This is the single
    /// largest cost the raised cap introduces, and it buys nothing.
    /// <para/>
    /// <b>The loop.</b> <c>Projectile.Damage</c> contains
    /// <code>for (int i = 0; i &lt; 200 &amp;&amp; flag4; i++)</code>
    /// nested inside <c>if (owner == Main.myPlayer)</c> and <c>if (damage &gt; 0)</c> — note that it is NOT
    /// behind <c>friendly</c>. Every vanilla NPC-fired projectile is spawned with <c>Main.myPlayer</c> as its
    /// owner, so enemy projectiles enter it too, and <see cref="EngineILPatcher"/> rewrites that <c>200</c> to
    /// the live cap. At cap 3000 an enemy projectile therefore scans up to 3000 NPC slots every tick.
    /// <para/>
    /// <b>Why it is wasted.</b> The loop body's outcome gate is
    /// <c>flag6 || (friendly &amp;&amp; …) || (hostile &amp;&amp; npc.friendly &amp;&amp; !npc.dontTakeDamageFromHostiles)</c>.
    /// A hostile, non-friendly projectile can therefore only ever reach an NPC with <c>npc.friendly</c> set —
    /// in practice the town NPCs. Critters do NOT set it (a Bunny is <c>damage = 0, aiStyle = 7</c> and nothing
    /// more), so they are not reachable either. Everything else in the scan — three chained hook enumerations
    /// per NPC via <c>CombinedHooks.CanHitNPCWithProj</c>, plus <c>CanNPCBeHitByPlayerOrPlayerProjectile</c> and
    /// a write to <c>npc.position</c> — runs in full and is then thrown away.
    /// <para/>
    /// <b>Measured.</b> In a cap-3000 stress run this phase was 95.3% of all projectile time, scaling at about
    /// 44.7 ns per active NPC per projectile per tick: ~50.8 µs/projectile at 718 NPCs and ~89.1 µs at 1574.
    /// With ~850 projectiles alive that is ~1.34 million loop-body executions and ~4 million hook dispatches
    /// per tick, to find (in that run) exactly one reachable NPC — the Old Man in slot 0.
    /// <para/>
    /// <b>The fix.</b> Return a bound covering only up to the highest slot currently holding a friendly NPC,
    /// so the scan stops there instead of walking the whole array. Friendly projectiles are untouched: a
    /// player's weapon genuinely does need to see every enemy, which is the entire point of the raised cap.
    /// <para/>
    /// <b>The exception, and why this can turn itself off.</b> <c>flag6</c> above comes from a mod returning a
    /// non-null <c>true</c> out of <c>CombinedHooks.CanHitNPCWithProj</c>, which is exactly how an "enemies
    /// damage each other" mod is written. Shortening the scan skips that hook, so the shortcut is only
    /// provably identical to vanilla when no mod occupies any of the five hooks involved. We detect that at
    /// load (see <see cref="DetectModHooks"/>) and fall back to the full scan when one does, unless the player
    /// deliberately overrides it. Correctness is the default; the speed is opt-in past that point.
    /// <para/>
    /// <b>Scope.</b> None of this exists in Default cap mode: <see cref="MaxNpcCapRaise"/> returns before
    /// <c>EngineILPatcher.ApplyAll</c> when the mode is not Expanded, so the loop is never rewritten and
    /// vanilla's own <c>200</c> stands. The shortcut can only be reached where the cost it removes is ours.
    /// </summary>
    public static class HostileHitScan
    {
        /// <summary>
        /// Names of loaded mods that override a hook capable of forcing a hostile projectile to hit a hostile
        /// NPC. Empty when the shortcut is provably safe. Reported by <c>/mmm info</c> and logged at load.
        /// </summary>
        internal static readonly List<string> ConflictingMods = new();

        /// <summary>True when some other mod occupies one of the five hooks. Set once, at load.</summary>
        internal static bool ModHooksPresent => ConflictingMods.Count > 0;

        // Recomputed at most once per game tick, on first use. The scan bound cannot simply be cached forever:
        // town NPCs move between slots as they die and respawn.
        private static uint _stamp = uint.MaxValue;
        private static int _bound;
        private static bool _active;

        /// <summary>
        /// The bound <c>Projectile.Damage</c>'s NPC loops should run to for this projectile.
        /// <para/>
        /// Called from IL <b>once per <c>Damage()</c> call</b> — i.e. once per projectile per tick — and cached
        /// in a local there. It must stay that way: a C# <c>for</c> loop evaluates its bound inside the
        /// per-iteration condition, so wiring this in as the literal's replacement would call it once per NPC.
        /// <see cref="EngineILPatcher"/> hoists it for that reason; the note there has the engine IL.
        /// <para/>
        /// <b>Must never throw</b> — it stands in for a constant in the middle of vanilla's damage path.
        /// </summary>
        public static int ScanBound(Projectile projectile)
        {
            int cap = EngineState.NpcCap;

            try
            {
                // A friendly projectile has to be able to reach every enemy in the world, so it always gets the
                // full array. This also covers the both-flags case (friendly && hostile) by erring wide.
                if (projectile == null || projectile.friendly || !projectile.hostile)
                    return cap;

                Refresh(cap);
                return _active ? _bound : cap;
            }
            catch
            {
                return cap;
            }
        }

        /// <summary>
        /// Notes that a friendly NPC has appeared in <paramref name="slot"/>, growing this tick's bound if
        /// needed. Called from <see cref="SlotImmunityReset"/>'s existing spawn hook, so it costs no extra
        /// hook registration.
        /// <para/>
        /// Without this the bound could be one tick stale, and a town NPC that spawned mid-tick would be
        /// briefly immune to enemy fire. The window is tiny and probably unreachable in practice, but "town
        /// NPC silently cannot be hurt" is not a bug anyone would connect back to a performance change.
        /// </summary>
        internal static void NoteFriendlySlot(int slot)
        {
            if (slot >= _bound)
                _bound = slot + 1;
        }

        // Recompute the bound once per tick, lazily. O(cap) once, against the O(cap x projectiles) it replaces.
        //
        // _active is cleared first and _stamp set LAST, so a throw part-way through leaves the shortcut off
        // and the tick un-stamped rather than leaving a half-computed bound stamped as this tick's answer.
        // ScanBound's own catch would return the safe value for the call that threw, but every later call in
        // the same tick would have sailed past the stamp check and used the garbage.
        private static void Refresh(int cap)
        {
            uint now = Main.GameUpdateCount;
            if (now == _stamp)
                return;

            _active = false;

            var config = ModContent.GetInstance<ManyMoreMobsConfig>();
            if (config == null
                || !config.FastHostileProjectileScan
                || (ModHooksPresent && !config.FastScanEvenWithOtherMods))
            {
                _stamp = now;
                return;
            }

            NPC[] npc = Main.npc;
            int limit = Math.Min(cap, npc.Length);
            int highest = -1;
            for (int i = 0; i < limit; i++)
            {
                NPC n = npc[i];
                if (n != null && n.active && n.friendly)
                    highest = i;
            }

            // highest == -1 (no friendly NPC anywhere) yields 0, which skips the loop entirely — correct, and
            // the best case: an enemy projectile then has provably nothing to scan for.
            _bound = highest + 1;
            _active = true;
            _stamp = now;
        }

        /// <summary>
        /// Finds every loaded mod that overrides a hook able to force a hostile projectile onto a hostile NPC.
        /// <para/>
        /// Done with plain reflection over each mod's own types rather than by reading tModLoader's internal
        /// hook lists: those are private implementation detail and their shape has changed between versions,
        /// and a detector that silently stops detecting would re-enable an unsafe shortcut without a word. An
        /// override is identified by the method's <c>DeclaringType</c> no longer being the base class.
        /// </summary>
        internal static void DetectModHooks(Mod self)
        {
            ConflictingMods.Clear();

            // (base type, method name, parameter types) for every hook feeding CombinedHooks.CanHitNPCWithProj.
            var watched = new (Type baseType, string name, Type[] args)[]
            {
                (typeof(ModPlayer),        nameof(ModPlayer.CanHitNPCWithProj),        new[] { typeof(Projectile), typeof(NPC) }),
                (typeof(GlobalProjectile), nameof(GlobalProjectile.CanHitNPC),         new[] { typeof(Projectile), typeof(NPC) }),
                (typeof(ModProjectile),    nameof(ModProjectile.CanHitNPC),            new[] { typeof(NPC) }),
                (typeof(GlobalNPC),        nameof(GlobalNPC.CanBeHitByProjectile),     new[] { typeof(NPC), typeof(Projectile) }),
                (typeof(ModNPC),           nameof(ModNPC.CanBeHitByProjectile),        new[] { typeof(Projectile) }),
            };

            foreach (Mod mod in ModLoader.Mods)
            {
                if (mod == self)
                    continue;

                Type[] types;
                try
                {
                    types = mod.Code?.GetTypes() ?? Array.Empty<Type>();
                }
                catch (ReflectionTypeLoadException e)
                {
                    // A partially-loadable assembly still tells us about the types that DID load.
                    types = e.Types.Where(t => t != null).ToArray()!;
                }
                catch
                {
                    // Cannot inspect it, so cannot clear it. Assume the worst and keep vanilla's full scan.
                    ConflictingMods.Add($"{mod.Name} (not inspectable)");
                    continue;
                }

                if (OverridesAnyHook(types, watched))
                    ConflictingMods.Add(mod.Name);
            }
        }

        private static bool OverridesAnyHook(Type[] types, (Type baseType, string name, Type[] args)[] watched)
        {
            foreach (Type type in types)
            {
                if (type == null || type.IsAbstract)
                    continue;

                foreach ((Type baseType, string name, Type[] args) in watched)
                {
                    if (!baseType.IsAssignableFrom(type))
                        continue;

                    MethodInfo m;
                    try
                    {
                        m = type.GetMethod(name, BindingFlags.Public | BindingFlags.Instance, null, args, null);
                    }
                    catch
                    {
                        return true; // unreadable: assume it overrides
                    }

                    if (m != null && m.DeclaringType != baseType)
                        return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Records a reason the shortcut must not be trusted, which holds it back exactly as a conflicting
        /// mod would. Used when detection itself fails: an unreadable mod list is not evidence of safety.
        /// </summary>
        internal static void ForceUnsafe(string reason)
        {
            if (!ConflictingMods.Contains(reason))
                ConflictingMods.Add(reason);
        }

        /// <summary>One line for the load log, so the state is always visible without running a command.</summary>
        internal static string LoadSummary()
        {
            if (!ModHooksPresent)
                return "[MMM] Hostile-projectile hit scan shortcut: available (no other mod overrides the " +
                       "projectile-vs-NPC hit hooks).";

            return "[MMM] Hostile-projectile hit scan shortcut: held back, these mods override a " +
                   $"projectile-vs-NPC hit hook and could legitimately want the full scan — {string.Join(", ", ConflictingMods)}. " +
                   "Enable 'Fast Scan Even With Other Mods' to use it anyway.";
        }
    }
}
