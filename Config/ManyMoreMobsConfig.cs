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

        /// <summary>
        /// Hard ceiling on <see cref="MaxNPCTotal"/>, and the single source of truth for it.
        /// <para/>
        /// <b>Must stay a const, and must stay the only place this number lives.</b> The early cap raise reads
        /// the config file by hand (before tModLoader has parsed it) and re-clamps the value itself, so the
        /// bound exists in two places by necessity. When they were two literals they could silently disagree,
        /// and the failure mode is nasty: the config would happily accept a number the engine then quietly
        /// clamped to something lower, so a test would run at a cap nobody chose and the log would look fine.
        /// </summary>
        /// <para/>
        /// Held at 1500 — the range the mod has actually been played at. It was briefly raised to 3000 to
        /// stress-test the projectile work in 0.7.8.1 and put back afterwards; raising it again is a decision
        /// to make once the current caps have had more time in real playthroughs, not a side effect of a
        /// performance pass.
        public const int MaxNPCTotalCeiling = 1500;

        /// <summary>Ceiling on <see cref="EnemyCap"/>: everything above the native 0-199 zone.</summary>
        public const int EnemyCapCeiling = MaxNPCTotalCeiling - 200;

        // The total NPC budget in Expanded mode. IGNORED in Default (clamped to the vanilla 200).
        // The upper bound is deliberately far above anything playable — it exists for stress testing, and a
        // machine will run out of frame budget long before it runs out of slots.
        [ReloadRequired]
        [Range(200, MaxNPCTotalCeiling)]
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

        // Max is the size of the expanded zone at the largest MaxNPCTotal (the ceiling minus the 200 low zone),
        // so a player who cranks MaxNPCTotal can actually fill it with enemies. Default 525 suits the 750 total.
        [Range(1, EnemyCapCeiling)]
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
        //
        // 300x against a vanilla base of ~5 allows roughly 1500 active, which covers the 1300 Enemy ceiling
        // with room to spare — so this dial is not the binding constraint at any setting the mod allows. It
        // was raised to 1000 while MaxNPCTotalCeiling was temporarily at 3000, where 300x no longer reached
        // the cap and a stress test would have plateaued below the number it was trying to hit; both went
        // back together. Note the spawn RATE dial is not raised to match, and does not need to be — the
        // engine attempts spawns per frame, so past a few hundred it is already attempting one every frame
        // and a larger number buys nothing.
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

        // ── 4. Performance ─────────────────────────────────────────────────────────────────────────
        // Enemy projectiles scan the whole NPC array every tick looking for something to hit, even though the
        // only thing they can actually damage is a town NPC. At a raised cap that one loop measured as 95% of
        // all projectile time — it is what turns a screen full of harpies or spike slimes into a slideshow.
        // On, an enemy projectile only scans as far as the highest town NPC instead of the whole array.
        // Player projectiles are untouched; they still see every enemy. Live (no reload).
        [Header("Performance")]
        [DefaultValue(true)]
        public bool FastHostileProjectileScan { get; set; } = true;

        // The shortcut is only IDENTICAL to vanilla when no other mod overrides the projectile-vs-NPC hit
        // hooks, because a mod can use those to let enemy projectiles damage other enemies ("friendly fire"
        // mods). When one does, the shortcut turns itself off and the load log names the mod. Turn this on to
        // use it anyway: you get the frame rate back, and any such cross-enemy damage stops working. Off is
        // the correct-by-default choice; this is the deliberate override. Live (no reload).
        [DefaultValue(false)]
        public bool FastScanEvenWithOtherMods { get; set; } = false;

        // ── 5. Debugging & experimental ────────────────────────────────────────────────────────────
        // Auto-dumps state to ManyMoreMobs-state.log on world load and before each save. /mmmdebug commands
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

        // Raises Terraria's NPC limit before ANY other mod loads, so mods that size their own NPC arrays and
        // loops from that number build themselves around the real cap instead of around 200. This is what makes
        // big content mods (Calamity and friends) work: without it their arrays stay 200 long while NPCs exist
        // above slot 199, and the resulting crash lands inside the spawn and NPC-update loops — which reads, in
        // game, as enemies not spawning at all. Needs a reload. Turn it off to get the old behaviour back if a
        // mod dislikes the larger arrays.
        [ReloadRequired]
        [DefaultValue(true)]
        public bool RaiseCapBeforeOtherMods { get; set; } = true;

        // Fallback for the above: rewrites other mods' compiled reads of the NPC limit to the raised cap. Only
        // worth turning on if a mod still misbehaves with the early raise on — the early raise fixes the same
        // problem at its source, and this pass costs several seconds of load time with a large mod list.
        // Cannot help a mod that hardcodes 200 instead of reading the field. Needs a reload.
        [ReloadRequired]
        [DefaultValue(false)]
        public bool PatchOtherModsNpcLimit { get; set; } = false;

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
