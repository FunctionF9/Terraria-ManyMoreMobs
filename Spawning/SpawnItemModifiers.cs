using System;
using Terraria.ModLoader;

namespace ManyMoreMobs
{
    /// <summary>
    /// Computes the effective spawn multipliers for each spawn-affecting item/effect, scaled by the user's
    /// per-item "strength" in <see cref="SpawnItemsConfig"/>. The IL patch in <c>EngineILPatcher</c> replaces
    /// each hardcoded vanilla constant in <c>NPC.SpawnNPC</c> with a call to the matching method here.
    /// <para/>
    /// The strength model keeps the config intuitive: rather than exposing the raw vanilla numbers (0.75, 1.65,
    /// …), each item has a single knob where <b>1 = vanilla, 0 = no effect (disabled), &gt;1 = stronger</b>. We
    /// scale the vanilla multiplier's <i>deviation from neutral (1.0)</i>, so both spawn boosts (vanilla &gt; 1)
    /// and reductions (vanilla &lt; 1) get stronger/weaker symmetrically:
    /// <c>effective = 1 + (vanilla - 1) * strength</c>.
    /// </summary>
    public static class SpawnItemModifiers
    {
        private static SpawnItemsConfig C => ModContent.GetInstance<SpawnItemsConfig>();

        // Scale a vanilla multiplier by strength. strength <= 0 => 1.0 (the item has no effect at all).
        private static float Scale(float vanilla, float strength)
            => strength <= 0f ? 1f : 1f + (vanilla - 1f) * strength;

        private static double ScaleD(double vanilla, float strength)
            => strength <= 0f ? 1.0 : 1.0 + (vanilla - 1.0) * strength;

        // ── Items that REDUCE spawns (vanilla raises spawnRate / lowers maxSpawns) ──
        public static float InvisRate()     => Scale(1.2f, C?.Invisibility ?? 1f);
        public static float InvisMax()      => Scale(0.8f, C?.Invisibility ?? 1f);

        public static float CalmingRate()   => Scale(1.65f, C?.CalmingPotion ?? 1f);
        public static float CalmingMax()    => Scale(0.6f, C?.CalmingPotion ?? 1f);

        public static float SunflowerRate() => Scale(1.2f, C?.Sunflower ?? 1f);
        public static float SunflowerMax()  => Scale(0.8f, C?.Sunflower ?? 1f);

        public static float AnglerRate()    => Scale(1.3f, C?.AnglerSet ?? 1f);
        public static float AnglerMax()     => Scale(0.7f, C?.AnglerSet ?? 1f);

        public static double PeaceRate()    => ScaleD(1.3, C?.PeaceCandle ?? 1f);
        public static float PeaceMax()      => Scale(0.7f, C?.PeaceCandle ?? 1f);

        public static float FairyRate()     => Scale(1.2f, C?.Fairy ?? 1f);
        public static float FairyMax()      => Scale(0.8f, C?.Fairy ?? 1f);

        // ── Items that INCREASE spawns ──
        public static double BattleRate()   => ScaleD(0.5, C?.BattlePotion ?? 1f);
        public static float BattleMax()     => Scale(2f, C?.BattlePotion ?? 1f);

        public static double WaterRate()    => ScaleD(0.75, C?.WaterCandle ?? 1f);
        public static float WaterMax()      => Scale(1.5f, C?.WaterCandle ?? 1f);
        public static double WaterDeepRate()=> ScaleD(0.5, C?.WaterCandle ?? 1f);

        // ── Shadow Candle: vanilla zeroes the town-NPC spawn weight. We multiply it by this factor instead,
        // so strength 1 => 0 (full vanilla suppression), strength 0 => 1 (no suppression), 0.5 => half. ──
        public static float ShadowFactor()  => Math.Max(0f, Scale(0f, C?.ShadowCandle ?? 1f));
    }
}
