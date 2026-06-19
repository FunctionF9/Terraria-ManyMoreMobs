using System;
using System.ComponentModel;
using Terraria;
using Terraria.ModLoader.Config;

namespace ManyMoreMobs
{
    /// <summary>
    /// Per-biome spawn tuning. When <see cref="UseBiomeSpecificModifiers"/> is OFF the General config's
    /// blanket multipliers apply everywhere (default behaviour). When ON, each biome uses its own spawn-rate
    /// and max-spawn multipliers instead, resolved from the player's current zone (most-specific biome wins).
    /// These control spawn pressure / near-player limit only; the total NPC budget stays in the General config.
    /// </summary>
    public class BiomeSpawnConfig : ModConfig
    {
        public override ConfigScope Mode => ConfigScope.ServerSide;

        [Header("Mode")]
        [DefaultValue(false)]
        public bool UseBiomeSpecificModifiers { get; set; } = false;

        // Each biome: SpawnRateMultiplier (higher = more frequent) and MaxSpawnMultiplier (higher = more at
        // once). 1.0 = vanilla baseline for that biome. Grouped Standard -> Evil & Hallow -> Special &
        // Structures; within each group, roughly surface -> depths, and pre-hardmode before hardmode (the two
        // hardmode-gated entries, Hallow and the Lihzahrd Temple, are flagged in their labels).
        // (Field order here only sets the on-screen order; the active-biome PRIORITY is in ResolveFor below.)

        // Standard world biomes — all present from world-gen / pre-hardmode; surface down to the underworld.
        [Header("NormalBiomes")]
        public BiomeSpawnRates Surface { get; set; } = new();
        public BiomeSpawnRates Snow { get; set; } = new();
        public BiomeSpawnRates Desert { get; set; } = new();
        public BiomeSpawnRates Jungle { get; set; } = new();
        public BiomeSpawnRates Ocean { get; set; } = new();
        public BiomeSpawnRates Space { get; set; } = new();
        public BiomeSpawnRates Underground { get; set; } = new();
        public BiomeSpawnRates Caverns { get; set; } = new();
        public BiomeSpawnRates UndergroundDesert { get; set; } = new();
        public BiomeSpawnRates Underworld { get; set; } = new();

        // Evil & Hallow — Corruption / Crimson exist pre-hardmode; the Hallow only appears at hardmode.
        [Header("EvilBiomes")]
        public BiomeSpawnRates Corruption { get; set; } = new();
        public BiomeSpawnRates Crimson { get; set; } = new();
        public BiomeSpawnRates Hallow { get; set; } = new();

        // Special biomes & structures — Glowing Mushroom / Graveyard / Meteor / Dungeon are pre-hardmode; the
        // Lihzahrd Temple only spawns its enemies after Plantera.
        [Header("SpecialBiomes")]
        public BiomeSpawnRates GlowingMushroom { get; set; } = new();
        public BiomeSpawnRates Graveyard { get; set; } = new();
        public BiomeSpawnRates Meteor { get; set; } = new();
        public BiomeSpawnRates Dungeon { get; set; } = new();
        public BiomeSpawnRates LihzahrdTemple { get; set; } = new();

        /// <summary>
        /// The biome multipliers to use for this player, or null to fall back to the General blanket
        /// multipliers. Most-specific biome wins (structures &amp; special biomes before the depth layers).
        /// </summary>
        public BiomeSpawnRates ResolveFor(Player p)
        {
            if (!UseBiomeSpecificModifiers)
                return null;

            if (p.ZoneLihzhardTemple) return LihzahrdTemple;
            if (p.ZoneDungeon) return Dungeon;
            if (p.ZoneUnderworldHeight) return Underworld;
            if (p.ZoneMeteor) return Meteor;
            if (p.ZoneGlowshroom) return GlowingMushroom;
            if (p.ZoneGraveyard) return Graveyard;
            if (p.ZoneCorrupt) return Corruption;
            if (p.ZoneCrimson) return Crimson;
            if (p.ZoneHallow) return Hallow;
            if (p.ZoneJungle) return Jungle;
            if (p.ZoneSnow) return Snow;
            if (p.ZoneUndergroundDesert) return UndergroundDesert;
            if (p.ZoneDesert) return Desert;
            if (p.ZoneBeach) return Ocean;
            if (p.ZoneSkyHeight) return Space;
            if (p.ZoneRockLayerHeight) return Caverns;
            if (p.ZoneDirtLayerHeight) return Underground;
            return Surface;
        }

        /// <summary>
        /// The display name of the biome <see cref="ResolveFor"/> would pick for this player (same priority
        /// order), or null when biome-specific modifiers are off. Used by /debugnpc spawninfo for readout only.
        /// </summary>
        public string ActiveBiomeName(Player p)
        {
            if (!UseBiomeSpecificModifiers)
                return null;

            if (p.ZoneLihzhardTemple) return "Lihzahrd Temple";
            if (p.ZoneDungeon) return "Dungeon";
            if (p.ZoneUnderworldHeight) return "Underworld";
            if (p.ZoneMeteor) return "Meteor";
            if (p.ZoneGlowshroom) return "Glowing Mushroom";
            if (p.ZoneGraveyard) return "Graveyard";
            if (p.ZoneCorrupt) return "Corruption";
            if (p.ZoneCrimson) return "Crimson";
            if (p.ZoneHallow) return "Hallow";
            if (p.ZoneJungle) return "Jungle";
            if (p.ZoneSnow) return "Snow";
            if (p.ZoneUndergroundDesert) return "Underground Desert";
            if (p.ZoneDesert) return "Desert";
            if (p.ZoneBeach) return "Ocean";
            if (p.ZoneSkyHeight) return "Space";
            if (p.ZoneRockLayerHeight) return "Caverns";
            if (p.ZoneDirtLayerHeight) return "Underground";
            return "Surface";
        }
    }

    /// <summary>
    /// Spawn multipliers for one biome. Overrides Equals/GetHashCode because tModLoader requires nested
    /// config classes to be value-comparable for change detection and "reset to default" to work.
    /// </summary>
    public class BiomeSpawnRates
    {
        // Increment 1 to match the General sliders (snaps to whole numbers; 1.0 reachable). The far-left 0.1
        // is still selectable as a "near-off" value for a biome.
        [Range(0.1f, 200f)]
        [Increment(1f)]
        [DefaultValue(1f)]
        public float SpawnRateMultiplier { get; set; } = 1f;

        [Range(0.1f, 200f)]
        [Increment(1f)]
        [DefaultValue(1f)]
        public float MaxSpawnMultiplier { get; set; } = 1f;

        public override bool Equals(object obj)
            => obj is BiomeSpawnRates o
               && o.SpawnRateMultiplier == SpawnRateMultiplier
               && o.MaxSpawnMultiplier == MaxSpawnMultiplier;

        public override int GetHashCode() => HashCode.Combine(SpawnRateMultiplier, MaxSpawnMultiplier);
    }
}
