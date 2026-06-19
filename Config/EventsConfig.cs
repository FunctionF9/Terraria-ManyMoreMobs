using System.ComponentModel;
using Terraria.ModLoader.Config;

namespace ManyMoreMobs
{
    /// <summary>
    /// Makes events last longer so they aren't trivially short under the mod's high spawn rates and raised
    /// cap (a vanilla-size invasion can end in seconds when the enemy cap fills instantly). The scaling is
    /// applied in <see cref="EventScaling"/> — invasions via a detour on <c>Main.StartInvasion</c>, Moons via
    /// IL on the wave-progress methods. (Old One's Army is intentionally not covered; it's a v0.5 milestone.)
    /// </summary>
    public class EventsConfig : ModConfig
    {
        public override ConfigScope Mode => ConfigScope.ServerSide;

        [Header("Length")]

        // Multiplies the total number of enemies to defeat in Goblin / Frost Legion / Pirate / Martian
        // invasions. 1 = vanilla.
        [Range(1f, 20f)]
        [Increment(1f)]
        [DefaultValue(3f)]
        public float InvasionSizeMultiplier { get; set; } = 3f;

        // Multiplies the points needed to advance each Pumpkin / Frost Moon WAVE. 1 = vanilla. Higher = more
        // time (and more enemies) per wave before climbing to the next.
        [Range(1f, 20f)]
        [Increment(1f)]
        [DefaultValue(3f)]
        public float MoonWaveLengthMultiplier { get; set; } = 3f;

        // ── Old One's Army ──────────────────────────────────────────────────────────────────────────
        // How much of the NORMAL (non-event) spawn rate is kept while Old One's Army is running, so the
        // Etherian enemies stand out. 0 = no normal spawns, 1 = full normal spawns. Does not affect the event's
        // own spawns. (The event's enemies come from the portals, not this.)
        [Header("OldOnesArmy")]
        [Range(0f, 1f)]
        [Increment(0.05f)]
        [DefaultValue(0.1f)]
        public float OldOnesArmyNormalSpawnFactor { get; set; } = 0.1f;

        // How much faster the portals spawn Etherian enemies. 1 = vanilla; higher = the portals spit out
        // enemies more frequently (vanilla trickles them in, which drags the waves out under this mod).
        [Range(1f, 20f)]
        [Increment(0.5f)]
        [DefaultValue(5f)]
        public float OldOnesArmySpawnRateMultiplier { get; set; } = 5f;

        // Multiplies how many kills each Old One's Army wave needs (like the invasion / moon length knobs).
        // 1 = vanilla.
        [Range(1f, 20f)]
        [Increment(1f)]
        [DefaultValue(5f)]
        public float OldOnesArmyWaveLengthMultiplier { get; set; } = 5f;
    }
}
