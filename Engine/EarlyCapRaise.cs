using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Text.Json;
using Terraria;
using Terraria.ModLoader;

namespace ManyMoreMobs
{
    /// <summary>
    /// Raises <c>Main.maxNPCs</c> during tModLoader's <b>"Constructing Mods"</b> phase — before any mod,
    /// including this one, loads its content — so that every OTHER mod builds itself around the raised cap
    /// instead of around 200.
    /// <para/>
    /// <b>The problem this solves.</b> Content mods routinely size NPC-indexed arrays from the documented
    /// field, e.g. Calamity's <c>CalamityDrawParameterNPC.Load()</c>:
    /// <code>DrawingMiracleBlight = new bool[Main.maxNPCs + 1];</code>
    /// That runs during ITS content load. <see cref="MaxNpcCapRaise"/> used to write the field in
    /// <c>PostSetupContent</c>, which is after every mod has finished loading — so the array stayed 201 long
    /// forever, while the guards protecting it (<c>whoAmI &lt; Main.maxNPCs</c>) later read the raised value
    /// and waved high-slot NPCs straight into an <c>IndexOutOfRangeException</c>. Because those throws happen
    /// inside <c>NPC.SetDefaults</c> (called from <c>NPC.SpawnNPC</c>) and inside <c>NPCLoader.PreAI</c>, they
    /// take the whole spawn pass and the NPC update tick down with them: the visible symptom is "no enemies
    /// spawn at all".
    /// <para/>
    /// <b>Why this ordering works.</b> tModLoader constructs every <see cref="Mod"/> instance first and only
    /// then loads content mod-by-mod ("Constructing Mods..." precedes the first "Adding Content:" line). So a
    /// write from this mod's constructor lands before every other mod's <c>Load()</c>. It also lands before
    /// their code is first compiled, which matters just as much: <c>Main.maxNPCs</c> is a <c>static readonly</c>
    /// int, so the JIT bakes whatever it holds at compile time into the machine code as a constant. Raise it
    /// early and the constant every mod gets baked with IS the raised cap — allocations, loop bounds and index
    /// guards all agree, with no IL rewriting of anyone's assembly.
    /// <para/>
    /// <b>Multiplayer caveat.</b> These settings are <c>ServerSide</c>, so in a joined game the server's values
    /// are the ones that count — and no network exists this early, so what gets read here is the local file.
    /// The cap itself still ends up correct either way, because <see cref="MaxNpcCapRaise.PostSetupContent"/>
    /// applies the real (synced) config afterwards. What cannot be corrected afterwards is the size other mods
    /// already allocated their arrays at: a client whose local file asked for a SMALLER cap than the server's
    /// gets those mods sized for the smaller number, and the compatibility fix quietly does nothing for it.
    /// Consistent with multiplayer being unsupported here, but it is a trap rather than an obvious failure.
    /// <para/>
    /// <b>Deliberately minimal.</b> Only the NPC array itself is grown here. Everything else
    /// (<c>perIDStaticNPCImmunity</c> and friends) is sized by tModLoader from content that does not exist yet,
    /// so it stays in <see cref="MaxNpcCapRaise.PostSetupContent"/> where it always was. Nothing runs a game
    /// tick during mod loading, so the gap between the two costs nothing.
    /// </summary>
    internal static class EarlyCapRaise
    {
        private static readonly FieldInfo MaxNPCsField =
            typeof(Main).GetField(nameof(Main.maxNPCs), BindingFlags.Public | BindingFlags.Static);

        /// <summary>
        /// Compiled <c>Main.maxNPCs = value;</c>. The field is <c>static readonly</c>, and in .NET 8
        /// <see cref="FieldInfo.SetValue"/> throws <see cref="FieldAccessException"/> on an init-only static
        /// once its type is initialized — but the JIT does not enforce init-only on an emitted <c>stsfld</c>,
        /// so this store succeeds. Standard Harmony/MonoMod technique for readonly statics.
        /// </summary>
        internal static readonly Action<int> SetMaxNPCs = BuildMaxNPCsSetter();

        /// <summary>
        /// Whether the constructor-time raise actually happened. <see cref="MaxNpcCapRaise"/> checks this so it
        /// can undo the raise if the real config turns out to disagree with what we read off disk.
        /// </summary>
        internal static bool Applied { get; private set; }

        /// <summary>
        /// The total this early raise actually sized other mods' NPC arrays for, or 0 if it never ran.
        /// <para/>
        /// Kept so <see cref="MaxNpcCapRaise.PostSetupContent"/> can compare it against the config value the
        /// game ends up actually using. In single-player the two always agree. In multiplayer they can
        /// silently disagree, and the disagreement is dangerous — see the note there.
        /// </summary>
        internal static int AppliedTotal { get; private set; }

        /// <summary>
        /// First <c>Main.npc</c> slot this class created, or <see cref="int.MaxValue"/> if it created none.
        /// <see cref="EngineArrayResizer"/> picks the fill loop back up here, so the slots we allocated bare
        /// still get their <c>SetDefaults</c> pass once the content that hook needs actually exists.
        /// </summary>
        internal static int NpcArrayFilledFrom { get; private set; } = int.MaxValue;

        // Mod.Logger is not assigned until after construction, so anything worth saying is queued and flushed
        // from Mod.Load(). Failing silently here would be the worst outcome available — this is the one place
        // where a wrong answer stays invisible for the rest of the session.
        private static readonly List<string> PendingLog = new();

        /// <summary>Called from the <see cref="ManyMoreMobs"/> constructor. Must never throw.</summary>
        internal static void Apply()
        {
            try
            {
                if (!TryReadConfiguredTotal(out int total))
                    return;

                if (SetMaxNPCs == null)
                {
                    PendingLog.Add("[MMM] Early cap raise skipped: could not build the Main.maxNPCs setter.");
                    return;
                }

                EngineArrayResizer.GrowNpcArrayEarly(total, out int filledFrom);
                NpcArrayFilledFrom = filledFrom;

                EngineState.NpcCap = total;
                SetMaxNPCs(total);
                AppliedTotal = total;
                Applied = true;

                PendingLog.Add($"[MMM] Early cap raise: Main.maxNPCs = {total} before any mod loaded content " +
                               $"(Main.npc length {Main.npc.Length}). Other mods size their NPC arrays to fit.");
            }
            catch (Exception e)
            {
                // Leave the engine exactly as we found it; MaxNpcCapRaise still performs the late raise as before.
                Revert();
                PendingLog.Add($"[MMM] Early cap raise failed, falling back to the late raise: {e}");
            }
        }

        /// <summary>Puts the cap back to vanilla. Used by the failure path here and by mod unload.</summary>
        internal static void Revert()
        {
            EngineState.NpcCap = 200;
            try { SetMaxNPCs?.Invoke(200); }
            catch { /* nothing useful to do while tearing down */ }
            Applied = false;
        }

        /// <summary>Emits everything queued during construction. Called once, from <c>Mod.Load()</c>.</summary>
        internal static void FlushLog(Mod mod)
        {
            foreach (string line in PendingLog)
                mod.Logger.Info(line);
            PendingLog.Clear();
        }

        /// <summary>
        /// Reads the cap settings straight off disk, because no <see cref="ModConfig"/> instance exists yet.
        /// <para/>
        /// This is safe precisely because the settings involved are <c>[ReloadRequired]</c>: they cannot change
        /// without a mod reload, which re-runs this constructor, so the value read here can never drift from the
        /// value the rest of the mod sees. tModLoader omits properties still at their default from the JSON, so
        /// a fresh install has an absent file or absent keys and we fall back to the config class's own defaults
        /// rather than duplicating them here.
        /// <para/>
        /// Parsed with <c>System.Text.Json</c> rather than Newtonsoft on purpose: the latter is an older
        /// assembly than the runtime and every call into it costs a CS1701 reference warning at mod build time.
        /// </summary>
        private static bool TryReadConfiguredTotal(out int total)
        {
            var defaults = new ManyMoreMobsConfig();
            NpcCapMode mode = defaults.CapMode;
            bool enabled = defaults.RaiseCapBeforeOtherMods;
            total = defaults.MaxNPCTotal;

            string path = Path.Combine(Main.SavePath, "ModConfigs", "ManyMoreMobs_ManyMoreMobsConfig.json");
            bool found = File.Exists(path);
            if (found)
            {
                using JsonDocument json = JsonDocument.Parse(File.ReadAllText(path));
                JsonElement root = json.RootElement;

                if (root.TryGetProperty(nameof(ManyMoreMobsConfig.CapMode), out JsonElement rawMode)
                    && Enum.TryParse(rawMode.GetString(), out NpcCapMode parsedMode))
                    mode = parsedMode;

                if (root.TryGetProperty(nameof(ManyMoreMobsConfig.RaiseCapBeforeOtherMods), out JsonElement rawEnabled))
                    enabled = rawEnabled.GetBoolean();

                if (root.TryGetProperty(nameof(ManyMoreMobsConfig.MaxNPCTotal), out JsonElement rawTotal))
                    total = rawTotal.GetInt32();
            }

            // Mirror the property's [Range]: a hand-edited config must not size the array to something absurd.
            // Shares the config's own constant rather than repeating the number — when these were two
            // literals, raising the config bound without raising this one would silently clamp the cap back
            // down, and nothing in the log would say the chosen value had been overridden.
            total = Math.Clamp(total, 200, ManyMoreMobsConfig.MaxNPCTotalCeiling);

            // Say where the numbers came from. Since tModLoader omits defaulted properties, a successful read
            // and a missing file can produce identical values — without this line the log cannot tell the two
            // apart, and "we silently used defaults" is exactly the failure worth being able to spot.
            PendingLog.Add($"[MMM] Early cap raise config ({(found ? "read from disk" : "file absent, using defaults")}): " +
                           $"enabled={enabled} mode={mode} total={total}.");

            return enabled && mode == NpcCapMode.Expanded && total > 200;
        }

        // Runs from a static field initializer, i.e. as this type's class constructor — which is triggered by
        // the very first line of Apply() and so sits OUTSIDE its try block. Anything escaping here would
        // surface as a TypeInitializationException out of the Mod constructor and fail the whole mod load, so
        // it swallows and returns null instead; every caller already treats null as "no setter available".
        private static Action<int> BuildMaxNPCsSetter()
        {
            if (MaxNPCsField == null)
                return null;

            try
            {
                var dm = new DynamicMethod("MMM_SetMaxNPCs", typeof(void), new[] { typeof(int) },
                    typeof(Main).Module, skipVisibility: true);
                ILGenerator il = dm.GetILGenerator();
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Stsfld, MaxNPCsField);
                il.Emit(OpCodes.Ret);
                return (Action<int>)dm.CreateDelegate(typeof(Action<int>));
            }
            catch
            {
                return null;
            }
        }
    }
}
