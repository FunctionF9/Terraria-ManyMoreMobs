using System;
using System.ComponentModel;
using Newtonsoft.Json;
using Terraria.ModLoader.Config;

namespace ManyMoreMobs
{
    /// <summary>
    /// The General / baseline config page: the ultimate limits (cap raise, per-category budget), the
    /// blanket spawn multipliers (used when biome-specific modifiers are off — see <see cref="BiomeSpawnConfig"/>),
    /// boss throttling, combat, and debug. Spawn-item tuning lives in <see cref="SpawnItemsConfig"/>.
    /// </summary>
    public class ManyMoreMobsConfig : ModConfig
    {
        public override ConfigScope Mode => ConfigScope.ServerSide;

        // The config is ordered as a top-down setup flow: first WHICH mode (the world's NPC capacity), then
        // HOW that capacity is split per category, then HOW FAST it fills, then debug. (Combat assists live on
        // their own Horde Combat page so all combat tuning is in one place.)

        // ── 1. Mode: how big is the world's NPC budget? ─────────────────────────────────────────────
        // Default = vanilla 200 limit, NO engine patching (max safety/compatibility; the spawn/category/event
        // tuning still applies within 200). Expanded = raise the limit to MaxNPCTotal (~750) for the full
        // horde experience. SINGLE-PLAYER ONLY for now. Reload-required (changing it forces a reload).
        [Header("Mode")]
        [ReloadRequired]
        [DrawTicks]
        [DefaultValue(NpcCapMode.Expanded)]
        public NpcCapMode CapMode { get; set; } = NpcCapMode.Expanded;

        // The total NPC budget in Expanded mode. IGNORED in Default (clamped to the vanilla 200).
        [ReloadRequired]
        [Range(200, 1500)]
        [DefaultValue(750)]
        public int MaxNPCTotal { get; set; } = 750;

        // ── 2. Category slots: how the budget is split (Town / Boss / Critter / Enemy) ──────────────
        // EXPANDED: a hard reservation — the four should sum to the total (defaults 100/100/25/525 = 750) so
        // each category has guaranteed space; Town+Boss live in the native 0-199 zone, Critter+Enemy above.
        // DEFAULT: these act as CEILINGS over the shared 200 — Town/Boss always spawn (low-priority NPCs are
        // despawned to fit them), Critter/Enemy are limited to their value. See GetEffectiveCaps.
        [Header("CategoryCaps")]
        [Range(1, 100)]
        [DefaultValue(100)]
        public int TownNPCCap { get; set; } = 100;

        // 100 still fits a full Destroyer (head + ~85 body segments all count as Boss). Both ceilings top out
        // at 100 on purpose: Town + Boss can then never oversubscribe the native 0-199 zone they share.
        [Range(1, 100)]
        [DefaultValue(100)]
        public int BossCap { get; set; } = 100;

        [Range(1, 500)]
        [DefaultValue(25)]
        public int CritterCap { get; set; } = 25;

        // Max is the size of the expanded zone at the largest MaxNPCTotal (1500 - the 200 low zone = 1300),
        // so a player who cranks MaxNPCTotal can actually fill it with enemies. Default 525 suits the 750 total.
        [Range(1, 1300)]
        [DefaultValue(525)]
        public int EnemyCap { get; set; } = 525;

        // ── 3. Spawn rate: how aggressively the budget fills ───────────────────────────────────────
        // Blanket multipliers, applied everywhere UNLESS the Biome Spawn config's per-biome modifiers are on.
        // Applied on top of vanilla's own modifiers, so Water Candle / Battle Potion / biomes still stack.
        // Live (no reload). RATE = how often a spawn is attempted; MAX = how many can be active near you.
        [Header("SpawnRate")]
        [Range(1f, 300f)]
        [Increment(1f)]
        [DefaultValue(200f)]
        public float SpawnRateMultiplier { get; set; } = 200f;

        // Near-player active-spawn limit (vanilla base × this). Vanilla bases are tiny (~5), so a big value is
        // needed to approach the Enemy cap. If you plateau below the cap, the spawn AREA is the limiter, not this.
        [Range(1f, 300f)]
        [Increment(1f)]
        [DefaultValue(300f)]
        public float MaxSpawnMultiplier { get; set; } = 300f;

        // Throttle normal spawns while a boss is alive so the fight isn't buried in trash. The strength of the
        // throttle is BossActiveSpawnFactor below (which only matters while this is ON).
        [DefaultValue(true)]
        public bool ReduceSpawnsWhenBossActive { get; set; } = true;

        // Fraction of spawn pressure kept while a boss is alive: 0 = none, 1 = no throttle. Only used when
        // 'Reduce Spawns During Boss' above is ON.
        [Range(0f, 1f)]
        [Increment(0.05f)]
        [DefaultValue(0.25f)]
        public float BossActiveSpawnFactor { get; set; } = 0.25f;

        // Keep rare "lottery" spawns at their vanilla frequency instead of letting the spawn-rate multiplier
        // inflate them. Vanilla gates these on a per-ATTEMPT dice roll (King Slime 1-in-300, Prismatic Lacewing
        // 1-in-10-with-luck), so multiplying how often spawns are attempted multiplies them too — at 200x, a
        // Lacewing reappears almost the instant the Empress dies. We widen the odds by the same factor the
        // spawn rate was widened by, so the expected rate matches vanilla no matter how high the dial goes.
        // Only affects the natural roll; summoning items are untouched. Live (no reload).
        [DefaultValue(true)]
        public bool NormalizeRareSpawns { get; set; } = true;

        // Exception to the above, for the Prismatic Lacewing only. Finding one is a deliberate hunt rather than
        // an ambush, so full vanilla rarity makes it a chore at high spawn rates — this keeps it this many times
        // more common than vanilla. It still drops to full vanilla rarity for two minutes after the Empress
        // spawns or dies, so you don't immediately re-summon her. No effect if Normalize Rare Spawns is OFF.
        [Range(1f, 50f)]
        [Increment(1f)]
        [DefaultValue(5f)]
        public float LacewingSpawnBoost { get; set; } = 5f;

        // ── 4. Debugging & experimental ────────────────────────────────────────────────────────────
        // Auto-dumps state to ManyMoreMobs-state.log on world load and before each save. /debugnpc commands
        // always log regardless. Leave off for normal play.
        [Header("DebugAndExperimental")]
        [DefaultValue(false)]
        public bool DebugMode { get; set; } = false;

        // Widens the hardcoded 200-slot bounds checks in Terraria's packet handlers so messages aimed at
        // expanded-zone NPCs aren't silently discarded (critter catching, NPC i-frame sync, buff removal, the
        // initial NPC handoff on join). Multiplayer is still NOT supported — these are best-effort fixes for
        // reported symptoms and are untested by the author. Lives in MMMultiplayer/, checked live (no reload),
        // and does nothing at all in single-player. Off = vanilla packet handling.
        [DefaultValue(true)]
        public bool ExperimentalMultiplayerFixes { get; set; } = true;

        /// <summary>
        /// The total NPC budget actually available right now. Uses <see cref="MaxNpcCapRaise.AppliedCap"/>
        /// (the capacity we actually applied: 200 vanilla, or the raised total) instead of reading
        /// <c>Main.maxNPCs</c> directly — the JIT can bake the old <c>readonly</c> value (200) into compiled
        /// reads, which would silently starve the Enemy/Critter budgets.
        /// <para/>
        /// <c>[JsonIgnore]</c> keeps this computed property out of the config file and the config UI.
        /// </summary>
        [JsonIgnore]
        public int EffectiveTotal => Math.Min(MaxNPCTotal, MaxNpcCapRaise.AppliedCap);

        /// <summary>
        /// The per-category caps to actually enforce against the live budget.
        /// <para/>
        /// <b>Default mode</b> — the caps are independent CEILINGS, not a reservation. The vanilla 200 array
        /// plus despawn-to-make-room (<see cref="EntityEvictor"/>) handle the real slot-sharing: Town/Boss are
        /// guaranteed a slot up to their ceiling by evicting low-priority NPCs, while Enemy is additionally
        /// bounded by the array itself. So we use the configured values as-is — no proportional scaling (which
        /// would needlessly starve Town to ~10). Town and Critter ceilings still bite (housing ceiling, and
        /// the "town suppresses enemies so critters flood" owl cap); Boss is not enforced here at all.
        /// <para/>
        /// <b>Expanded mode</b> — hard reservation against the raised budget. If the configured caps fit they
        /// are used as-is (the 750 defaults sum to exactly 750, giving each category guaranteed zoned space);
        /// if they oversubscribe, all four scale down proportionally to fit while preserving the ratios.
        /// </summary>
        public (int town, int boss, int critter, int enemy) GetEffectiveCaps()
        {
            int town = TownNPCCap, boss = BossCap, critter = CritterCap, enemy = EnemyCap;

            if (CapMode == NpcCapMode.Default)
                return (town, boss, critter, enemy);

            int total = EffectiveTotal;
            int sum = town + boss + critter + enemy;
            if (sum <= total || sum == 0)
                return (town, boss, critter, enemy);

            float scale = (float)total / sum;
            return (
                Math.Max(1, (int)(town * scale)),
                Math.Max(1, (int)(boss * scale)),
                Math.Max(1, (int)(critter * scale)),
                Math.Max(1, (int)(enemy * scale)));
        }
    }
}
