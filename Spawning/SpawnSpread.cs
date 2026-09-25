using Microsoft.Xna.Framework;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using Terraria.ModLoader;

namespace ManyMoreMobs
{
    /// <summary>
    /// Moves each natural spawn a little sideways, so two enemies that roll the same spawn spot don't stand in
    /// exactly the same place and walk as one.
    /// <para/>
    /// <b>Why they land on the same spot.</b> <c>NPC.SpawnNPC</c> places a spawn in a ring around the player,
    /// between <c>safeRangeX</c> (0.52 screens, just off-screen) and <c>spawnRangeX</c> (0.7 screens) — about 22
    /// tile columns each side. It picks a column at random and drops the NPC on the first solid ground below it.
    /// At vanilla's spawn rate a repeat is rare, but this mod multiplies how often a spawn is attempted: at the
    /// default a spawn lands nearly every frame, so the same column comes up about once a second and two enemies
    /// end up on the identical tile. Identical enemies chasing the same player then move identically, which is
    /// what makes five of them read as one. <see cref="SpawnRateMultiplier.EditSpawnRange"/> widens the ring to
    /// make the collision rarer; this makes the remaining ones visible as separate enemies.
    /// <para/>
    /// It only moves an NPC the game has already decided to spawn, and only when the new spot is clear, so it
    /// cannot add, remove or relocate a spawn into somewhere the game would not have put one.
    /// <para/>
    /// <b>Off by default.</b> Dev testing found the staggered arrivals read as out of place next to vanilla's
    /// tidy drops, so both spacing settings are opt-in rather than on.
    /// </summary>
    public class SpawnSpread : GlobalNPC
    {
        /// <summary>
        /// How far sideways a spawn may move, in pixels. Two tiles: enough to read as two enemies rather than
        /// one, small enough that the NPC still belongs to the spot the game picked.
        /// </summary>
        private const float MaxOffsetPixels = 32f;

        public override void OnSpawn(NPC npc, IEntitySource source)
        {
            // Natural spawns only. Statues, summons, boss adds, worm segments and town arrivals all carry their
            // own source and are placed deliberately — a spear trap's worth of precision is not ours to move.
            if (source is not EntitySource_SpawnNPC)
                return;

            // The spawning authority owns positions. A client's NPCs arrive from the server already placed, and
            // moving them here would just disagree with it.
            if (Main.netMode == NetmodeID.MultiplayerClient)
                return;

            var config = ModContent.GetInstance<ManyMoreMobsConfig>();
            if (config == null || !config.SpreadSpawnPositions)
                return;

            float offset = (Main.rand.NextFloat() * 2f - 1f) * MaxOffsetPixels;
            Vector2 moved = npc.position + new Vector2(offset, 0f);

            // Solid tiles are the one thing an offset can push an NPC into, and the engine's own placement is
            // known-good — so when the new spot is not clear we leave it exactly where the game put it.
            if (Collision.SolidCollision(moved, npc.width, npc.height))
                return;

            npc.position = moved;
        }
    }
}
