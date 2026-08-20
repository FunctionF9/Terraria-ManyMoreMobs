using System;
using System.Reflection;
using Terraria.ModLoader;

namespace ManyMoreMobs
{
    /// <summary>
    /// Keeps Boss Checklist's NPC-slot array in step with the raised cap.
    /// <para/>
    /// Boss Checklist sizes <c>RecordSystem.ActiveNPCEntryFlags</c> to <c>Main.maxNPCs</c> and then indexes it
    /// by <c>npc.whoAmI</c>. That is correct code — but this mod deliberately leaves <c>Main.maxNPCs</c> at 200
    /// and redirects the ENGINE to read <see cref="EngineState.NpcCap"/> instead, so a mod that asks the
    /// documented question gets the pre-raise answer while NPCs really do exist above slot 199. The array ends
    /// up 200 long and every NPC that dies in a higher slot walks off the end of it.
    /// <para/>
    /// The throw lands in <c>RecordBossNPC.OnKill</c>, whose very first statement is an unguarded
    /// <c>ActiveNPCEntryFlags[npc.whoAmI] = -1</c>, and unwinds through
    /// <c>NPCLoader.OnKill → NPC.NPCLoot → NPC.checkDead → NPC.StrikeNPC</c> into whatever was dealing the
    /// damage. tModLoader swallows it, so there is no crash — just a silently abandoned call stack. The visible
    /// symptom was explosives: the throw aborts <c>Projectile.Damage</c> mid-AoE at the first enemy that dies,
    /// and <c>Projectile.Update</c> never reaches the fuse, so the bomb damages one enemy, skips the rest of the
    /// blast, and destroys no terrain. With no enemies around nothing dies, nothing throws, and bombs work
    /// perfectly — which is exactly how it was reported.
    /// <para/>
    /// So we grow the array ourselves. New entries are filled with <b>-1</b>, Boss Checklist's "no entry here"
    /// sentinel: zero is a valid entry index, and their <c>Contains</c> / <c>Any</c> lookups would read a
    /// zero-filled tail as "the first boss in the list is still alive" and suppress its despawn message and
    /// record tracking.
    /// <para/>
    /// The check runs every tick rather than once, because Boss Checklist reallocates the array in both
    /// <c>ClearWorld</c> and <c>OnWorldLoad</c> and <see cref="ModSystem"/> ordering between mods is not
    /// guaranteed — a one-tick window is the smallest we can close without hooking their internals. It is an
    /// integer compare on an already-resolved field, so the per-tick cost is nil.
    /// <para/>
    /// This is a shim, not a fix. The real fix is the bounds guard Boss Checklist already uses elsewhere in the
    /// same file, and it belongs upstream where it would also cover every other cap-raising mod.
    /// </summary>
    public class BossChecklistCompat : ModSystem
    {
        /// <summary>Value Boss Checklist uses for "no boss entry is occupying this slot".</summary>
        private const int UnflaggedEntry = -1;

        private static FieldInfo _activeNpcEntryFlags;

        // Set once the shim gives up, so a broken lookup can't retry (and re-log) on every tick forever.
        private static bool _disabled;

        // The install log line only makes sense once we know whether there is anything to attach to, and the
        // first grow is worth recording so a log read can tell "shim active" from "shim never fired".
        private static bool _loggedFirstGrow;

        public override void PostSetupContent()
        {
            // Resolved here rather than in OnModLoad: every mod exists by this point regardless of load order.
            try
            {
                if (!ModLoader.TryGetMod("BossChecklist", out Mod bossChecklist))
                {
                    _disabled = true;
                    return;
                }

                Type recordSystem = bossChecklist.Code?.GetType("BossChecklist.Systems.RecordSystem");
                _activeNpcEntryFlags = recordSystem?.GetField("ActiveNPCEntryFlags",
                    BindingFlags.Public | BindingFlags.Static);

                if (_activeNpcEntryFlags == null || _activeNpcEntryFlags.FieldType != typeof(int[]))
                {
                    // Their internals moved. Say so loudly enough to be found in a log, then stay out of the way.
                    _disabled = true;
                    Mod.Logger.Warn("[MMM] Boss Checklist is loaded but RecordSystem.ActiveNPCEntryFlags was not " +
                                    "found; slot-array compatibility shim inactive. Expect dropped NPC deaths " +
                                    "above slot 199 while both mods are enabled.");
                    return;
                }

                Mod.Logger.Info("[MMM] Boss Checklist compatibility shim armed (RecordSystem.ActiveNPCEntryFlags " +
                                "will be grown to the raised NPC cap).");
            }
            catch (Exception e)
            {
                _disabled = true;
                MmmLog.Report(e, "BossChecklistCompat setup");
            }
        }

        public override void PostUpdateNPCs()
        {
            if (_disabled || _activeNpcEntryFlags == null)
                return;

            try
            {
                GrowIfShort();
            }
            catch (Exception e)
            {
                // A throwing shim would be worse than the bug it patches — report once and disable.
                _disabled = true;
                MmmLog.Report(e, "BossChecklistCompat grow");
            }
        }

        private static void GrowIfShort()
        {
            int cap = EngineState.NpcCap;

            // Null until their ClearWorld/OnWorldLoad runs; nothing indexes it before then either.
            if (_activeNpcEntryFlags.GetValue(null) is not int[] flags)
                return;

            if (flags.Length >= cap)
                return;

            int[] grown = new int[cap];
            Array.Copy(flags, grown, flags.Length);
            for (int i = flags.Length; i < cap; i++)
                grown[i] = UnflaggedEntry;

            _activeNpcEntryFlags.SetValue(null, grown);

            if (!_loggedFirstGrow)
            {
                _loggedFirstGrow = true;
                MmmLog.Info($"Grew Boss Checklist's ActiveNPCEntryFlags from {flags.Length} to {cap} " +
                            $"(new entries set to {UnflaggedEntry}).");
            }
        }

        public override void Unload()
        {
            _activeNpcEntryFlags = null;
            _disabled = false;
            _loggedFirstGrow = false;
        }
    }
}
