using System;
using System.Collections.Generic;
using System.Linq;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace ManyMoreMobs
{
    /// <summary>
    /// Notices when the game silently deletes a projectile because its update threw.
    /// <para/>
    /// Vanilla runs every projectile update as
    /// <code>try { projectile[n].Update(n); } catch { projectile[n] = new Projectile(); }</code>
    /// (Main.cs:17820, under <c>Main.ignoreErrors</c>, which defaults on and is never turned off). Whatever
    /// threw — another mod's hit hook handed an NPC from a slot its own arrays never expected, a short per-slot
    /// array, anything — the only visible result is that the swing or shot does nothing. No damage, no number,
    /// no message. That is word for word the "my weapon can't hit that enemy" report, and it is the one version
    /// of it a tracker built on hit hooks can never see, because the hit never happens.
    /// <para/>
    /// The catch leaves one exact fingerprint: it puts a NEW object in the slot. Every legitimate way a
    /// projectile ends (Kill, timeout, reuse by NewProjectile) keeps the same object and clears <c>active</c>.
    /// So we remember which object sat in each active slot before the pass and compare after it.
    /// <para/>
    /// Always on, not behind Debug Mode: the people who hit this are players who will never turn a debug setting
    /// on, and the value is that <c>/mmm info</c> can say so in a screenshot. It costs a thousand reference
    /// copies and compares per tick. This is a diagnostic, not an optimisation — do not narrow it into something
    /// that only runs on demand, or it will be off in exactly the games where it matters.
    /// </summary>
    public class ProjectileErrorWatch : ModSystem
    {
        // Sized from Main.projectile itself on first use rather than from a constant, so it can never disagree
        // with the array it shadows.
        private static Projectile[] Before = Array.Empty<Projectile>();
        private static readonly Dictionary<int, int> DestroyedByType = new();

        /// <summary>Projectiles deleted by an error since the world loaded.</summary>
        internal static int Total { get; private set; }

        /// <summary>How many of those belonged to the local player — the ones a player actually notices.</summary>
        internal static int OwnedByLocalPlayer { get; private set; }

        public override void OnWorldLoad() => Reset();

        public override void OnWorldUnload() => Reset();

        private static void Reset()
        {
            Total = 0;
            OwnedByLocalPlayer = 0;
            DestroyedByType.Clear();
            Array.Clear(Before);
        }

        public override void PreUpdateProjectiles()
        {
            Projectile[] live = Main.projectile;
            if (Before.Length != live.Length)
                Before = new Projectile[live.Length];
            int n = live.Length;
            for (int i = 0; i < n; i++)
            {
                Projectile p = live[i];
                Before[i] = p != null && p.active ? p : null;
            }
        }

        public override void PostUpdateProjectiles()
        {
            Projectile[] live = Main.projectile;
            int n = Math.Min(Before.Length, live.Length);
            for (int i = 0; i < n; i++)
            {
                Projectile was = Before[i];
                if (was == null)
                    continue;
                Before[i] = null;

                if (!ReferenceEquals(live[i], was))
                    Record(was);
            }
        }

        private static void Record(Projectile lost)
        {
            Total++;
            bool local = Main.netMode != NetmodeID.Server && lost.owner == Main.myPlayer;
            if (local)
                OwnedByLocalPlayer++;

            DestroyedByType.TryGetValue(lost.type, out int seen);
            DestroyedByType[lost.type] = seen + 1;
            if (seen > 0)
                return; // one log line per type per world; the count keeps climbing

            MmmLog.Warn($"A projectile update threw and the game deleted the projectile without a word: " +
                        $"{ProjectileName(lost.type)} (type {lost.type}, owner {lost.owner}{(local ? ", yours" : "")}). " +
                        "Nothing on screen shows this beyond the shot or swing doing nothing. tModLoader normally " +
                        "records the exception itself in client.log as 'Silently Caught Exception', and its stack " +
                        "trace names the mod that threw.");
        }

        /// <summary>One line for <c>/mmm info</c>; empty when nothing has been lost.</summary>
        internal static string Summary()
        {
            if (Total == 0)
                return "";

            string types = string.Join(", ", DestroyedByType
                .OrderByDescending(kv => kv.Value)
                .Take(4)
                .Select(kv => $"{ProjectileName(kv.Key)} x{kv.Value}"));
            return $"{Total} projectile(s) deleted by an error mid-update ({OwnedByLocalPlayer} yours): {types}";
        }

        // Not "Name": ModSystem inherits ModType.Name, and hiding it would make this.Name mean two things.
        private static string ProjectileName(int type)
        {
            try { return Lang.GetProjectileName(type)?.Value ?? $"#{type}"; }
            catch { return $"#{type}"; }
        }
    }
}
