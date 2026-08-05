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
        /// This is a whole family, twenty sites across nine methods, and it is arguably worse than the packet
        /// guards: the NPC exists and behaves on the server while <b>no client is ever told it exists</b>. It
        /// is why the Old One's Army Dark Mage "doesn't spawn" in multiplayer — it spawns fine, into an
        /// expanded slot, and the broadcast is skipped. The same silence hits enemies that split on death
        /// (<c>checkDead</c>), adds summoned by enemy AI (<c>VanillaAI_Inner</c>) and on-hit spawns
        /// (<c>VanillaHitEffect</c>).
        /// <para/>
        /// Purely a multiplayer defect — the guarded branch only runs on a dedicated server, which is why none
        /// of it reproduces in single-player.
        /// </summary>
        private void ApplyServerBroadcastGuards()
        {
            Type dd2 = typeof(Main).Assembly.GetType("Terraria.GameContent.Events.DD2Event");

            var targets = new (Type type, string name, BindingFlags flags)[]
            {
                // Old One's Army gate spawns — every wave enemy AND the Dark Mage / Ogre minibosses.
                (dd2, "Difficulty_1_SpawnMonsterFromGate", BindingFlags.NonPublic | BindingFlags.Static),
                (dd2, "Difficulty_2_SpawnMonsterFromGate", BindingFlags.NonPublic | BindingFlags.Static),
                (dd2, "Difficulty_3_SpawnMonsterFromGate", BindingFlags.NonPublic | BindingFlags.Static),
                // Enemies that split or spawn children on death (Eater of Worlds, slimes, ...).
                (typeof(NPC), "checkDead", BindingFlags.Public | BindingFlags.Instance),
                // Adds summoned by enemy AI, and on-hit spawns.
                (typeof(NPC), "VanillaAI_Inner", BindingFlags.NonPublic | BindingFlags.Instance),
                (typeof(NPC), "VanillaHitEffect", BindingFlags.NonPublic | BindingFlags.Instance),
                (typeof(NPC), "AI_121_QueenSlime", BindingFlags.NonPublic | BindingFlags.Instance),
                (typeof(NPC), "SpawnFaelings", BindingFlags.Public | BindingFlags.Static),
                (typeof(NPC), "SpawnBoss", BindingFlags.Public | BindingFlags.Static),
            };

            foreach (var (type, name, flags) in targets)
            {
                MethodInfo m = type?.GetMethod(name, flags);
                if (m == null)
                {
                    Mod.Logger.Warn($"[MMM/MP] broadcast-guard target not found: {name} (skipped).");
                    continue;
                }

                try
                {
                    MonoModHooks.Modify(m, Patch_ServerBroadcastGuards);
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
        /// </summary>
        private static void Patch_ServerBroadcastGuards(ILContext il)
        {
            MethodInfo limit = typeof(MpNetGuards).GetMethod(nameof(MpNetGuards.IndexLimit),
                BindingFlags.Public | BindingFlags.Static)
                ?? throw new Exception("MpNetGuards.IndexLimit not resolvable");

            var instrs = il.Instrs;
            int patched = 0;

            for (int i = 0; i < instrs.Count; i++)
            {
                if (!instrs[i].MatchLdcI4(200))
                    continue;

                bool afterNetModeRead = false;
                for (int b = Math.Max(0, i - 5); b < i; b++)
                {
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

                bool beforeSendData = false;
                for (int f = i + 1; f < Math.Min(instrs.Count, i + 16); f++)
                {
                    if (instrs[f].Operand is Mono.Cecil.MethodReference mr && mr.Name == "SendData")
                    {
                        beforeSendData = true;
                        break;
                    }
                }
                if (!beforeSendData)
                    continue;

                instrs[i].OpCode = OpCodes.Call;
                instrs[i].Operand = il.Import(limit);
                patched++;
            }

            if (patched == 0)
                throw new Exception("no server-broadcast guard matched the netMode/SendData shape");

            ModContent.GetInstance<ManyMoreMobs>()?.Logger.Info(
                $"[MMM/MP] widened {patched} server broadcast guard(s) in {il.Method.Name}.");
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
                    if (!c.TryGotoPrev(i => i.MatchLdcI4(200)))
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
                if (!c.TryGotoPrev(i => i.MatchLdcI4(200)))
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
                if (!c.TryGotoNext(i => i.MatchLdcI4(400)))
                    throw new Exception("preceding item-loop bound 400 not found");
                if (!c.TryGotoNext(i => i.MatchLdcI4(200)))
                    throw new Exception("NPC loop bound 200 not found");
                Widen(c);
            });
        }
    }
}
