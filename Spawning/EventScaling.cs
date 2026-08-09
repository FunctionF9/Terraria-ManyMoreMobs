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

        /// <summary>
        /// Scale the kills required to advance an Old One's Army wave (called from a GetInvasionStatus detour).
        /// <para/>
        /// <b>The last wave of a tier is never scaled.</b> It is not a kill-count wave — it ends when the
        /// miniboss dies — and vanilla implements that with hardcoded numbers that assume the vanilla
        /// requirement, so multiplying the requirement breaks the pair.
        /// <para/>
        /// Tier 1, wave 5 is the confirmed case. Required is 140; once <c>NPC.waveKills</c> reaches 139,
        /// <c>Difficulty_1_GetMonsterPointsWorth</c> returns 0 for every kill except the Dark Mage, so progress
        /// parks at exactly 139 by design. The Dark Mage itself only spawns on
        /// <c>currentKillCount &gt; requiredKillCount * 0.5f</c>. Scale the requirement and that threshold
        /// leaves the reach of a counter that can no longer move — at the default ×5 it wants 350 against a
        /// hard ceiling of 139. Nothing can score, the Dark Mage never appears, and tier 1 cannot be finished,
        /// in single-player exactly as much as in multiplayer. The signature is a wave 5 frozen at
        /// <b>19%</b>: 139/700 rather than the 139/140 it should read.
        /// <para/>
        /// Tier 2, wave 7 is the same construction at 219/220 (the Ogre spawns fine — it has no 50% gate — but
        /// killing it yields point 220 against a scaled requirement, so the wave cannot complete). Tier 3's
        /// final wave is built differently, on Betsy's remaining health, and is observed to complete; it is
        /// covered here anyway because un-scaling only ever lowers the requirement toward vanilla, which
        /// cannot introduce a stall.
        /// </summary>
        public static int ScaleOldOnesArmyWaveKills(int required)
        {
            float m = C?.OldOnesArmyWaveLengthMultiplier ?? 1f;
            if (m <= 1f)
                return required;

            // Tier 1 ends on wave 5, tiers 2 and 3 on wave 7. Compared with >= because vanilla briefly steps
            // the counter one past the last wave to trigger the victory scene.
            int finalWave = DD2Event.OngoingDifficulty == 1 ? 5 : 7;
            if (NPC.waveNumber >= finalWave)
                return required;

            return (int)(required * m);
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
