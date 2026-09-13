using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace ManyMoreMobs
{
    /// <summary>
    /// Keeps vanilla's rare "lottery" spawns at their intended frequency when the spawn rate is cranked up.
    /// <para/>
    /// A handful of spawns aren't picked from the weighted spawn pool at all — they're gated on a dice roll
    /// evaluated once per spawn ATTEMPT, inside <c>NPC.SpawnNPC</c>:
    /// <list type="bullet">
    /// <item>King Slime — <c>Main.rand.Next(300) == 0 &amp;&amp; !AnyNPCs(50)</c>, outer thirds of the world</item>
    /// <item>Prismatic Lacewing — <c>RollLuck(10) == 0 &amp;&amp; !AnyNPCs(661)</c>, post-Plantera jungle at night</item>
    /// </list>
    /// Because the mod makes spawn attempts happen far more often, every one of those rolls fires far more
    /// often too. At the default 200x this means King Slime respawning endlessly near the world edges, and a
    /// Lacewing back within seconds of the Empress dying — a real risk of re-summoning her mid-horde.
    /// <para/>
    /// The fix is to widen the odds by the same factor the attempt rate was widened by, so expected spawns per
    /// minute land back on vanilla regardless of how high the dial goes. It's self-scaling, so no per-type
    /// tuning is needed and it can't drift out of sync with the spawn config.
    /// <para/>
    /// This deliberately scales the ROLL rather than vetoing the spawn afterwards. Vanilla routes a natural
    /// King Slime through <c>SpawnOnPlayer</c>, which uses <c>EntitySource_BossSpawn</c> — the exact same
    /// source a Slime Crown produces — so at the <c>NewNPC</c> chokepoint the two are indistinguishable and a
    /// veto there would also eat summoned bosses. Patching the roll leaves every summoning item untouched.
    /// </summary>
    internal static class RareSpawnNormalizer
    {
        /// <summary>
        /// Scales a vanilla 1-in-<paramref name="vanillaOdds"/> rare-spawn roll to cancel out the spawn-rate
        /// multiplier. Called from IL-patched sites in <c>NPC.SpawnNPC</c>; must never throw.
        /// </summary>
        public static int ScaleOdds(int vanillaOdds)
        {
            try
            {
                if (vanillaOdds <= 1)
                    return vanillaOdds;

                var config = ModContent.GetInstance<ManyMoreMobsConfig>();
                if (config == null || !config.NormalizeRareSpawns)
                    return vanillaOdds;

                float amplification = AttemptAmplification();
                if (amplification <= 1f)
                    return vanillaOdds;

                // Cap the widened odds well below int.MaxValue so Main.rand.Next() can never be handed a
                // nonsensical bound, and so the spawn stays *possible* rather than mathematically dead.
                long scaled = (long)(vanillaOdds * amplification);
                return (int)System.Math.Min(scaled, 1_000_000L);
            }
            catch
            {
                return vanillaOdds; // a broken normaliser must never stop the game spawning
            }
        }

        /// <summary>
        /// How many times more often a spawn is attempted than vanilla would attempt it, derived from the
        /// spawn rate actually handed back to the engine this tick.
        /// <para/>
        /// Vanilla's <c>spawnRate</c> is an INTERVAL — lower means more frequent — so the amplification is
        /// <c>in / out</c>. Reading the live values rather than the config multiplier means biome overrides,
        /// the boss-active throttle and the Old One's Army suppression are all accounted for automatically:
        /// if something has slowed spawning back down, the rare odds narrow back with it.
        /// </summary>
        private static float AttemptAmplification()
        {
            // Stale capture (no spawn tick has run recently) — assume vanilla and leave the roll alone.
            if (SpawnRateMultiplier.CapturedTick < 0)
                return 1f;

            int inRate = SpawnRateMultiplier.InRate;
            int outRate = SpawnRateMultiplier.OutRate;
            if (inRate <= 0 || outRate <= 0)
                return 1f;

            return (float)inRate / outRate;
        }

        // ── Prismatic Lacewing: normalized, but deliberately NOT all the way down to vanilla ──────────────

        /// <summary>How long after the Empress spawns or dies the Lacewing drops to full vanilla rarity.</summary>
        private const long EmpressCooldownTicks = 60 * 60 * 2; // 2 real minutes

        private static long _empressEventTick = -1;

        /// <summary>Called when the Empress of Light spawns or is defeated; starts the Lacewing cooldown.</summary>
        public static void NoteEmpressEvent() => _empressEventTick = (long)Main.GameUpdateCount;

        private static bool EmpressCooldownActive()
        {
            if (_empressEventTick < 0)
                return false;
            // Negative elapsed means the tick counter restarted (new session) — treat the stamp as stale.
            long elapsed = (long)Main.GameUpdateCount - _empressEventTick;
            return elapsed >= 0 && elapsed < EmpressCooldownTicks;
        }

        /// <summary>
        /// Lacewing variant of <see cref="ScaleOdds"/>. Hunting one down is a deliberate activity rather than
        /// an ambush, so full vanilla rarity makes it a chore at high spawn rates — it keeps a configurable
        /// multiple of vanilla frequency instead.
        /// <para/>
        /// The exception is the window right after the Empress spawns or dies, where the un-normalized
        /// behaviour was genuinely disruptive: a Lacewing would reappear within seconds, risking an accidental
        /// re-summon mid-horde. For <see cref="EmpressCooldownTicks"/> after either event it falls back to
        /// full vanilla rarity, then returns to the boosted rate.
        /// </summary>
        public static int ScaleLacewingOdds(int vanillaOdds)
        {
            try
            {
                int normalized = ScaleOdds(vanillaOdds);
                if (normalized <= vanillaOdds || EmpressCooldownActive())
                    return normalized; // normalization off, or cooling down — vanilla rarity

                var config = ModContent.GetInstance<ManyMoreMobsConfig>();
                float boost = System.Math.Max(1f, config?.LacewingSpawnBoost ?? 1f);

                // Narrower odds = more frequent. Never tighter than vanilla's own 1-in-10.
                return System.Math.Max(vanillaOdds, (int)(normalized / boost));
            }
            catch
            {
                return vanillaOdds;
            }
        }

        /// <summary>Diagnostic line for <c>/mmm spawninfo</c>.</summary>
        public static string Describe()
        {
            var config = ModContent.GetInstance<ManyMoreMobsConfig>();
            bool on = config?.NormalizeRareSpawns ?? false;
            float amp = AttemptAmplification();
            string lacewingNote = EmpressCooldownActive()
                ? $" [vanilla rarity, Empress cooldown {(EmpressCooldownTicks - ((long)Main.GameUpdateCount - _empressEventTick)) / 60}s left]"
                : $" [boosted x{System.Math.Max(1f, config?.LacewingSpawnBoost ?? 1f):0.##}]";

            // The odds NUMBER tracks the live attempt rate, so it moves when something changes how often
            // spawning is attempted (boss throttle, Old One's Army, a biome override). That's the point: the
            // real-world frequency stays put. Spell out the resulting interval so the two can't be confused.
            int kingOdds = on ? ScaleOdds(300) : 300;
            int laceOdds = on ? ScaleLacewingOdds(10) : 10;
            int outRate = System.Math.Max(1, SpawnRateMultiplier.OutRate);

            return $"rare-spawn normalize: {(on ? "ON" : "OFF")}  attemptAmp={amp:F1}x (1 attempt/{outRate} ticks)\n" +
                   $"  KingSlime 1/300 -> 1/{kingOdds}  => ~1 per {(long)kingOdds * outRate / 60}s of eligible time\n" +
                   $"  Lacewing  1/10  -> 1/{laceOdds}  => ~1 per {(long)laceOdds * outRate / 60}s of eligible time{lacewingNote}";
        }
    }

    /// <summary>
    /// Watches for the Empress of Light appearing or dying, so <see cref="RareSpawnNormalizer"/> can quiet the
    /// Prismatic Lacewing for a while afterwards instead of offering an immediate re-summon.
    /// </summary>
    public class EmpressSpawnWatcher : GlobalNPC
    {
        public override void OnSpawn(NPC npc, Terraria.DataStructures.IEntitySource source)
        {
            if (npc.type == NPCID.HallowBoss)
                RareSpawnNormalizer.NoteEmpressEvent();
        }

        public override void OnKill(NPC npc)
        {
            if (npc.type == NPCID.HallowBoss)
                RareSpawnNormalizer.NoteEmpressEvent();
        }
    }
}
