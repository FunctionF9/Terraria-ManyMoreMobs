using System.ComponentModel;
using Terraria.ModLoader.Config;

namespace ManyMoreMobs
{
    /// <summary>
    /// Central control for every item / effect that vanilla uses to change spawn rates. Each one is a single
    /// "strength" knob applied on top of the real vanilla math (IL-patched in <c>NPC.SpawnNPC</c>):
    /// <b>1 = vanilla effect, 0 = the item does nothing, above 1 = stronger</b>. So you can amplify, weaken or
    /// fully disable any of them from one page, instead of hunting through several config files. The actual
    /// scaling lives in <see cref="SpawnItemModifiers"/>.
    /// </summary>
    public class SpawnItemsConfig : ModConfig
    {
        public override ConfigScope Mode => ConfigScope.ServerSide;

        // ── Spawn-increasing items ───────────────────────────────────────────────────────────────────
        [Header("MoreSpawns")]
        [Range(0f, 5f)]
        [Increment(0.1f)]
        [DefaultValue(1f)]
        public float WaterCandle { get; set; } = 1f;

        [Range(0f, 5f)]
        [Increment(0.1f)]
        [DefaultValue(1f)]
        public float BattlePotion { get; set; } = 1f;

        // ── Spawn-reducing items ─────────────────────────────────────────────────────────────────────
        [Header("FewerSpawns")]
        [Range(0f, 5f)]
        [Increment(0.1f)]
        [DefaultValue(1f)]
        public float PeaceCandle { get; set; } = 1f;

        [Range(0f, 5f)]
        [Increment(0.1f)]
        [DefaultValue(1f)]
        public float CalmingPotion { get; set; } = 1f;

        [Range(0f, 5f)]
        [Increment(0.1f)]
        [DefaultValue(1f)]
        public float Sunflower { get; set; } = 1f;

        [Range(0f, 5f)]
        [Increment(0.1f)]
        [DefaultValue(1f)]
        public float Invisibility { get; set; } = 1f;

        [Range(0f, 5f)]
        [Increment(0.1f)]
        [DefaultValue(1f)]
        public float AnglerSet { get; set; } = 1f;

        [Range(0f, 5f)]
        [Increment(0.1f)]
        [DefaultValue(1f)]
        public float Fairy { get; set; } = 1f;

        // ── Special: Shadow Candle doesn't change the rate, it stops town NPCs from spawning. Here strength
        // 1 = full vanilla suppression, 0 = none, 0.5 = halve the town-NPC spawn weight. ──
        [Header("Special")]
        [Range(0f, 1f)]
        [Increment(0.05f)]
        [DefaultValue(1f)]
        public float ShadowCandle { get; set; } = 1f;
    }
}
