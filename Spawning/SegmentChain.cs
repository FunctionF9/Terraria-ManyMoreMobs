using System;
using Terraria;

namespace ManyMoreMobs
{
    /// <summary>
    /// Per-slot record of the multi-part entity a slot belongs to, and which category that entity counts as.
    /// <para/>
    /// Worms are not one NPC, they are a head plus dozens of segments that each occupy their own slot, and the
    /// engine gives us no reliable way to ask an arbitrary segment which body it belongs to:
    /// <list type="bullet">
    /// <item>Most worms point <see cref="NPC.realLife"/> at the head, so a segment can be traced back — but the
    /// Eater of Worlds deliberately does <b>not</b>, because it splits. The shared builder in
    /// <c>NPC.AI_006_Worms</c> reads <c>if (type &lt; 13 || type &gt; 15) segment.realLife = realLife;</c>,
    /// skipping exactly the EoW types.</item>
    /// <item>Flags don't help either: <c>NPCID.Sets.ShouldBeCountedAsBoss</c> lists the EoW <i>head</i> (13) and
    /// neither the body (14) nor the tail (15), and none of the three carry <c>npc.boss</c>.</item>
    /// </list>
    /// So an EoW used to count as 1 Boss plus 65 Enemies — its segments quietly eating the Enemy budget while
    /// sitting in the Boss zone. This table is the missing link: the spawn gate tags each segment as it is
    /// created, and everything downstream (counting, eviction, slot zoning) reads the head's category from here.
    /// <para/>
    /// It is deliberately transient — rebuilt from spawns, never saved. No multi-part enemy survives a world
    /// reload (<c>NPCID.Sets.SavesAndLoads</c> covers none of them), so there is nothing to restore.
    /// </summary>
    internal static class SegmentChain
    {
        /// <summary>Sentinel for "this slot is not part of a multi-part entity". Cannot be 0 — that is <see cref="NpcCategory.Town"/>.</summary>
        private const sbyte NotChained = -1;

        private static sbyte[] _chain = Array.Empty<sbyte>();

        /// <summary>Grow to the current NPC array, initialising new entries to "not chained" (Array.Resize zero-fills, and 0 is Town).</summary>
        public static void EnsureSize()
        {
            if (_chain.Length >= Main.npc.Length)
                return;

            int oldLength = _chain.Length;
            Array.Resize(ref _chain, Main.npc.Length);
            for (int i = oldLength; i < _chain.Length; i++)
                _chain[i] = NotChained;
        }

        /// <summary>
        /// Record (or clear) a slot's chain membership. Always called for every gated spawn, with
        /// <paramref name="chained"/> false as well as true, so a recycled slot can never inherit a stale tag
        /// from whatever used to live there.
        /// </summary>
        public static void Mark(int slot, bool chained, NpcCategory category)
        {
            EnsureSize();
            if ((uint)slot >= (uint)_chain.Length)
                return;

            _chain[slot] = chained ? (sbyte)category : NotChained;
        }

        /// <summary>True if this slot holds part of a multi-part entity (a worm segment, or a boss-spawned add).</summary>
        public static bool IsMember(int slot)
            => (uint)slot < (uint)_chain.Length && _chain[slot] != NotChained;

        /// <summary>The category of the entity this slot belongs to. Only meaningful when <see cref="IsMember"/>.</summary>
        public static NpcCategory CategoryOf(int slot)
            => IsMember(slot) ? (NpcCategory)_chain[slot] : NpcCategory.Enemy;

        /// <summary>
        /// True for a member of a <b>Boss</b> chain. Kept distinct from <see cref="IsMember"/> because the two
        /// protect against different things: boss chains must never be evicted or replaced at all, whereas an
        /// ordinary worm is only protected while it is still assembling.
        /// </summary>
        public static bool IsBossMember(int slot)
            => IsMember(slot) && CategoryOf(slot) == NpcCategory.Boss;

        /// <summary>
        /// Forget every tag. Must run on world load AND unload.
        /// <para/>
        /// The table is static for the whole session while the tags describe one world's slots, and not every
        /// NPC is created through <c>NPC.NewNPC</c> — <c>WorldFile.LoadNPCs</c> writes town NPCs straight into
        /// <c>Main.npc[i]</c> with <c>SetDefaults</c>, so it never passes the gate and never overwrites a tag.
        /// Without this, slot 5 holding a boss in one world would still read as a Boss chain member when the
        /// next world loads its Guide into slot 5.
        /// </summary>
        public static void Reset()
        {
            for (int i = 0; i < _chain.Length; i++)
                _chain[i] = NotChained;
        }

        /// <summary>Carry a tag across a relocation (see <see cref="SlotRezoner"/>) and clear the vacated slot.</summary>
        public static void Move(int from, int to)
        {
            EnsureSize();
            if ((uint)from >= (uint)_chain.Length || (uint)to >= (uint)_chain.Length)
                return;

            _chain[to] = _chain[from];
            _chain[from] = NotChained;
        }
    }
}
