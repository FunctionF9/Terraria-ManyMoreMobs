using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace ManyMoreMobs
{
    /// <summary>
    /// The player-facing command surface: read-only inspection anyone can be asked to run, with no config
    /// change and nothing that alters the world.
    /// <para/>
    /// Split out from the developer commands because the two have different audiences. This is the set that
    /// goes in a Workshop reply, so it has to be short to type, obviously this mod's, and safe to hand to
    /// someone who has never used a chat command — a reporter mistyping a spawn or kill command while trying
    /// to file a bug report helps nobody.
    /// </summary>
    public class MmmCommand : ModCommand
    {
        public override string Command => "mmm";
        public override string Usage => "/mmm <info|version|counts|dump|blocked|spawninfo>";
        public override string Description => "Many More Mobs: inspect what the mod is doing. Start with /mmm info. Full reports also go to ManyMoreMobs-state.log (on the SERVER's machine in multiplayer; the summary still comes back to you in chat).";

        public override CommandType Type => CommandType.Chat;

        public override void Action(CommandCaller caller, string input, string[] args)
        {
            if (args.Length == 0)
            {
                caller.Reply(Usage);
                return;
            }

            string sub = args[0].ToLower();
            if (!DebugCommands.PlayerFacing.Contains(sub))
            {
                // Point at the right command rather than just refusing — the split is new and the failure is
                // otherwise indistinguishable from a typo.
                caller.Reply(DebugCommands.IsKnown(sub)
                    ? $"[MMM] '{sub}' is a developer command — run /mmmdebug {sub}"
                    : Usage);
                return;
            }

            DebugCommands.Dispatch(caller, args, Usage);
        }
    }

    /// <summary>
    /// The developer command surface. A superset: everything on <see cref="MmmCommand"/> works here too, so
    /// there is one prefix to type while working rather than having to remember which side a subcommand
    /// landed on. Holds the diagnostics that write large reports, the trackers, and the world-altering test
    /// helpers (spawn / kill / boss).
    /// </summary>
    public class MmmDebugCommand : ModCommand
    {
        public override string Command => "mmmdebug";
        public override string Usage =>
            "/mmmdebug <info|version|counts|dump|dumpall|validate|modarrays|spawninfo|blocked|event|immune|hittest|track|perf [now]|slot <i>|spawn [town|enemy|critter|boss|rare|truffle|<typeId>] [amount]|boss [name]|kill|killall>";
        public override string Description => "Many More Mobs developer tools: full dumps, array validation, hit/perf tracking, and spawn testing. Reports go to ManyMoreMobs-state.log.";

        public override CommandType Type => CommandType.Chat;

        public override void Action(CommandCaller caller, string input, string[] args)
        {
            if (args.Length == 0)
            {
                caller.Reply(Usage);
                return;
            }

            DebugCommands.Dispatch(caller, args, Usage);
        }
    }

    public static class DebugCommands
    {
        /// <summary>
        /// Subcommands safe to give a bug reporter: read-only, no config requirement, no world changes.
        /// Everything not in here is developer-only and lives behind <c>/mmmdebug</c>.
        /// </summary>
        internal static readonly HashSet<string> PlayerFacing = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "info", "version", "counts", "dump", "blocked", "blockinfo", "spawninfo",
        };

        private static readonly HashSet<string> DeveloperOnly = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "dumpall", "dump_all", "validate", "modarrays", "event", "immune", "hittest",
            "track", "perf", "slot", "spawn", "boss", "kill", "killall",
        };

        /// <summary>Whether this is a real subcommand at all, so a wrong-surface hint isn't given for a typo.</summary>
        internal static bool IsKnown(string sub) => PlayerFacing.Contains(sub) || DeveloperOnly.Contains(sub);

        internal static void Dispatch(CommandCaller caller, string[] args, string usage)
        {
            switch (args[0].ToLower())
            {
                // The triage command — the one to give a non-technical reporter. Prints OK/WARN per subsystem
                // and names the specialised command to run next. See EngineDiagnostics.BuildInfoReport.
                case "info":
                {
                    string report = EngineDiagnostics.BuildInfoReport(caller.Player);
                    MmmLog.Dump("/mmm info", report);
                    foreach (string line in report.TrimEnd().Split('\n'))
                        caller.Reply(line.TrimEnd());
                    break;
                }

                case "counts":
                    PrintCounts(caller);
                    break;

                case "dump":
                {
                    string report = EngineDiagnostics.BuildStateReport();
                    MmmLog.Dump("/mmm dump", report);
                    foreach (string line in report.TrimEnd().Split('\n'))
                        caller.Reply(line.TrimEnd());
                    caller.Reply("[MMM] full dump written to ManyMoreMobs-state.log");
                    break;
                }

                case "dumpall":
                case "dump_all":
                {
                    // Deliberately NOT echoed to chat: this is one line per active NPC, up to the full cap.
                    // The summary block is what a human needs in the moment; the census is for reading back
                    // out of the log afterwards.
                    string report = EngineDiagnostics.BuildFullDumpReport(out int problems);
                    MmmLog.Dump("/mmmdebug dumpall", report);
                    foreach (string line in report.Split('\n'))
                    {
                        if (line.StartsWith("--- byType", StringComparison.Ordinal))
                            break;
                        caller.Reply(line.TrimEnd().Replace('\t', ' '));
                    }
                    caller.Reply(problems == 0
                        ? "[MMM] census written to ManyMoreMobs-state.log — no anomalies."
                        : $"[MMM] census written to ManyMoreMobs-state.log — {problems} ANOMALIES listed there.");
                    break;
                }

                // Separate from spawninfo on purpose — see EngineDiagnostics.BuildBlockedReport.
                case "blocked":
                case "blockinfo":
                {
                    string report = EngineDiagnostics.BuildBlockedReport(caller.Player);
                    MmmLog.Dump("/mmm blocked", report);
                    foreach (string line in report.TrimEnd().Split('\n'))
                        caller.Reply(line.TrimEnd());
                    break;
                }

                case "spawninfo":
                {
                    string report = EngineDiagnostics.BuildSpawnInfoReport(caller.Player);
                    MmmLog.Dump("/mmm spawninfo", report);
                    foreach (string line in report.TrimEnd().Split('\n'))
                        caller.Reply(line.TrimEnd());
                    break;
                }

                case "event":
                {
                    string report = EngineDiagnostics.BuildEventReport();
                    MmmLog.Dump("/mmmdebug event", report);
                    foreach (string line in report.TrimEnd().Split('\n'))
                        caller.Reply(line.TrimEnd());
                    break;
                }

                case "immune":
                {
                    string report = EngineDiagnostics.BuildImmunityReport(caller.Player);
                    MmmLog.Dump("/mmmdebug immune", report);
                    foreach (string line in report.TrimEnd().Split('\n'))
                        caller.Reply(line.TrimEnd());
                    break;
                }

                case "hittest":
                {
                    string report = EngineDiagnostics.BuildHitTestReport(caller.Player);
                    MmmLog.Dump("/mmmdebug hittest", report);
                    caller.Reply(report);
                    break;
                }

                case "track":
                {
                    // The tracker hangs off client-side projectile hooks keyed to Main.myPlayer. A Chat command
                    // runs on the SERVER in multiplayer, where there is no local player, so it would silently
                    // record nothing. Say so rather than hand back an empty log — a tracker that produces
                    // nothing reads as "no problem found" when it means "never measured".
                    if (Main.netMode != NetmodeID.SinglePlayer)
                    {
                        caller.Reply("[MMM] /mmmdebug track is single-player only — it hooks client-side projectile updates and would log nothing here.");
                        break;
                    }

                    string msg = HitTracker.Toggle();
                    caller.Reply("[MMM] " + msg);
                    break;
                }

                // Per-tick timing. Gated behind Debug Mode rather than being toggle-only like `track`,
                // because this one installs PreAI/PostAI timers on EVERY npc and projectile — the cost is
                // small but it is paid by entities the player never asked us to touch, so it should not be
                // reachable at all on an ordinary setup. Everything stays off until the config says otherwise.
                case "perf":
                {
                    if (ModContent.GetInstance<ManyMoreMobsConfig>()?.DebugMode != true)
                    {
                        caller.Reply("[MMM] /mmmdebug perf needs Debug Mode enabled (Mod Config -> Many More Mobs -> Debug).");
                        break;
                    }

                    // `now` snapshots mid-run without stopping — the point being to capture a bad moment
                    // while it is happening, rather than having to end the run to see anything.
                    if (args.Length > 1 && args[1].Equals("now", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!PerfTracker.Enabled)
                        {
                            caller.Reply("[MMM] perf sampling is not running — /mmmdebug perf to start it.");
                            break;
                        }
                        caller.Reply("[MMM] " + PerfTracker.WriteReport("/mmmdebug perf now (snapshot, still running)"));
                        break;
                    }

                    caller.Reply("[MMM] " + PerfTracker.Toggle());
                    break;
                }

                case "modarrays":
                {
                    string report = ModArrayScanner.Build(out int stale);
                    MmmLog.Dump("/mmmdebug modarrays", report);
                    caller.Reply(stale == 0
                        ? "[MMM] modarrays: every mod array found is sized for the raised cap. (details in ManyMoreMobs-state.log)"
                        : $"[MMM] modarrays: {stale} array(s) still sized for 200 — see ManyMoreMobs-state.log for which mod");
                    break;
                }

                case "validate":
                {
                    string report = EngineDiagnostics.BuildValidationReport(out int anomalies);
                    MmmLog.Dump("/mmmdebug validate", report);
                    caller.Reply(anomalies == 0
                        ? "[MMM] validate: no anomalies. (details in ManyMoreMobs-state.log)"
                        : $"[MMM] validate: {anomalies} ANOMALIES — see ManyMoreMobs-state.log");
                    break;
                }

                case "slot":
                {
                    if (args.Length < 2 || !int.TryParse(args[1], out int index))
                    {
                        caller.Reply("Usage: /mmmdebug slot <index>");
                        break;
                    }
                    string report = EngineDiagnostics.BuildSlotReport(index);
                    MmmLog.Dump($"/mmmdebug slot {index}", report);
                    foreach (string line in report.TrimEnd().Split('\n'))
                        caller.Reply(line.TrimEnd());
                    break;
                }

                case "spawn":
                {
                    string category = args.Length > 1 ? args[1].ToLower() : "town";
                    int amount = 1;
                    if (args.Length > 2 && int.TryParse(args[2], out int a))
                        amount = Math.Clamp(a, 1, 1000);
                    SpawnTest(caller, caller.Player, category, amount);
                    break;
                }

                case "boss":
                {
                    if (args.Length < 2)
                    {
                        caller.Reply("Usage: /mmmdebug boss <name>");
                        caller.Reply("Known: " + string.Join(", ", BossList));
                        break;
                    }
                    // Join the remaining words so "king slime" / "moon lord" work as well as "kingslime".
                    string name = string.Concat(args.Skip(1)).ToLower();
                    SpawnBoss(caller, caller.Player, name);
                    break;
                }

                case "kill":
                {
                    for (int i = 0; i < Main.npc.Length; i++)
                    {
                        NPC npc = Main.npc[i];
                        if (npc.active && npc.townNPC)
                        {
                            npc.StrikeInstantKill();
                            caller.Reply($"Killed {npc.GivenName}");
                            return;
                        }
                    }

                    caller.Reply("No living town NPC.");
                    break;
                }

                case "killall":
                {
                    bool found = false;
                    for (int i = 0; i < Main.npc.Length; i++)
                    {
                        NPC npc = Main.npc[i];
                        if (npc.active && npc.townNPC)
                        {
                            npc.StrikeInstantKill();
                            found = true;
                        }
                    }

                    caller.Reply(found ? "Killed all town NPCs." : "No living town NPC.");
                    break;
                }

                case "version":
                    PrintVersion(caller);
                    break;

                default:
                    caller.Reply(usage);
                    break;
            }
        }

        /// <summary>
        /// Reports which build is ACTUALLY running, and where.
        /// <para/>
        /// Exists because of a real diagnostic dead end: a player reported a bug that had already been fixed
        /// and released, and there was no way to tell from the outside whether they were running the fixed
        /// build. On a server the answer is not "whatever the player subscribed to" — tModLoader makes the
        /// SERVER's mod list authoritative and switches (or downloads to) the client to match it, so a stale
        /// server silently downgrades everyone connected to it. Since <c>CommandType.Chat</c> executes
        /// server-side in multiplayer, the version this prints IS the one the session is running.
        /// </summary>
        private static void PrintVersion(CommandCaller caller)
        {
            Mod mod = ModContent.GetInstance<ManyMoreMobs>();
            string where = Main.netMode switch
            {
                NetmodeID.SinglePlayer => "single-player",
                NetmodeID.MultiplayerClient => "multiplayer (reported by the SERVER — this is the version in force)",
                NetmodeID.Server => "server",
                _ => "unknown",
            };

            caller.Reply($"[MMM] Many More Mobs v{mod?.Version} — {where}");
            caller.Reply($"[MMM] tModLoader {ModLoader.versionedName}");

            var config = ModContent.GetInstance<ManyMoreMobsConfig>();
            if (config != null)
            {
                caller.Reply($"[MMM] mode={config.CapMode} total={config.EffectiveTotal} applied={MaxNpcCapRaise.AppliedCap} npc.Length={Main.npc.Length}");
                caller.Reply($"[MMM] experimental MP fixes: {(config.ExperimentalMultiplayerFixes ? "ON" : "OFF")}");
            }
        }

        private static void PrintCounts(CommandCaller caller)
        {
            var counts = CategoryCounts.Snapshot();
            var config = ModContent.GetInstance<ManyMoreMobsConfig>();
            var caps = config.GetEffectiveCaps();

            // Engine-state diagnostics. directMaxNPCs is a normal compiled read (can be JIT-baked to 200);
            // reflectionMaxNPCs reads the true field storage; npc.Length is the real array size.
            int directMaxNPCs = Main.maxNPCs;
            int reflectionMaxNPCs = (int)(typeof(Main)
                .GetField(nameof(Main.maxNPCs), BindingFlags.Public | BindingFlags.Static)
                ?.GetValue(null) ?? -1);

            caller.Reply($"[MMM] mode={config.CapMode} target={config.MaxNPCTotal} applied={MaxNpcCapRaise.AppliedCap}");
            caller.Reply($"[MMM] maxNPCs direct={directMaxNPCs} reflection={reflectionMaxNPCs} npc.Length={Main.npc.Length}");
            caller.Reply($"[MMM] effectiveTotal={config.EffectiveTotal}  caps T/B/C/E = {caps.town}/{caps.boss}/{caps.critter}/{caps.enemy}");
            caller.Reply($"[MMM] active   T/B/C/E = {counts.town}/{counts.boss}/{counts.critter}/{counts.enemy}");
        }

        private static void SpawnTest(CommandCaller caller, Player player, string category, int amount)
        {
            // Representative vanilla NPC per category. Enemy/critter spawns go through NewNpcGate, so they
            // respect the caps (a blocked spawn returns the failure slot and is counted below).
            int type;
            switch (category)
            {
                case "enemy": type = NPCID.Zombie; break;
                case "critter": type = NPCID.Bunny; break;
                case "boss": type = NPCID.KingSlime; break;
                case "town": type = NPCID.Guide; break;
                // A RARE critter (Prismatic Lacewing). Rare critters are exempt from the critter ceiling
                // because they gate boss summons — saturate the cap with `spawn critter 30`, then this must
                // still succeed. Testing that rule otherwise means waiting on Plantera + a natural spawn.
                case "rare": type = NPCID.EmpressButterfly; break;
                case "truffle": type = NPCID.TruffleWorm; break;
                default:
                    // Raw type id, so any NPC can be put through the gate without a new alias each time.
                    if (int.TryParse(category, out int rawType) && rawType > 0 && rawType < NPCLoader.NPCCount)
                    {
                        type = rawType;
                        break;
                    }
                    caller.Reply("Unknown category. Use: town | enemy | critter | boss | rare | truffle | <type id>");
                    return;
            }

            int ok = 0, fail = 0;
            for (int n = 0; n < amount; n++)
            {
                // Scatter a crowd around the player instead of stacking them on one point; keep a single
                // spawn right above the player.
                int ox = amount > 1 ? Main.rand.Next(-500, 501) : 0;
                int oy = amount > 1 ? Main.rand.Next(-250, 51) : -64;

                int index = NPC.NewNPC(
                    player.GetSource_Misc("MMM_DebugSpawn"),
                    (int)player.Center.X + ox,
                    (int)player.Center.Y + oy,
                    type);

                if (index < 0 || index >= EngineState.NpcCap || !Main.npc[index].active)
                    fail++;
                else
                    ok++;
            }

            string label = Lang.GetNPCNameValue(type);
            if (amount == 1)
                caller.Reply(ok == 1
                    ? $"Spawned {label} ({category})."
                    : $"{category} spawn blocked or failed (cap reached / no free slot).");
            else
                caller.Reply($"{category} ({label}): spawned {ok}/{amount}{(fail > 0 ? $", {fail} blocked (cap reached)" : "")}.");
        }

        private static void SpawnBoss(CommandCaller caller, Player player, string name)
        {
            int type = ResolveBoss(name);
            if (type <= 0)
            {
                caller.Reply($"Unknown boss '{name}'.");
                caller.Reply("Known: " + string.Join(", ", BossList));
                return;
            }

            // The Twins are two separate NPCs; spawn both for a real fight.
            if (name == "twins")
            {
                NPC.SpawnOnPlayer(player.whoAmI, NPCID.Retinazer);
                NPC.SpawnOnPlayer(player.whoAmI, NPCID.Spazmatism);
                caller.Reply("Spawned The Twins.");
                return;
            }

            // SpawnOnPlayer is the vanilla boss-summon path (handles music/broadcast). A few bosses (Wall of
            // Flesh, Moon Lord) have unusual spawn sequences and may behave oddly when forced this way.
            NPC.SpawnOnPlayer(player.whoAmI, type);
            caller.Reply($"Spawned {Lang.GetNPCNameValue(type)} (type {type}).");
        }

        private static int ResolveBoss(string name)
        {
            if (BossAliases.TryGetValue(name, out int type))
                return type;

            // Fallback: match any NPCID constant by name (case-insensitive), e.g. "plantera", "golem".
            FieldInfo f = typeof(NPCID).GetField(name, BindingFlags.Public | BindingFlags.Static | BindingFlags.IgnoreCase);
            if (f != null && f.FieldType == typeof(short))
                return (short)f.GetValue(null);

            return -1;
        }

        // Friendly aliases -> NPCID. Several short aliases per boss for convenience.
        private static readonly Dictionary<string, int> BossAliases = new()
        {
            ["kingslime"] = NPCID.KingSlime, ["ks"] = NPCID.KingSlime,
            ["eyeofcthulhu"] = NPCID.EyeofCthulhu, ["eoc"] = NPCID.EyeofCthulhu, ["eye"] = NPCID.EyeofCthulhu,
            ["eaterofworlds"] = NPCID.EaterofWorldsHead, ["eow"] = NPCID.EaterofWorldsHead, ["eater"] = NPCID.EaterofWorldsHead,
            ["brainofcthulhu"] = NPCID.BrainofCthulhu, ["boc"] = NPCID.BrainofCthulhu, ["brain"] = NPCID.BrainofCthulhu,
            ["queenbee"] = NPCID.QueenBee, ["qb"] = NPCID.QueenBee,
            ["skeletron"] = NPCID.SkeletronHead,
            ["deerclops"] = NPCID.Deerclops,
            ["wallofflesh"] = NPCID.WallofFlesh, ["wof"] = NPCID.WallofFlesh,
            ["queenslime"] = NPCID.QueenSlimeBoss, ["qs"] = NPCID.QueenSlimeBoss,
            ["destroyer"] = NPCID.TheDestroyer,
            ["twins"] = NPCID.Retinazer,
            ["skeletronprime"] = NPCID.SkeletronPrime, ["prime"] = NPCID.SkeletronPrime,
            ["plantera"] = NPCID.Plantera,
            ["golem"] = NPCID.Golem,
            ["empressoflight"] = NPCID.HallowBoss, ["empress"] = NPCID.HallowBoss, ["eol"] = NPCID.HallowBoss,
            ["dukefishron"] = NPCID.DukeFishron, ["duke"] = NPCID.DukeFishron, ["fishron"] = NPCID.DukeFishron,
            ["lunaticcultist"] = NPCID.CultistBoss, ["cultist"] = NPCID.CultistBoss,
            ["moonlord"] = NPCID.MoonLordCore, ["ml"] = NPCID.MoonLordCore,
        };

        // Primary names shown in the help/usage list.
        private static readonly string[] BossList =
        {
            "kingslime", "eoc", "eow", "boc", "queenbee", "skeletron", "deerclops", "wof",
            "queenslime", "destroyer", "twins", "prime", "plantera", "golem", "empress",
            "duke", "cultist", "moonlord",
        };
    }
}
