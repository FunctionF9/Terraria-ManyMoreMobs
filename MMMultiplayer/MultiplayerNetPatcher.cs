using System;
using System.Reflection;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using Terraria;
using Terraria.ModLoader;

namespace ManyMoreMobs.MMMultiplayer
{
    /// <summary>
    /// Experimental multiplayer support: widens the hardcoded <c>200</c> NPC-slot bounds checks inside
    /// <see cref="MessageBuffer.GetData"/> so packets addressed to expanded-zone NPCs aren't discarded.
    /// <para/>
    /// This registers its own hooks rather than going through the single-player <c>EngineILPatcher</c>, so the
    /// two can be worked on — or thrown away — independently. Nothing outside this folder refers to it.
    /// <para/>
    /// <b>Why these sites and not a blanket pass:</b> <c>GetData</c> is one ~4,500-line method handling every
    /// packet in the game, so a blanket "replace every 200" would rewrite unrelated constants (item counts,
    /// tile bounds, buff caps). Each site is anchored on a call that appears exactly ONCE in the method, then
    /// walked back to its own literal. Every site is patched in its own try/catch: one missing anchor is
    /// logged and skipped, never fatal, and never blocks the others.
    /// <para/>
    /// <b>Untested by the author.</b> Multiplayer is not supported; these are best-effort fixes for reported
    /// symptoms, gated behind <c>ExperimentalMultiplayerFixes</c> so a host can switch them off live.
    /// </summary>
    public class MultiplayerNetPatcher : ModSystem
    {
        public override void OnModLoad()
        {
            MethodInfo getData = typeof(MessageBuffer).GetMethod(nameof(MessageBuffer.GetData),
                BindingFlags.Public | BindingFlags.Instance);

            if (getData == null)
            {
                Mod.Logger.Error("[MMM/MP] MessageBuffer.GetData not found — multiplayer fixes not applied.");
                return;
            }

            try
            {
                MonoModHooks.Modify(getData, Patch_GetData);
                Mod.Logger.Info("[MMM/MP] IL patched: MessageBuffer.GetData (packet slot bounds).");
            }
            catch (Exception e)
            {
                Mod.Logger.Error($"[MMM/MP] MessageBuffer.GetData patch FAILED (skipped; game stays stable): {e.Message}");
            }

            ApplyServerBroadcastGuards();
        }

        /// <summary>
        /// Widen the <c>if (Main.netMode == 2 &amp;&amp; slot &lt; 200) NetMessage.SendData(23, ...)</c> pattern —
        /// "the server just spawned an NPC; tell the clients about it, but only if it landed below slot 200".
        /// <para/>
        /// This is a whole family — <b>21 sites across 10 methods</b> — and it is arguably worse than the
        /// packet guards: the NPC exists and behaves on the server while no client is told it just appeared.
        /// It hits the Old One's Army gate spawns, the ordinary natural-spawn path (<c>SpawnNPC</c>), enemies
        /// that split on death (<c>checkDead</c>), adds summoned by enemy AI (<c>VanillaAI_Inner</c>) and
        /// on-hit spawns (<c>VanillaHitEffect</c>).
        /// <para/>
        /// <b>Severity, honestly:</b> this delays rather than erases. A missed spawn broadcast is usually
        /// recovered by the per-NPC <c>netUpdate</c> sync at the end of <c>NPC.UpdateNPC</c>, which carries no
        /// slot guard — so the NPC shows up on clients as soon as its AI next flags itself dirty. What that
        /// leaves is a window of invisibility whose length depends entirely on how chatty the NPC's AI is,
        /// which is exactly the shape of an intermittent, unreproducible report.
        /// <para/>
        /// Purely a multiplayer defect — the guarded branch only runs on a dedicated server, which is why none
        /// of it reproduces in single-player.
        /// </summary>
        private void ApplyServerBroadcastGuards()
        {
            Type dd2 = typeof(Main).Assembly.GetType("Terraria.GameContent.Events.DD2Event");

            // `sentinels` = also widen the `int slot = 200;` initializer the guard compares against. Enabled
            // ONLY for the Old One's Army gates, where that sentinel does nothing but feed this guard.
            // Deliberately OFF elsewhere: in NPC.SpawnNPC the same sentinel is also used to index Main.npc, so
            // widening it is a cap correctness fix rather than a network one and belongs to EngineILPatcher,
            // unconditionally. If both claimed it, whichever patcher happened to load first would win — and
            // when that was the multiplayer one, single-player behaviour would start depending on the
            // ExperimentalMultiplayerFixes toggle. (NPC.SpawnBoss also opens `int num = 200;`, but both
            // branches assign before any read, so that one is a dead store and needs nobody.)
            var targets = new (Type type, string name, BindingFlags flags, bool sentinels)[]
            {
                // Old One's Army gate spawns — every wave enemy AND the Dark Mage / Ogre minibosses.
                (dd2, "Difficulty_1_SpawnMonsterFromGate", BindingFlags.NonPublic | BindingFlags.Static, true),
                (dd2, "Difficulty_2_SpawnMonsterFromGate", BindingFlags.NonPublic | BindingFlags.Static, true),
                (dd2, "Difficulty_3_SpawnMonsterFromGate", BindingFlags.NonPublic | BindingFlags.Static, true),
                // Enemies that split or spawn children on death (Eater of Worlds, slimes, ...).
                (typeof(NPC), "checkDead", BindingFlags.Public | BindingFlags.Instance, false),
                // Adds summoned by enemy AI, and on-hit spawns.
                (typeof(NPC), "VanillaAI_Inner", BindingFlags.NonPublic | BindingFlags.Instance, false),
                (typeof(NPC), "VanillaHitEffect", BindingFlags.NonPublic | BindingFlags.Instance, false),
                (typeof(NPC), "AI_121_QueenSlime", BindingFlags.NonPublic | BindingFlags.Instance, false),
                (typeof(NPC), "SpawnFaelings", BindingFlags.Public | BindingFlags.Static, false),
                (typeof(NPC), "SpawnBoss", BindingFlags.Public | BindingFlags.Static, false),
                // The ordinary natural-spawn broadcast. Missed in the first pass and the widest-reaching of
                // the lot: every naturally spawned enemy goes through it, so without it a client only learns
                // about a high-slot spawn once its AI happens to set netUpdate.
                (typeof(NPC), "SpawnNPC", BindingFlags.Public | BindingFlags.Static, false),
            };

            foreach (var (type, name, flags, sentinels) in targets)
            {
                MethodInfo m = type?.GetMethod(name, flags);
                if (m == null)
                {
                    Mod.Logger.Warn($"[MMM/MP] broadcast-guard target not found: {name} (skipped).");
                    continue;
                }

                try
                {
                    MonoModHooks.Modify(m, il => Patch_ServerBroadcastGuards(il, sentinels));
                    Mod.Logger.Info($"[MMM/MP] IL patched: {name} (server broadcast guards).");
                }
                catch (Exception e)
                {
                    Mod.Logger.Warn($"[MMM/MP] broadcast guards in {name} not patched (skipped): {e.Message}");
                }
            }
        }

        /// <summary>
        /// Structural matcher for the broadcast guard. These methods are far too large to blanket-replace
        /// every <c>200</c> in, so a literal only qualifies when it is genuinely part of this pattern: a
        /// <c>Main.netMode</c> read just before it, and a <c>NetMessage.SendData</c> call just after. A stray
        /// 200 used as a distance, a tile bound or a damage figure matches neither and is left alone.
        /// <para/>
        /// <b>Both scans skip <c>nop</c>.</b> This build's IL is <c>nop</c>-padded, and the guard emits as
        /// <c>ldsfld netMode · ldc.i4.2 · bne.un · ldloc · nop · nop · ldc.i4 200</c> — the netMode read is
        /// SIX instructions back. The first version of this matcher counted raw instructions through a window
        /// of five, so the padding pushed it just out of reach: every site in <c>VanillaHitEffect</c> and two
        /// of the five in <c>VanillaAI_Inner</c> silently failed to match while the un-padded ones succeeded.
        /// Counting real instructions instead of raw ones is what makes the shape, not the layout, decide.
        /// </summary>
        private static void Patch_ServerBroadcastGuards(ILContext il, bool widenSentinels)
        {
            MethodInfo limit = typeof(MpNetGuards).GetMethod(nameof(MpNetGuards.IndexLimit),
                BindingFlags.Public | BindingFlags.Static)
                ?? throw new Exception("MpNetGuards.IndexLimit not resolvable");

            var instrs = il.Instrs;
            int patched = 0;
            int sentinels = 0;

            void Widen(int index)
            {
                instrs[index].OpCode = OpCodes.Call;
                instrs[index].Operand = il.Import(limit);
            }

            for (int i = 0; i < instrs.Count; i++)
            {
                if (!EngineILPatcher.SafeLdcI4(instrs[i], 200))
                    continue;

                // Backward: Main.netMode read within 6 real instructions (ldloc, branch, ldc.i4.2, ldsfld).
                bool afterNetModeRead = false;
                for (int b = i - 1, seen = 0; b >= 0 && seen < 6; b--)
                {
                    if (instrs[b].OpCode == OpCodes.Nop)
                        continue;
                    seen++;
                    if (instrs[b].OpCode == OpCodes.Ldsfld
                        && instrs[b].Operand is Mono.Cecil.FieldReference fr
                        && fr.Name == nameof(Main.netMode))
                    {
                        afterNetModeRead = true;
                        break;
                    }
                }
                if (!afterNetModeRead)
                    continue;

                // Forward: SendData within 20 real instructions (the branch plus its eleven arguments).
                bool beforeSendData = false;
                for (int f = i + 1, seen = 0; f < instrs.Count && seen < 20; f++)
                {
                    if (instrs[f].OpCode == OpCodes.Nop)
                        continue;
                    seen++;
                    if (instrs[f].Operand is Mono.Cecil.MethodReference mr && mr.Name == "SendData")
                    {
                        beforeSendData = true;
                        break;
                    }
                }
                if (!beforeSendData)
                    continue;

                Widen(i);
                patched++;
                if (widenSentinels)
                    sentinels += WidenSentinelInitializer(instrs, i, Widen);
            }

            if (patched == 0)
                throw new Exception("no server-broadcast guard matched the netMode/SendData shape");

            ModContent.GetInstance<ManyMoreMobs>()?.Logger.Info(
                $"[MMM/MP] widened {patched} server broadcast guard(s)"
                + (sentinels > 0 ? $" + {sentinels} 'nothing spawned' sentinel(s)" : string.Empty)
                + $" in {il.Method.Name}.");
        }

        /// <summary>
        /// Widens the <c>int slot = 200;</c> initializer that some of these guards compare against.
        /// <para/>
        /// The Old One's Army gate spawners do <c>int num4 = 200; switch (wave) { ... num4 = NewNPC(...) }</c>
        /// and then <c>if (netMode == 2 &amp;&amp; num4 &lt; 200)</c>. That literal is doing double duty: it is
        /// the slot bound AND the "nothing spawned this tick" sentinel. Widening only the comparison leaves
        /// <c>200 &lt; cap</c> reading true on a tick where nothing spawned, so the server broadcasts slot 200
        /// to everyone for no reason — harmless in content (the sync is truthful) but it is a packet per idle
        /// gate tick, and this mod can drive the lane spawn rate down to 8 frames.
        /// <para/>
        /// Matched precisely rather than by proximity: take the local the guard loads, then widen only a
        /// <c>ldc.i4 200</c> stored straight into that same local. A method whose slot comes directly out of
        /// <c>NewNPC</c> has no such pair and is left completely alone.
        /// </summary>
        private static int WidenSentinelInitializer(
            System.Collections.Generic.IList<Instruction> instrs, int guard, Action<int> widen)
        {
            // The guard's operand: the last real instruction before the literal, which loads the slot local.
            Instruction load = null;
            for (int b = guard - 1; b >= 0; b--)
            {
                if (instrs[b].OpCode == OpCodes.Nop)
                    continue;
                load = instrs[b];
                break;
            }

            int local = LocalIndex(load, isStore: false);
            if (local < 0)
                return 0;

            int widened = 0;
            for (int i = 0; i < instrs.Count; i++)
            {
                if (i == guard || !EngineILPatcher.SafeLdcI4(instrs[i], 200))
                    continue;

                Instruction next = null;
                for (int f = i + 1; f < instrs.Count; f++)
                {
                    if (instrs[f].OpCode == OpCodes.Nop)
                        continue;
                    next = instrs[f];
                    break;
                }

                if (LocalIndex(next, isStore: true) != local)
                    continue;

                widen(i);
                widened++;
            }

            return widened;
        }

        /// <summary>Local-variable index for a ldloc/stloc in any of its encodings, or -1 if not one.</summary>
        private static int LocalIndex(Instruction instr, bool isStore)
        {
            if (instr == null)
                return -1;

            OpCode op = instr.OpCode;

            if (isStore)
            {
                if (op == OpCodes.Stloc_0) return 0;
                if (op == OpCodes.Stloc_1) return 1;
                if (op == OpCodes.Stloc_2) return 2;
                if (op == OpCodes.Stloc_3) return 3;
                if (op != OpCodes.Stloc && op != OpCodes.Stloc_S) return -1;
            }
            else
            {
                if (op == OpCodes.Ldloc_0) return 0;
                if (op == OpCodes.Ldloc_1) return 1;
                if (op == OpCodes.Ldloc_2) return 2;
                if (op == OpCodes.Ldloc_3) return 3;
                if (op != OpCodes.Ldloc && op != OpCodes.Ldloc_S) return -1;
            }

            return instr.Operand is Mono.Cecil.Cil.VariableDefinition v ? v.Index : -1;
        }

        private static void Patch_GetData(ILContext il)
        {
            MethodInfo limit = typeof(MpNetGuards).GetMethod(nameof(MpNetGuards.IndexLimit),
                BindingFlags.Public | BindingFlags.Static)
                ?? throw new Exception("MpNetGuards.IndexLimit not resolvable");

            // Turn the `ldc.i4 200` the cursor sits before into `call MpNetGuards.IndexLimit()` — same stack
            // effect (pushes one int), so the surrounding comparison is untouched.
            void Widen(ILCursor c)
            {
                c.Next.OpCode = OpCodes.Call;
                c.Next.Operand = il.Import(limit);
            }

            bool Calls(Instruction i, string name) => i.Operand is Mono.Cecil.MethodReference mr && mr.Name == name;

            void Site(string name, Action act)
            {
                try { act(); }
                catch (Exception e) { ModContent.GetInstance<ManyMoreMobs>()?.Logger.Warn($"[MMM/MP] site '{name}' not patched (skipped): {e.Message}"); }
            }

            // Anchor on a once-only call, then walk BACK to that guard's own literal.
            void GuardBefore(string anchorCall, string label)
            {
                Site(label, () =>
                {
                    var c = new ILCursor(il);
                    if (!c.TryGotoNext(i => Calls(i, anchorCall)))
                        throw new Exception($"anchor {anchorCall} not found");
                    if (!c.TryGotoPrev(i => EngineILPatcher.SafeLdcI4(i, 200)))
                        throw new Exception("bound literal 200 not found");
                    Widen(c);
                });
            }

            // ── packet 70, CatchNPC ─────────────────────────────────────────────────────────────────────
            // `if (num2 < 200 && num2 >= 0) NPC.CatchNPC(num2, who);`
            // THE reported bug. In multiplayer a client can't catch anything itself: it sets the NPC inactive
            // locally (the critter visibly enters the net) and asks the server to perform the real catch. The
            // server drops the request for any expanded-slot critter, then its next sync re-activates the NPC
            // — the critter "goes into the net and right back out of it".
            GuardBefore("CatchNPC", "packet 70 — critter catching");

            // ── packet 131, NPC immunity sync ───────────────────────────────────────────────────────────
            // `nPC4 = ((num92 >= 200) ? new NPC() : Main.npc[num92]); ... nPC4.GetImmuneTime(fromWho, time);`
            // For an expanded-slot NPC the update is applied to a THROWAWAY NPC and lost, so clients never
            // learn the real invulnerability window. Strong candidate for the "enemy won't take damage"
            // reports that only ever appeared in multiplayer.
            GuardBefore("GetImmuneTime", "packet 131 — NPC i-frame sync");

            // ── packet 137, buff removal ────────────────────────────────────────────────────────────────
            // `if (num61 >= 0 && num61 < 200) Main.npc[num61].RequestBuffRemoval(...)` — debuffs can't be
            // cleared from expanded-slot enemies.
            GuardBefore("RequestBuffRemoval", "packet 137 — NPC buff removal");

            // ── packet 92, coin/extraValue ping ─────────────────────────────────────────────────────────
            // `if (num223 >= 0 && num223 <= 200)`. Note this one is INCLUSIVE in vanilla, so widening leaves
            // it `<= cap`, which permits the dummy index — harmless, Main.npc is cap+1 long, and it mirrors
            // vanilla's own off-by-one rather than silently changing behaviour.
            GuardBefore("moneyPing", "packet 92 — coin value ping");

            // ── packet 60, anti-cheat boot ──────────────────────────────────────────────────────────────
            // `if (num13 >= 200) { NetMessage.BootPlayer(whoAmI, "Net.CheatingInvalid"); }` — a high slot index
            // here KICKS the player as a suspected cheater. Town NPCs live low so it shouldn't trigger, but
            // the failure mode is severe and baffling enough to be worth neutralising defensively.
            Site("packet 60 — anti-cheat boot guard", () =>
            {
                var c = new ILCursor(il);
                if (!c.TryGotoNext(i => i.MatchLdstr("Net.CheatingInvalid")))
                    throw new Exception("anchor \"Net.CheatingInvalid\" not found");
                if (!c.TryGotoPrev(i => EngineILPatcher.SafeLdcI4(i, 200)))
                    throw new Exception("bound literal 200 not found");
                Widen(c);
            });

            // ── packet 8, initial world sync on join ────────────────────────────────────────────────────
            // `for (i = 0; i < 200; i++) if (Main.npc[i].active) TrySendData(23, ...)` — the bulk NPC handoff
            // to a joining player only covers slots 0-199. Expanded-slot NPCs do arrive eventually via their
            // ordinary per-NPC updates, so this is a slow/incomplete join rather than permanent invisibility,
            // but an idle NPC can stay unknown to that client for a long time.
            // Anchored between the item loop (bound 400) that precedes it and its own bound.
            Site("packet 8 — initial NPC sync on join", () =>
            {
                var c = new ILCursor(il);
                if (!c.TryGotoNext(i => EngineILPatcher.SafeLdcI4(i, 400)))
                    throw new Exception("preceding item-loop bound 400 not found");
                if (!c.TryGotoNext(i => EngineILPatcher.SafeLdcI4(i, 200)))
                    throw new Exception("NPC loop bound 200 not found");
                Widen(c);
            });
        }
    }
}
