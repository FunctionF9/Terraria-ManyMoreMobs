using System;
using Terraria;

namespace ManyMoreMobs
{
    /// <summary>
    /// Counters for the two ways this mod can make an NPC not exist: the spawn gate REFUSING a spawn, and the
    /// evictor DESPAWNING something to make room. Both are silent by design, and that silence is why the
    /// "no enemies are spawning" class of report has never been diagnosable from a log — the player sees an
    /// empty screen and we see nothing at all.
    /// <para/>
    /// Read back through <c>/debugnpc spawninfo</c>. Deliberately plain static counters: this is written from
    /// the spawn path on every refusal, so it must cost nothing and must never throw.
    /// <para/>
    /// Not synced and not saved. In multiplayer these describe whichever side is running the gate (the server),
    /// so a client's readout is its own local view and will normally be all zeroes.
    /// </summary>
    internal static class SpawnGateTelemetry
    {
        // ── Refusals: the gate returned the failure index instead of a slot. ──
        internal static long RefusedTown, RefusedBoss, RefusedCritter, RefusedEnemy, RefusedPerTypeCap;

        /// <summary>Game tick of the most recent refusal, or -1 if the gate has never refused a spawn.</summary>
        internal static long LastRefusalTick = -1;

        // ── Evictions: EntityEvictor deleted a live NPC to free a slot for a guaranteed spawn. ──
        internal static long Evictions, EvictionsOfChainMembers, EvictionsFailed;

        /// <summary>Game tick of the most recent eviction, or -1 if nothing has ever been evicted.</summary>
        internal static long LastEvictionTick = -1;

        /// <summary>Type id of the most recently evicted NPC, for naming it in the report. -1 if none.</summary>
        internal static int LastEvictedType = -1;

        internal static void CountRefusal(NpcCategory cat)
        {
            LastRefusalTick = (long)Main.GameUpdateCount;
            switch (cat)
            {
                case NpcCategory.Town: RefusedTown++; break;
                case NpcCategory.Boss: RefusedBoss++; break;
                case NpcCategory.Critter: RefusedCritter++; break;
                default: RefusedEnemy++; break;
            }
        }

        internal static void CountPerTypeRefusal()
        {
            LastRefusalTick = (long)Main.GameUpdateCount;
            RefusedPerTypeCap++;
        }

        internal static void CountEviction(int type, bool wasChainMember)
        {
            LastEvictionTick = (long)Main.GameUpdateCount;
            LastEvictedType = type;
            Evictions++;
            if (wasChainMember)
                EvictionsOfChainMembers++;   // the destructive case: removing one link unravels the whole body
        }

        internal static void CountEvictionFailure() => EvictionsFailed++;

        /// <summary>Total refusals across every category — the single number that answers "is the gate the reason?".</summary>
        internal static long TotalRefusals => RefusedTown + RefusedBoss + RefusedCritter + RefusedEnemy + RefusedPerTypeCap;

        /// <summary>Zero everything. Called on world load so the counts describe the current session only.</summary>
        internal static void Reset()
        {
            RefusedTown = RefusedBoss = RefusedCritter = RefusedEnemy = RefusedPerTypeCap = 0;
            Evictions = EvictionsOfChainMembers = EvictionsFailed = 0;
            LastRefusalTick = LastEvictionTick = -1;
            LastEvictedType = -1;
        }

        /// <summary>"12s ago" / "never", for a tick stamp recorded by this class.</summary>
        internal static string Ago(long tick)
        {
            if (tick < 0)
                return "never";
            long age = (long)Main.GameUpdateCount - tick;
            return age < 60 ? "just now" : $"~{age / 60}s ago";
        }
    }
}
