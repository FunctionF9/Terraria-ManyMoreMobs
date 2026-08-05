using Terraria;
using Terraria.ModLoader;

namespace ManyMoreMobs.MMMultiplayer
{
    /// <summary>
    /// Runtime side of the experimental multiplayer netcode fixes.
    /// <para/>
    /// Terraria's packet handlers don't just loop over the first 200 NPC slots — several of them <b>bounds
    /// check</b> the incoming slot index against a literal 200 and silently drop anything above it. The packet
    /// itself is fine (indices travel as a <c>short</c>, so 750 arrives intact); it's the receiving guard that
    /// throws the message away. That makes the expanded slots exist for everyone but be unaddressable by
    /// several specific packets.
    /// <para/>
    /// Every patched site simply reads its bound from <see cref="IndexLimit"/> instead of the literal, so with
    /// the toggle off — or in Default mode, where the cap IS 200 — behaviour is bit-for-bit vanilla.
    /// <para/>
    /// <b>Dependency direction:</b> this module may use the single-player engine (<see cref="EngineState"/>,
    /// config, categories); nothing in the single-player code may reference anything in
    /// <c>MMMultiplayer/</c>. Multiplayer is the experimental layer built on top, never a dependency of the
    /// part that works.
    /// </summary>
    internal static class MpNetGuards
    {
        /// <summary>Whether the experimental multiplayer netcode fixes are active (checked live, no reload).</summary>
        public static bool Enabled
        {
            get
            {
                var config = ModContent.GetInstance<ManyMoreMobsConfig>();
                return config != null && config.ExperimentalMultiplayerFixes;
            }
        }

        /// <summary>
        /// The NPC-slot bound a packet handler should accept, replacing vanilla's hardcoded 200. Returns the
        /// live cap when the fixes are on, and exactly 200 when they're off, so flipping the toggle restores
        /// vanilla packet handling instantly. Must never throw — this runs inside network message handling.
        /// </summary>
        public static int IndexLimit()
        {
            try
            {
                return Enabled ? EngineState.NpcCap : 200;
            }
            catch
            {
                return 200;
            }
        }
    }
}
