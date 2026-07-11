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
        /// The TRUE nearby-enemy count for the Radar / Cell Phone HUD, tracked as an uncapped int alongside
        /// vanilla's <c>byte Player.accThirdEyeNumber</c> (which physically can't exceed 255). Updated inside
        /// the IL-patched count loop via <see cref="SaturatingByteInc"/> and read at the display site via
        /// <see cref="GetRadarDisplayCount"/>, so the HUD shows the real number instead of topping out at 255.
        /// </summary>
        public static int RadarNearbyCount;

        /// <summary>
        /// Increment used inside the IL-patched Radar count loop in <c>Main.DrawInfoAccs</c>. Does two things:
        /// (1) tallies the real count into <see cref="RadarNearbyCount"/> (uncapped), and (2) returns a value
        /// that saturates the vanilla <c>byte accThirdEyeNumber</c> at 255 instead of overflowing/wrapping —
        /// the byte is still what vanilla's "any enemies nearby?" gate reads, so it must stay sane.
        /// </summary>
        public static int SaturatingByteInc(int current, int increment)
        {
            // current == 0 marks the first hit of a fresh count pass (vanilla just reset accThirdEyeNumber to
            // 0 before the loop), so restart our tally then; otherwise accumulate. The byte saturates rather
            // than wrapping, so it never spuriously reads 0 mid-pass and mis-triggers a restart.
            RadarNearbyCount = current == 0 ? increment : RadarNearbyCount + increment;

            int v = current + increment;
            return v > 255 ? 255 : v;
        }

        /// <summary>
        /// The value the Radar HUD should actually display — our uncapped <see cref="RadarNearbyCount"/>. Takes
        /// the <see cref="Player"/> that the patched call site leaves on the stack (where it used to read the
        /// <c>accThirdEyeNumber</c> field) and simply ignores it.
        /// </summary>
        public static int GetRadarDisplayCount(Player player) => RadarNearbyCount;

        /// <summary>
        /// The slot index of the rare NPC the Lifeform Analyzer is tracking, or -1 for none. Vanilla stores
        /// this index in <c>byte Player.accCritterGuideNumber</c>, which can't address the expanded zone
        /// (slot 300 would truncate to 44 and the HUD would name a completely different NPC) — so the
        /// IL-patched Lifeform Analyzer block tracks the true slot here instead.
        /// </summary>
        public static int LifeformAnalyzerSlot = -1;

        /// <summary>
        /// Pass-through recorder inserted just before the analyzer's <c>(byte)</c> store: remembers the true
        /// found slot (or -1 sentinel) in <see cref="LifeformAnalyzerSlot"/> and returns it unchanged so the
        /// vanilla byte store still happens (harmlessly truncated; nothing reads it once we patch the read).
        /// </summary>
        public static int TrackLifeformSlot(int slot)
        {
            LifeformAnalyzerSlot = slot;
            return slot;
        }

        /// <summary>
        /// Replaces the analyzer's cached read of <c>accCritterGuideNumber</c> on non-scan frames. Takes the
        /// <see cref="Player"/> the patched call site leaves on the stack (where the field read consumed it)
        /// and ignores it, returning the true tracked slot instead of the truncated byte.
        /// </summary>
        public static int GetLifeformSlot(Player player) => LifeformAnalyzerSlot;

        /// <summary>
        /// Selection score for the Lifeform Analyzer scan — replaces every raw <c>NPC.rarity</c> read in the
        /// IL-patched analyzer block (loop compare, best-so-far store, display validity check), so all three
        /// agree on the same value. Rescueable NPCs (see <see cref="NpcCategorizer.IsRescueNpc"/>) get a large
        /// boost: when someone needs rescuing within detection range, the analyzer always names THEM over
        /// golden critters / rare enemies — finding your rescues in a horde beats admiring a gold bunny.
        /// Everything else keeps its vanilla rarity, so behavior is unchanged when no rescue is around.
        /// </summary>
        public static int LifeformPriorityScore(NPC npc)
            => NpcCategorizer.IsRescueNpc(npc.type) ? npc.rarity + 1000 : npc.rarity;
    }
}
