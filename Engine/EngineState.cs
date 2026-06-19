using Terraria;
using Terraria.ModLoader;

namespace ManyMoreMobs
{
    /// <summary>
    /// The single mutable source of truth for the current NPC capacity.
    /// <para/>
    /// Why this exists instead of just using <c>Main.maxNPCs</c>: that field is <c>static readonly</c>, and
    /// the .NET JIT bakes the value a readonly static held at JIT time (200) directly into compiled code as a
    /// constant. So even after we write 750 into the field's storage, every compiled <c>ldsfld Main.maxNPCs</c>
    /// read keeps returning 200 (confirmed in-game: <c>direct=200 reflection=750</c>). A plain mutable static
    /// like this one is never constant-folded, so the IL patches and our own code read the live value.
    /// <para/>
    /// The <see cref="EngineILPatcher"/> rewrites the engine's hardcoded NPC bounds to read this field, and
    /// <see cref="MaxNpcCapRaise"/> sets it (to 200 vanilla, or the raised total).
    /// </summary>
    public static class EngineState
    {
        /// <summary>Current NPC slot capacity. 200 by default; raised by <see cref="MaxNpcCapRaise"/>.</summary>
        public static int NpcCap = 200;

        /// <summary>
        /// Returns the value the melee hit loop should see for the player's <c>attackCD</c> gate. When the
        /// "melee hits all in swing" option is on we return 0 so the gate never blocks (a swing hits every
        /// enemy in its arc); otherwise the real attackCD (vanilla one-hit-per-frame). Called from the
        /// IL-patched <c>Player.ItemCheck_MeleeHitNPCs</c>.
        /// </summary>
        public static int MeleeAttackGate(Player player)
        {
            var config = ModContent.GetInstance<HordeCombatConfig>();
            return (config != null && config.MeleeHitsAllInSwing) ? 0 : player.attackCD;
        }

        /// <summary>
        /// Saturating increment for the Cell Phone / Radar enemy count. The vanilla counter
        /// (<c>Player.accThirdEyeNumber</c>) is a <c>byte</c>, so once more than 255 enemies are in range the
        /// raw <c>++</c> overflows and wraps to 0 — making the on-screen count reset to "no enemies nearby"
        /// and recount. We replace the increment's <c>add</c> with this so it tops out at 255 instead (the
        /// display then reads a steady "255" rather than looping). Called from the IL-patched count loop in
        /// <c>Main.DrawInfoAccs</c>.
        /// </summary>
        public static int SaturatingByteInc(int current, int increment)
        {
            int v = current + increment;
            return v > 255 ? 255 : v;
        }
    }
}
