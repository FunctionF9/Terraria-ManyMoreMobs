using System;
using Terraria;
using Terraria.GameContent.Events;
using Terraria.ModLoader;

namespace ManyMoreMobs
{
    /// <summary>
    /// Tunes how aggressively the area around each player tries to fill with NPCs. The precise
    /// per-category split is enforced separately by <see cref="NewNpcGate"/>; here we just raise the
    /// spawn frequency and the near-player active budget so the spawn system actually attempts to reach
    /// the caps.
    /// </summary>
    public class SpawnRateMultiplier : GlobalNPC
    {
        // ── Live capture of the local player's last spawn computation, for /mmm spawninfo & dump. ──
        // EditSpawnRate runs constantly during natural spawning, so these reflect "right now" in SP.
        public static long CapturedTick = -1;
        public static int InRate, InMax, OutRate, OutMax;
        public static float UsedRateMult = 1f, UsedMaxMult = 1f;
        public static string UsedSource = "(none)";
        public static bool BossThrottled;

        public override void EditSpawnRate(Player player, ref int spawnRate, ref int maxSpawns)
        {
            var config = ModContent.GetInstance<ManyMoreMobsConfig>();
            if (config == null)
                return;

            bool capture = player.whoAmI == Main.myPlayer;
            int inRate = spawnRate, inMax = maxSpawns;

            // Pick the spawn multipliers: per-biome if enabled (and a biome resolved), else the General
            // blanket values. Both come in already modified by vanilla (biome, events, candle, battle), and
            // we MULTIPLY rather than overwrite so those modifiers are preserved and amplified:
            //   lower spawnRate = more frequent spawns; higher maxSpawns = more active at once.
            (float rateMult, float maxMult, string source) = ResolveMultipliers(player, config);

            spawnRate = Math.Max(1, (int)(spawnRate / Math.Max(0.1f, rateMult)));
            maxSpawns = Math.Max(1, (int)(maxSpawns * Math.Max(0.1f, maxMult)));

            // Throttle trash spawns while a boss is alive (boss heads sit in the low slots, so the vanilla
            // boss flag covers them). FLAT dial: the factor is the fraction of the CURRENT spawn pressure to
            // keep during a boss — 0 = no spawns at all, 1 = keep the current rate (no throttle). Applied to
            // the final (already-multiplied) values, so it always bites regardless of how high the multipliers
            // are set (the old multiply-on-multiply left e.g. 0.25 x 200x = still a swarm).
            bool throttled = false;
            if (config.ReduceSpawnsWhenBossActive && Main.CurrentFrameFlags.AnyActiveBossNPC)
            {
                float factor = Math.Clamp(config.BossActiveSpawnFactor, 0f, 1f);
                maxSpawns = Math.Max(0, (int)(maxSpawns * factor)); // 0 => no new spawns (count < 0 never true)
                if (factor > 0f)
                    spawnRate = Math.Max(1, (int)(spawnRate / factor)); // lower factor => longer interval => fewer
                throttled = true;
            }

            // Suppress NORMAL spawns while Old One's Army is active so the Etherian enemies stand out (the
            // event's own enemies come from the portals, not the normal spawn system, so they're unaffected).
            if (DD2Event.Ongoing)
            {
                var eventsCfg = ModContent.GetInstance<EventsConfig>();
                float ooaFactor = Math.Clamp(eventsCfg?.OldOnesArmyNormalSpawnFactor ?? 0.1f, 0f, 1f);
                maxSpawns = Math.Max(0, (int)(maxSpawns * ooaFactor));
                if (ooaFactor > 0f)
                    spawnRate = Math.Max(1, (int)(spawnRate / ooaFactor));
                throttled = true;
            }

            if (capture)
            {
                CapturedTick = (long)Main.GameUpdateCount;
                InRate = inRate; InMax = inMax;
                OutRate = spawnRate; OutMax = maxSpawns;
                UsedRateMult = rateMult; UsedMaxMult = maxMult;
                UsedSource = source;
                BossThrottled = throttled;
            }
        }

        /// <summary>
        /// The spawn multipliers in force for this player: the per-biome overrides when one resolves, else the
        /// General blanket dials. Shared so <see cref="EditSpawnRange"/> reads exactly what
        /// <see cref="EditSpawnRate"/> applied, including on a server, where nothing is captured.
        /// </summary>
        private static (float rate, float max, string source) ResolveMultipliers(Player player, ManyMoreMobsConfig config)
        {
            var biomeConfig = ModContent.GetInstance<BiomeSpawnConfig>();
            BiomeSpawnRates biome = biomeConfig?.ResolveFor(player);
            if (biome != null)
                return (biome.SpawnRateMultiplier, biome.MaxSpawnMultiplier,
                        "Biome: " + (biomeConfig.ActiveBiomeName(player) ?? "?"));

            return (config.SpawnRateMultiplier, config.MaxSpawnMultiplier, "General blanket");
        }

        // How far the ring may grow, as a multiple of vanilla's width.
        private const float MaxWidenFactor = 2.5f;

        /// <summary>
        /// Widens the ring natural spawns are placed in, in proportion to the raised spawn rate.
        /// <para/>
        /// Vanilla spawns into a ring between <c>safeRangeX</c> (0.52 screens, just off-screen) and
        /// <c>spawnRangeX</c> (0.7 screens): about 22 tile columns each side. It picks one column at random and
        /// drops the NPC on the ground there, which is fine at a spawn every few seconds. Multiply the rate and
        /// that same narrow band takes a spawn nearly every frame, so the same column repeats within about a
        /// second and the two NPCs land on the identical tile — the "five enemies standing as one" the horde is
        /// supposed to avoid. A wider ring gives the roll more columns to land on.
        /// <para/>
        /// <b>Square root, not the multiplier.</b> The ring cannot grow 200-fold; it would sit far outside the
        /// distance an NPC stays loaded at and every spawn would despawn on its first update. The root turns the
        /// default 200x into the cap while leaving a modest 4x setting near vanilla.
        /// <para/>
        /// <b>Horizontal only, deliberately.</b> The vertical range decides how far below you a spawn may land,
        /// and depth decides WHICH enemies the game picks — widening it would spawn cave enemies while you stand
        /// on the surface, which is a content change, not a spacing one.
        /// <para/>
        /// <c>safeRangeX</c> is left alone: it is what keeps spawns off-screen.
        /// <para/>
        /// <b>Off by default, and it should stay that way.</b> Horizontal distance is not the neutral axis it
        /// looks like: <c>NPC.SpawnNPC</c> hands the chosen tile to the pool as <c>NPCSpawnInfo.SpawnTileType</c>,
        /// and vanilla keys whole enemy lists off it (sand, snow, jungle grass). So a wide ring reaches the
        /// neighbouring biome's ground and spawns its enemies while the player stands in their own — desert
        /// enemies in a forest, seen in dev testing. Spawning further out also delays every arrival. Both are
        /// judgement calls about feel rather than faults, which is why this is a switch and not a fix.
        /// </summary>
        public override void EditSpawnRange(Player player, ref int spawnRangeX, ref int spawnRangeY,
                                            ref int safeRangeX, ref int safeRangeY)
        {
            var config = ModContent.GetInstance<ManyMoreMobsConfig>();
            if (config == null || !config.WidenSpawnArea)
                return;

            float rateMult = ResolveMultipliers(player, config).rate;
            if (rateMult <= 1f)
                return;

            float factor = Math.Clamp(MathF.Sqrt(rateMult), 1f, MaxWidenFactor);

            // Never past the distance the game keeps an NPC loaded at (activeRangeX is sWidth * 2.1), or a spawn
            // would be culled on its first update. Four fifths of it leaves room for the player to move away.
            int maxTilesX = (int)(NPC.sWidth * 2.1f / 16f * 0.8f);
            spawnRangeX = Math.Min((int)(spawnRangeX * factor), maxTilesX);
        }
    }
}
