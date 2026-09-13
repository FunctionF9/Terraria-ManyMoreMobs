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
            float rateMult = config.SpawnRateMultiplier;
            float maxMult = config.MaxSpawnMultiplier;
            string source = "General blanket";

            var biomeConfig = ModContent.GetInstance<BiomeSpawnConfig>();
            BiomeSpawnRates biome = biomeConfig?.ResolveFor(player);
            if (biome != null)
            {
                rateMult = biome.SpawnRateMultiplier;
                maxMult = biome.MaxSpawnMultiplier;
                source = "Biome: " + (biomeConfig.ActiveBiomeName(player) ?? "?");
            }

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
    }
}
