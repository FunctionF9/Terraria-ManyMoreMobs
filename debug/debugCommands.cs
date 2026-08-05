using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace ManyMoreMobs
{
    public class DebugCommands : ModCommand
    {
        public override string Command => "debugnpc";
        public override string Usage =>
            "/debugnpc <counts|dump|validate|spawninfo|event|immune|hittest|track|slot <i>|spawn [town|enemy|critter|boss|rare|truffle|<typeId>] [amount]|boss [name]|kill|killall>";
        public override string Description => "Many More Mobs debug: inspect counts/state/spawn rate, validate arrays, test spawning. Full reports go to ManyMoreMobs-state.log (on the SERVER's machine in multiplayer; the summary still comes back to you in chat).";

        public override CommandType Type => CommandType.Chat;

        public override void Action(CommandCaller caller, string input, string[] args)
        {
            if (args.Length == 0)
            {
                caller.Reply(Usage);
                return;
            }

            switch (args[0].ToLower())
            {
                case "counts":
                    PrintCounts(caller);
                    break;

                case "dump":
                {
                    string report = EngineDiagnostics.BuildStateReport();
                    MmmLog.Dump("/debugnpc dump", report);
                    foreach (string line in report.TrimEnd().Split('\n'))
                        caller.Reply(line.TrimEnd());
                    caller.Reply("[MMM] full dump written to ManyMoreMobs-state.log");
                    break;
                }

                case "spawninfo":
                {
                    string report = EngineDiagnostics.BuildSpawnInfoReport(caller.Player);
                    MmmLog.Dump("/debugnpc spawninfo", report);
                    foreach (string line in report.TrimEnd().Split('\n'))
                        caller.Reply(line.TrimEnd());
                    break;
                }

                case "event":
                {
                    string report = EngineDiagnostics.BuildEventReport();
                    MmmLog.Dump("/debugnpc event", report);
                    foreach (string line in report.TrimEnd().Split('\n'))
                        caller.Reply(line.TrimEnd());
                    break;
                }

                case "immune":
                {
                    string report = EngineDiagnostics.BuildImmunityReport(caller.Player);
                    MmmLog.Dump("/debugnpc immune", report);
                    foreach (string line in report.TrimEnd().Split('\n'))
                        caller.Reply(line.TrimEnd());
                    break;
                }

                case "hittest":
                {
                    string report = EngineDiagnostics.BuildHitTestReport(caller.Player);
                    MmmLog.Dump("/debugnpc hittest", report);
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
                        caller.Reply("[MMM] /debugnpc track is single-player only — it hooks client-side projectile updates and would log nothing here.");
                        break;
                    }

                    string msg = HitTracker.Toggle();
                    caller.Reply("[MMM] " + msg);
                    break;
                }

                case "validate":
                {
                    string report = EngineDiagnostics.BuildValidationReport(out int anomalies);
                    MmmLog.Dump("/debugnpc validate", report);
                    caller.Reply(anomalies == 0
                        ? "[MMM] validate: no anomalies. (details in ManyMoreMobs-state.log)"
                        : $"[MMM] validate: {anomalies} ANOMALIES — see ManyMoreMobs-state.log");
                    break;
                }

                case "slot":
                {
                    if (args.Length < 2 || !int.TryParse(args[1], out int index))
                    {
                        caller.Reply("Usage: /debugnpc slot <index>");
                        break;
                    }
                    string report = EngineDiagnostics.BuildSlotReport(index);
                    MmmLog.Dump($"/debugnpc slot {index}", report);
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
                        caller.Reply("Usage: /debugnpc boss <name>");
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

                default:
                    caller.Reply(Usage);
                    break;
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
