using System;
using System.Reflection;
using Terraria;
using Terraria.ModLoader;

namespace ManyMoreMobs
{
    /// <summary>
    /// EXPERIMENTAL Stage 2: raises Terraria's hard 200-NPC engine cap to <see cref="ManyMoreMobsConfig.MaxNPCTotal"/>.
    /// Single-player only for now (no multiplayer cap handshake yet).
    /// <para/>
    /// Orchestrates, at mod load (before any world/player exists):
    /// <list type="number">
    /// <item>register the <see cref="EngineILPatcher"/> detours (so the engine loops honor <c>Main.maxNPCs</c>),</item>
    /// <item><see cref="EngineArrayResizer.Grow"/> the NPC-indexed engine arrays,</item>
    /// <item>set the <c>readonly</c> <c>Main.maxNPCs</c> field to the new total.</item>
    /// </list>
    /// Note: <c>Main.maxNPCs</c> is <c>static readonly</c>, so it takes an emitted <c>stsfld</c> to write —
    /// see <see cref="EarlyCapRaise.SetMaxNPCs"/>, which owns that setter because it needs it before this
    /// <see cref="ModSystem"/> exists.
    /// <para/>
    /// <b>This is the LATE half of the raise.</b> <see cref="EarlyCapRaise"/> already wrote the field during
    /// mod construction so other mods size their own arrays to the raised cap; what is left for here is the
    /// engine state that only exists once content loading has finished. Both halves are idempotent, and either
    /// one working alone still leaves a coherent engine.
    /// <para/>
    /// Gated behind <see cref="ManyMoreMobsConfig.CapMode"/> being <see cref="NpcCapMode.Expanded"/> (both
    /// that and <c>MaxNPCTotal</c> are <c>[ReloadRequired]</c>), so a Default-mode install is never touched.
    /// On unload the cap is restored to 200 and the IL detours auto-undo via <see cref="MonoModHooks"/>.
    /// </summary>
    public class MaxNpcCapRaise : ModSystem
    {
        private static readonly FieldInfo MaxNPCsField =
            typeof(Main).GetField(nameof(Main.maxNPCs), BindingFlags.Public | BindingFlags.Static);

        // Compiled `Main.maxNPCs = value;` — works on the readonly static where reflection SetValue cannot.
        // Built and owned by EarlyCapRaise, which needs it before this ModSystem exists.
        private static Action<int> SetMaxNPCs => EarlyCapRaise.SetMaxNPCs;

        /// <summary>
        /// The NPC capacity this mod is actually operating at (200 vanilla, or the raised total once applied).
        /// Backed by <see cref="EngineState.NpcCap"/> — we never read <c>Main.maxNPCs</c> in mod code, because
        /// the JIT bakes the old <c>readonly</c> value (200) into compiled reads.
        /// </summary>
        public static int AppliedCap => EngineState.NpcCap;

        private static bool _patchesApplied;
        private static bool _applied;

        public override void OnModLoad()
        {
            var config = ModContent.GetInstance<ManyMoreMobsConfig>();
            if (config == null || config.CapMode != NpcCapMode.Expanded)
            {
                // EarlyCapRaise read the same settings off disk before any ModConfig existed. If the loaded
                // config disagrees (a corrupt or hand-edited file), the real one wins and the early raise is
                // undone here — while no world is loaded and nothing has been indexed yet.
                if (EarlyCapRaise.Applied)
                {
                    EarlyCapRaise.Revert();
                    Mod.Logger.Warn("[MMM] Early cap raise reverted: the loaded config is not in Expanded mode.");
                }
                return;
            }

            if (SetMaxNPCs == null)
            {
                Mod.Logger.Error("[MMM] Could not build the Main.maxNPCs setter; cap raise aborted (cap stays 200).");
                return;
            }

            // Register the IL detours once, early. They take effect when the patched methods next run
            // (in-world) and read EngineState.NpcCap, which is set in PostSetupContent below.
            EngineILPatcher.ApplyAll(Mod);
            _patchesApplied = true;
        }

        /// <summary>
        /// Non-null when the early cap raise and the active config disagree about the total — see the note in
        /// <see cref="PostSetupContent"/>. Surfaced by <c>/mmm info</c> so a player can report it without
        /// having to find the log.
        /// </summary>
        internal static string CapMismatch { get; private set; }

        public override void PostSetupContent()
        {
            // PostSetupContent runs AFTER tModLoader has finished its own content/array setup (instanced
            // global slots and Projectile.perIDStaticNPCImmunity are finalized here). Growing the NPC-indexed
            // arrays now — rather than in OnModLoad — means tModLoader's later resize can't undo them.
            if (!_patchesApplied || _applied)
                return;

            var config = ModContent.GetInstance<ManyMoreMobsConfig>();
            int total = config.MaxNPCTotal;

            // ── Multiplayer config-mismatch detection ────────────────────────────────────────────────────
            //
            // EarlyCapRaise runs before any mod loads content, so it cannot ask tModLoader for our config —
            // it reads ModConfigs/ManyMoreMobs_ManyMoreMobsConfig.json off disk directly. That is correct in
            // single-player and WRONG on a modded server join, because of how tModLoader applies a
            // server-supplied config:
            //
            //     ConfigManager.Load()  —  if (config.Mode == ServerSide && ModNet.NetReloadActive) {
            //                                  PopulateObject(ModNet.pendingConfigs...);  return;  }
            //
            // The server's values are populated into MEMORY and the method returns without reading or
            // writing the file. So during the forced reload on join, EarlyCapRaise reads the CLIENT's stale
            // local number while every runtime read gets the SERVER's.
            //
            // The dangerous direction is client-lower-than-server (including the client having CapMode set
            // to Default, which skips the early raise entirely). Other mods then allocate their NPC-indexed
            // arrays for the small number during their own Load(), and the raise below moves Main.maxNPCs up
            // to the server's number afterwards — which is precisely the crash EarlyCapRaise exists to
            // prevent, reappearing in multiplayer. It surfaces as an IndexOutOfRangeException inside an
            // unrelated mod, or as no enemies spawning at all, with nothing pointing at us.
            //
            // We cannot repair another mod's already-allocated arrays, so this does not try to. It makes the
            // failure DIAGNOSABLE instead of silent, which is the whole difference between a rough feature
            // and an undebuggable one. The player's fix is to set their local config to match the server's.
            if (EarlyCapRaise.Applied && EarlyCapRaise.AppliedTotal != total)
            {
                CapMismatch = $"early raise sized other mods' arrays for {EarlyCapRaise.AppliedTotal}, " +
                              $"but the active config says {total}";
                Mod.Logger.Error(
                    $"[MMM] CONFIG MISMATCH: {CapMismatch}. This happens when your local cap settings differ " +
                    "from the server's: the early raise reads your own config file, while the game then uses " +
                    "the server's. Other mods may have sized their NPC arrays for the wrong number, which can " +
                    "crash them or stop enemies spawning. Set your Max NPC Total and Cap Mode to match the " +
                    "server and rejoin.");
            }
            else if (!EarlyCapRaise.Applied && total > 200)
            {
                CapMismatch = $"early raise did not run, but the active config asks for {total}";
                Mod.Logger.Warn(
                    $"[MMM] {CapMismatch}. If you are joining a server, your local Cap Mode is probably " +
                    "Default while the server's is Expanded. Other mods will have sized their NPC arrays " +
                    "for 200. Set your local config to match the server and rejoin.");
            }

            if (total <= 200)
            {
                Mod.Logger.Info($"[MMM] Cap raise target ({total}) <= 200; nothing to do.");
                return;
            }

            try
            {
                EngineArrayResizer.Grow(total, Mod);
                EngineState.NpcCap = total;  // what the IL-patched gameplay loops read
                SetMaxNPCs(total);           // for engine code that reads Main.maxNPCs live (see the note below on saving)
                _applied = true;
                Mod.Logger.Info($"[MMM] NPC cap raised to {total} (Main.maxNPCs={(int)MaxNPCsField.GetValue(null)}, AppliedCap={AppliedCap}).");
            }
            catch (Exception e)
            {
                Mod.Logger.Error($"[MMM] NPC cap raise failed; reverting to 200. {e}");
                EngineState.NpcCap = 200;
                TrySetMaxNPCs(200);
                _applied = false;
            }
        }

        public override void OnWorldLoad()
        {
            // The bonus slot zone (200+) is session-only. Clear it on every world load so high-slot NPCs from
            // a previous session don't persist ("don't reset on reload") and can't leave the array in a stale
            // state.
            //
            // On saving, be precise — an earlier version of this comment said "save/load is pinned to vanilla
            // 200", which is only half true and would mislead anyone reasoning from it. tModLoader's
            // WorldIO.SaveNPCs/LoadNPCs ARE forced back to 200 by Patch_ForceVanilla200. Vanilla's own
            // WorldFile.SaveNPCs is NOT: it iterates Main.npc.Length, so 751 under a raised cap. That is
            // harmless and arguably correct — it only writes town NPCs and NPCID.Sets.SavesAndLoads types
            // (just the four Lunar Pillars), and WorldFile.LoadNPCs re-packs them from slot 0 upward, so a
            // high-slot town NPC survives a save rather than being silently dropped.
            //
            // This runs even when the cap raise is OFF: if the player toggled the raise off and reloaded, the
            // array can still be grown (751) and hold stale NPCs in slots 200+ from the previous (raised)
            // session. Those would otherwise count toward the caps and block all spawning, so clear them too.
            for (int i = 200; i < Main.npc.Length; i++)
            {
                NPC n = Main.npc[i];
                if (n != null && n.active)
                    n.active = false;
            }

            if (ModContent.GetInstance<ManyMoreMobsConfig>()?.DebugMode == true)
                MmmLog.Dump("OnWorldLoad state", EngineDiagnostics.BuildStateReport());
        }

        public override void Unload()
        {
            // Restore the vanilla cap so the engine is clean if this mod is disabled.
            // IL detours are undone automatically by MonoModHooks on unload; the grown arrays are left in
            // place (harmless — engine code only touches slots < the cap plus the dummy slot).
            // Unconditional: the raise can also have come from EarlyCapRaise, which runs long before the flag
            // below is ever set, and leaving 750 in a readonly static after unload would outlive us.
            EarlyCapRaise.Revert();
            _applied = false;
            _patchesApplied = false;
        }

        private static void TrySetMaxNPCs(int value)
        {
            try { SetMaxNPCs?.Invoke(value); }
            catch { /* nothing useful to do during unload */ }
        }
    }
}
