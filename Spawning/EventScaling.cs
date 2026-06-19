using System;
using Terraria;
using Terraria.GameContent.Events;
using Terraria.ModLoader;

namespace ManyMoreMobs
{
    /// <summary>
    /// Runtime helpers for <see cref="EventsConfig"/>. <see cref="ScaleInvasion"/> is called from a detour on
    /// <c>Main.StartInvasion</c> (after the size is set); <see cref="ScaleMoonWavePoints"/> is called from IL
    /// inserted right after each <c>MoonEventRequiredPointsPerWaveLookup</c> read in the Moon wave-progress
    /// methods, so both the advance threshold and the progress-bar max scale together.
    /// </summary>
    public static class EventScaling
    {
        private static EventsConfig C => ModContent.GetInstance<EventsConfig>();

        /// <summary>Scale the points required to advance a Pumpkin/Frost Moon wave.</summary>
        public static int ScaleMoonWavePoints(int basePoints)
        {
            float m = C?.MoonWaveLengthMultiplier ?? 1f;
            return m <= 1f ? basePoints : (int)(basePoints * m);
        }

        /// <summary>Multiply a freshly-started invasion's size (total enemies to defeat) and its progress max.</summary>
        public static void ScaleInvasion()
        {
            float m = C?.InvasionSizeMultiplier ?? 1f;
            if (m <= 1f)
                return;

            Main.invasionSize = (int)(Main.invasionSize * m);
            Main.invasionSizeStart = Main.invasionSize;
            Main.invasionProgressMax = Main.invasionSizeStart;
        }

        /// <summary>Scale the kills required to advance an Old One's Army wave (called from a GetInvasionStatus detour).</summary>
        public static int ScaleOldOnesArmyWaveKills(int required)
        {
            float m = C?.OldOnesArmyWaveLengthMultiplier ?? 1f;
            return m <= 1f ? required : (int)(required * m);
        }

        /// <summary>
        /// Speed up the Old One's Army portals by lowering DD2Event.LaneSpawnRate (frames between spawns).
        /// Called right after the event sets it per-wave (Difficulty_X_GetEnemiesForWave), so the per-wave
        /// value is preserved and just divided by the multiplier. Floored so it can't go silly-fast.
        /// </summary>
        public static void RampLaneSpawnRate()
        {
            float m = C?.OldOnesArmySpawnRateMultiplier ?? 1f;
            if (m <= 1f)
                return;
            DD2Event.LaneSpawnRate = Math.Max(8, (int)(DD2Event.LaneSpawnRate / m));
        }
    }
}
