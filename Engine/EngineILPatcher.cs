using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using Terraria;
using Terraria.ModLoader;

namespace ManyMoreMobs
{
    /// <summary>
    /// IL surgery that makes the engine honor a raised <c>Main.maxNPCs</c> instead of the hardcoded 200.
    /// Each patch is <b>anchored</b> to a unique, stable instruction (a method call or field store) and
    /// touches exactly one literal, rather than blindly replacing every <c>200</c> in a method — large
    /// methods like <c>DoUpdateInWorld</c> contain unrelated <c>200</c>s that must not be touched.
    /// <para/>
    /// Every patch is wrapped so a failure to find its anchor is logged and skipped rather than crashing the
    /// load — the cap raise is then incomplete (some NPCs ≥200 may not update/draw), but the game stays up.
    /// Detours are registered via <see cref="MonoModHooks"/>, which auto-undoes them on mod unload.
    /// <para/>
    /// Patched (all verified against this build by decompilation):
    /// <list type="number">
    /// <item><c>NPC.GetAvailableNPCSlot</c>: <c>int t = 200</c> → <c>Main.maxNPCs</c> (else slots ≥200 are never allocated)</item>
    /// <item><c>NPC.NewNPC</c>: failure <c>return 200</c> → <c>return Main.maxNPCs</c> (correct dummy slot)</item>
    /// <item><c>Main.DoUpdateInWorld</c>: main <c>npc[l].UpdateNPC(l)</c> loop bound 200 → maxNPCs (else ≥200 frozen)</item>
    /// <item><c>Main.DrawNPCs</c>: outer <c>num = 199</c> loop init → maxNPCs - 1 (else ≥200 invisible)</item>
    /// <item><c>Projectile..ctor</c>: <c>localNPCImmunity = new int[200]</c> → <c>new int[maxNPCs]</c> (else projectiles crash hitting ≥200)</item>
    /// <item><c>Player..ctor</c>: <c>meleeNPCHitCooldown = new int[200]</c> → <c>new int[maxNPCs]</c> (else melee crashes hitting ≥200)</item>
    /// </list>
    /// </summary>
    internal static class EngineILPatcher
    {
        // We inject reads of OUR mutable field, not Main.maxNPCs: the JIT constant-folds the readonly
        // Main.maxNPCs back to 200, which silently defeats every patch. EngineState.NpcCap is a plain
        // mutable static, so the live value (750) is always read.
        private static readonly FieldInfo NpcCapField =
            typeof(EngineState).GetField(nameof(EngineState.NpcCap), BindingFlags.Public | BindingFlags.Static);

        private static readonly FieldInfo MaxNPCsField =
            typeof(Main).GetField(nameof(Main.maxNPCs), BindingFlags.Public | BindingFlags.Static);

        // Captured so manipulators that patch many independent sites (e.g. the per-item spawn modifiers) can
        // log a skipped site without aborting the whole patch.
        internal static Mod ModRef;

        /// <summary>
        /// Registers every IL patch, and cannot take the mod down with it.
        /// <para/>
        /// <b>Why the guard exists.</b> <see cref="Apply"/> already try/catches the patching step, so a
        /// manipulator that throws only loses its own patch. But the <c>MethodBase</c> argument is evaluated
        /// BEFORE <c>Apply</c> is entered, so anything thrown while RESOLVING a method never reaches that
        /// catch. <c>GetMethod(name, flags)</c> throws <c>AmbiguousMatchException</c> the moment an engine
        /// update adds an overload to any of the ~80 names resolved here — which is not hypothetical, it is
        /// exactly what <c>NPC.StrikeNPC</c> does today. This runs inside <c>OnModLoad</c>, so an escape does
        /// not skip one patch: it fails the whole mod's load.
        /// <para/>
        /// Catching here degrades that to "the patches registered so far stay, the rest do not, and the log
        /// and <c>/mmm info</c> both say so loudly." A partly-patched engine is a bad state, but it is a
        /// reportable one, and it is strictly better than a mod that will not load at all.
        /// </summary>
        public static void ApplyAll(Mod mod)
        {
            ModRef = mod;
            try
            {
                ApplyAllCore(mod);
            }
            catch (Exception e)
            {
                RegistrationAborted = e.GetType().Name + ": " + e.Message;
                PatchesFailed++;
                Record("registration aborted partway");
                mod.Logger.Error(
                    $"[MMM] PATCH REGISTRATION ABORTED partway through: {e}. Every patch registered before " +
                    "this point is active and the rest are not, so the engine is only partly widened. This " +
                    "usually means a tModLoader update changed a method this mod resolves by name. Please " +
                    "report this log.");
            }
        }

        /// <summary>Non-null if <see cref="ApplyAll"/> aborted before finishing; surfaced by
        /// <c>/mmm info</c> so a player can report it without finding the log.</summary>
        internal static string RegistrationAborted { get; private set; }

        private static void ApplyAllCore(Mod mod)
        {

            // Slot-zoning fully replaces GetAvailableNPCSlot (Town/Boss low, Enemy/Critter high), so no IL
            // patch is needed there — the detour decides placement and search range itself.
            SlotAllocator.Apply(mod);

            Apply(mod, "NPC.NewNPC",
                typeof(NPC).GetMethod(nameof(NPC.NewNPC), BindingFlags.Public | BindingFlags.Static),
                Patch_SingleLiteral200);

            Apply(mod, "Main.DoUpdateInWorld",
                typeof(Main).GetMethod("DoUpdateInWorld", BindingFlags.NonPublic | BindingFlags.Instance),
                Patch_UpdateLoop);

            Apply(mod, "Main.DrawNPCs",
                typeof(Main).GetMethod("DrawNPCs", BindingFlags.NonPublic | BindingFlags.Instance),
                Patch_DrawLoop);

            Apply(mod, "Projectile..ctor",
                typeof(Projectile).GetConstructor(Type.EmptyTypes),
                Patch_ProjectileImmunityAlloc);

            Apply(mod, "Player..ctor",
                typeof(Player).GetConstructor(Type.EmptyTypes),
                Patch_PlayerMeleeCooldownAlloc);

            // ── Combat loops: make NPCs in slots ≥200 hittable and able to deal contact damage. ──
            // Each of these methods' `< 200` loops are all NPC-bound (players use 255, projectiles 1000),
            // verified by decompilation, so replacing every `ldc.i4 200` in them is safe.
            Apply(mod, "Player.Update_NPCCollision",
                typeof(Player).GetMethod("Update_NPCCollision", BindingFlags.Public | BindingFlags.Instance),
                Patch_AllNpcLoopBounds200);

            Apply(mod, "Player.ItemCheck_MeleeHitNPCs",
                typeof(Player).GetMethod("ItemCheck_MeleeHitNPCs", BindingFlags.NonPublic | BindingFlags.Instance),
                Patch_MeleeHitNPCs);

            // "Melee hits all in swing" — the real lever. After EVERY melee hit, ProcessHitAgainstNPC calls
            // Player.ApplyAttackCooldown() => attackCD = itemAnimationMax * 0.33, and the hit loop's gate is
            // `attackCD <= 0`. So vanilla only lands ~one hit per cooldown window (≈2-3 per swing for a slow
            // weapon like Night's Edge, more for fast ones). When the toggle is on we suppress that setter so
            // attackCD stays 0 and every enemy in the swing's hitbox is hit. (More reliable than routing the
            // gate's read through MeleeAttackGate, which didn't bite in testing.)
            ApplyAttackCooldownSuppressor(mod);

            Apply(mod, "Projectile.Damage",
                typeof(Projectile).GetMethod("Damage", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null),
                Patch_DamageHitScanBounds);

            // Projectile.Update's per-frame `localNPCImmunity` decrement loop is hardcoded `< 200`, so a
            // projectile's hit-cooldown to enemies in slots 200+ never clears — minions/sentries hit each such
            // enemy ONCE then treat it as permanently immune (no damage). Widen it to the array's real length.
            Apply(mod, "Projectile.Update (localNPCImmunity decrement)",
                typeof(Projectile).GetMethod("Update", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(int) }, null),
                Patch_LocalImmunityDecrement);

            // Projectile.ResetLocalNPCHitImmunity() zeroes localNPCImmunity in a hardcoded `for (i < 200)` loop.
            // It runs both on projectile reuse (SetDefaults) and whenever a re-hitting projectile starts a new
            // attack pass (e.g. dashing sword minions like Terraprisma). Leaving it at 200 means a high-slot
            // enemy's "hit once" flag (-1) is NEVER cleared: the projectile strikes it once and can never hit it
            // again — and a reused projectile inherits the stale -1 from its previous life. Widen to the array's
            // real length. (NB: the sibling AI_156_StartAttack reset already uses localNPCImmunity.Length.)
            Apply(mod, "Projectile.ResetLocalNPCHitImmunity",
                typeof(Projectile).GetMethod(nameof(Projectile.ResetLocalNPCHitImmunity), BindingFlags.Public | BindingFlags.Instance),
                Patch_ResetLocalImmunity);

            // ── Save-safety: force the world save/load paths back to the vanilla 0-199 range. ──
            // These tModLoader methods iterate Main.maxNPCs and enumerate each NPC's instanced-global data;
            // letting them reach high slots crashes world SAVE (risking corruption). High-slot NPCs are
            // transient (enemies/critters/bosses) and intentionally not persisted, so confining save/load
            // to vanilla slots is correct and keeps worlds safe.
            // SafeGetMethod, not GetMethod: these two resolve a tModLoader-INTERNAL type, so unlike the
            // vanilla engine we cannot check the decompile for overloads, and tModLoader's own internals move
            // between builds far more freely than Terraria's. An AmbiguousMatchException here would be caught
            // by ApplyAll's guard, but that guard abandons every remaining registration — resolving safely
            // means one unresolvable name costs one patch instead of all the ones after it.
            Type worldIO = typeof(Main).Assembly.GetType("Terraria.ModLoader.IO.WorldIO");
            Apply(mod, "WorldIO.SaveNPCs",
                SafeGetMethod(worldIO, "SaveNPCs", BindingFlags.NonPublic | BindingFlags.Static),
                Patch_ForceVanilla200);
            Apply(mod, "WorldIO.LoadNPCs",
                SafeGetMethod(worldIO, "LoadNPCs", BindingFlags.NonPublic | BindingFlags.Static),
                Patch_ForceVanilla200);

            // Safety net: wrap NPCLoader.SavesAndLoads so its instanced-global enumeration can never crash
            // the game. It is called both during world save and per-NPC in CheckActive; on any error we
            // return false ("don't save / allow despawn"), which is the safe outcome for transient NPCs.
            ApplySavesAndLoadsSafeguard(mod);

            // ── Targeting / combat reach: let the player and summons act on enemies in the bonus zone. ──
            // Each method below was verified to contain only NPC-loop `200` bounds (no unrelated 200s, no
            // coupled local arrays), so replacing every `ldc.i4 200` with EngineState.NpcCap is safe.
            // (Town NPCs and bosses live in 0-199, so vanilla combat already reaches them; these widen the
            // reach to the enemies/critters that live in slots 200+.)
            // These two are load-bearing for six vanilla swords, and the second one's comment used to say
            // "reset on death", which is wrong and would mislead anyone triaging from here.
            //
            // meleeNPCHitCooldown is written UNBOUNDED from two places (Player.ProcessHitAgainstNPC and
            // Projectile.Damage, via SetMeleeHitCooldown), and read as the hit gate in Projectile.Damage. It
            // is cleared by exactly these two loops. UpdateMeleeHitCooldowns has ONE call site, and the block
            // it sits in returns early for any `noMelee` item -- so for a swing sword the Reset below, called
            // once per swing from ItemCheck_StartActualUse, is the ONLY thing that ever clears the array.
            //
            // If Reset's bound is ever left at 200, then the first time a usesOwnerMeleeHitCD sword hits an
            // enemy above slot 199 that entry is stuck forever and the enemy becomes permanently immune to
            // every sword in that family -- Night's Edge, Excalibur, True Excalibur, True Night's Edge, Terra
            // Blade, Horseman's Blade -- for the rest of the session, with no crowding needed. Breaker Blade
            // keeps working because it has no projectile and does reach UpdateMeleeHitCooldowns. That is the
            // exact signature of a standing bug report, so if these two ever stop matching, look here first.
            PatchMethod(mod, typeof(Player), "UpdateMeleeHitCooldowns");   // per-frame decrement, melee items only
            PatchMethod(mod, typeof(Player), "ResetMeleeHitCooldowns");    // per-SWING reset (ItemCheck_StartActualUse)
            PatchMethod(mod, typeof(Player), "CollideWithNPCs");           // player contact/touch damage
            PatchMethod(mod, typeof(Player), "JumpMovement");             // slime-mount bounce damage
            PatchMethod(mod, typeof(Player), "DashMovement");            // dash damage: Shield of Cthulhu + Solar Flare (both its 200s are NPC loops)
            PatchMethod(mod, typeof(Player), "MinionNPCTargetAim");        // whip/minion target aim (closest to cursor)
            PatchMethod(mod, typeof(Player), "GetZenithTarget");          // Zenith homing
            PatchMethod(mod, typeof(Player), "GetSparkleGuitarTarget");   // Sparkle Guitar homing
            PatchMethod(mod, typeof(Projectile), "FindTargetWithinRange");        // shared minion finder
            PatchMethod(mod, typeof(Projectile), "FindTargetWithLineOfSight");    // shared minion finder
            PatchMethod(mod, typeof(Projectile), "Minion_FindTargetInRange");      // shared minion AUTO-target finder (single NPC loop) — fixes minions not auto-acquiring enemies in slots 200+
            PatchMethod(mod, typeof(Projectile), "AI_120_StardustGuardian_FindTarget");

            // Discrete projectile AIs with their OWN NPC-target scan (i.e. not using the shared finder):
            // new-style minions (Abigail/Stardust Dragon/Smolstars…), sentries (towers/lightning aura) and
            // whip-summoned homing projectiles (Cool Whip / Dark Harvest…). Each is patched with the chase-loop
            // heuristic, which widens ONLY a `< 200` loop whose body calls CanBeChasedBy — so unrelated 200s in
            // these methods are untouched. (Old-style minions + homing bullets live in the monolithic AI(); a
            // separate, validated pass.)
            // These per-weapon AIs use varied target predicates — CanBeChasedBy (Abigail), `active`/`chaseable`
            // (Chlorophyte homing in AI_001), type-gated scans (spider sentry in AI_026) — so we widen any
            // `< 200` loop whose body touches an NPC member, not just CanBeChasedBy ones.
            foreach (string ai in ChaseLoopAIMethods)
                PatchNpcLoops(mod, typeof(Projectile), ai);

            // The REAL monolithic projectile AI is Projectile.VanillaAI() (Projectile.AI() is just a 5-line
            // dispatcher). It holds every inline-aiStyle NPC scan: the turret sentries (Frost Hydra / Rainbow
            // Crystal / Houndius / Spider Hiver / Lunar Portal), old-style minions, and active-based homing.
            // Use the broad NPC-member check so all of them are widened, not just the CanBeChasedBy ones.
            PatchNpcLoops(mod, typeof(Projectile), "VanillaAI");

            // Info accessories (in Main.DrawInfoAccs) that scan NPCs only cover 0-199 — with enemies/critters
            // living in the bonus zone, the Radar reads "No enemies nearby" and the Lifeform Analyzer misses
            // nearly everything it exists to find (rare critters/enemies). Anchored patches (so the other
            // info-accessory loops in this method are untouched) that widen both scans AND fix their byte-sized
            // storage: the Radar's count byte caps at 255 (true count tallied in an int and displayed instead);
            // the Analyzer stores the found NPC's SLOT INDEX in a byte (slot 300 would truncate to 44 and name
            // the wrong NPC — true slot tracked in an int instead).
            Apply(mod, "Main.DrawInfoAccs (radar + lifeform analyzer)",
                typeof(Main).GetMethod("DrawInfoAccs", BindingFlags.NonPublic | BindingFlags.Instance),
                Patch_InfoAccessories);

            // The mouse hover + right-click interaction scan (name-on-hover, chat, and the bound-NPC rescue
            // path all live in this one loop) is hardcoded 0-199. Rescue NPCs are kept in the low zone by
            // categorization (see NpcCategorizer.IsRescueNpc), but widen the scan anyway so any friendly that
            // still lands high (low zone full -> fallback, modded friendlies) can be hovered and rescued.
            // Verified: the method's only `200` literal is this loop bound.
            PatchNpcLoops(mod, typeof(Main), "HoverOverNPCs");

            // ── Playtester-reported gaps (0.7.2). Found by tools/audit-npc-loops.sh, which diffs the engine's
            // full inventory of hardcoded NPC loops against this registry — re-run it after a tModLoader update.
            // All four are small, single-purpose methods, and PatchNpcLoops only widens a `< 200` loop whose body
            // touches an NPC member, so unrelated 200s (type ids, distances) are never rewritten.

            // Bug net / critter catching. ItemCheck_CatchCritters scans 0-199 for `catchItem > 0`, but critters
            // are zoned into the expanded slots — so nets caught literally nothing in Expanded mode.
            PatchNpcLoops(mod, typeof(Player), "ItemCheck_CatchCritters");

            // Invasion progress bar. CheckInvasionProgressDisplay scans 0-199 to decide whether an invasion
            // enemy is near enough to SHOW the progress bar; with the invaders in high slots it concluded "no
            // invasion nearby" and the bar never appeared (reported as invasions being "un-ending").
            PatchNpcLoops(mod, typeof(Main), "CheckInvasionProgressDisplay");

            // Lunatic Cultist ritual: nine 0-199 scans (counting the ritual cultists, finding the real body,
            // clearing adds). Cultists spawning into high slots could break the ritual / pillar summon.
            PatchNpcLoops(mod, typeof(NPC), "AI_084_LunaticCultist");

            // Statue / mechanism spawn limits: MechSpawn counts nearby same-type NPCs to cap statue output.
            // Blind to high slots it undercounts, letting statues flood the world past their vanilla limit.
            PatchNpcLoops(mod, typeof(NPC), "MechSpawn");

            // ── Enemy AI proper (0.7.8.0). The two largest remaining gap clusters, and the last of the file
            //    to be done deliberately: the plan called for them LAST because they run on every enemy every
            //    tick, so their cost scales with the cap rather than being paid once.
            //
            // Both are routed through the npc-loop heuristic rather than a blanket pass, and NOT because
            // blanket is merely untidy here — it is wrong. AI_003_Fighters carries `Dust.NewDust(..., 200, ...)`
            // alpha arguments and two `= 200` assignments (an alpha for type 466 and a timer for type 291);
            // a blanket pass would rewrite all of those to 750 and corrupt the visuals of enemies that are
            // otherwise fine. The heuristic only rewrites a bound with a BACKWARD branch, which no assignment
            // or call argument has.
            //
            // Checked before registering, because the heuristic's own hazard inside Terraria.NPC is that a
            // 200-bounded DUST loop touching an NPC field would be widened to 750 particles:
            //   · VanillaAI_Inner  — 29 `< 200` loops, and all 29 are NPC scans. No dust loop, no other shape.
            //   · AI_003_Fighters  —  5 `< 200` loops, likewise all NPC scans.
            // Every one of the 34 bodies references a member declared on Terraria.NPC (`type`, `aiStyle`,
            // `ai[]`), so none of them fails the way NPCUtils.SearchForTarget did. The counts in the log are
            // the check that this stayed true: expect 29 and 5.
            //
            // What is actually broken without this:
            //   · the same-type SEPARATION loops ("if another of my type is within `width`, push apart") see
            //     nobody at all above slot 199, so same-type enemies converge into one spot instead of
            //     spreading — Twins, and the flying-fighter family;
            //   · type 415 decides whether to HIDE itself by looking for its type-416 partner, so it draws
            //     when it should not;
            //   · several add-counting scans (type 115, 125/126, 264, aiStyle 52) read zero and re-summon.
            //
            // The MP patcher also hooks VanillaAI_Inner, with Patch_ServerBroadcastGuards. No conflict: that
            // matcher only takes a literal with a Main.netMode read before it and a NetMessage.SendData call
            // after it, which is never true of a loop bound, and this one only takes backward-branch bounds.
            PatchNpcLoops(mod, typeof(NPC), "VanillaAI_Inner");
            PatchNpcLoops(mod, typeof(NPC), "AI_003_Fighters");

            // ── The remaining named gaps (0.7.8.0). Every one of these methods is blanket-UNSAFE, which is
            //    why they were left: each carries 200s that are not slot bounds and would do real damage if
            //    rewritten. Literal inventory taken from the decompile, per method:
            //
            //      checkDead              1 loop  · `Main.maxTilesY - 200` (a tile scan floor) + two netMode
            //                                       broadcast guards owned by the multiplayer patcher
            //      StrikeNPC              1 loop  · none at all (blanket would in fact be safe; routed the
            //                                       same way anyway so one rule covers the whole group).
            //                                       NOTE: resolved separately below — it has two overloads.
            //      SpawnNPC               2 loops · 15 others — spawn-depth checks, NPC type ids that happen
            //                                       to be 199/200/201, `Main.rand.Next(200)`, `NewNPC(..., 200)`
            //                                       and the `int newNPC = 200` sentinel that already has its
            //                                       own anchored patch
            //      DrawNPCDirect_Inner    2 loops · 13 others, all colour channels and alphas
            //      Wiring.HitWireSingle   2 loops · six `CheckMech(i, j, 200)` (a WIRE-PULSE budget, nothing
            //                                       to do with slots) plus two `= 200` counters
            //      AnyHelpfulFairies      1 loop  · none
            //
            // The loop-bound heuristic is immune to all of that by construction: it only rewrites a literal
            // that is the bound of a backward branch, and an argument, an assignment and a colour channel are
            // none of those.
            //
            // What each one costs while unpatched:
            //   · Wiring.HitWireSingle — the King and Queen statue teleports collect their candidate town
            //     NPCs here. Zoning normally keeps town NPCs low so this is latent, but it is the exact shape
            //     that bites the moment the low zone is full.
            //   · SpawnNPC — the first loop is the Moon-event slot budget (it counts pillar and miniboss
            //     types to decide how much more may spawn); reading zero makes the budget wrong in the
            //     permissive direction.
            //   · checkDead — the Destroyer picks the segment nearest the player to drop loot from; blind, it
            //     drops from wherever the dying segment happened to be.
            //   · StrikeNPC — Wall of Flesh: hitting one half must run HitEffect on both (types 113/114).
            //   · DrawNPCDirect_Inner — two draw-time partner lookups (the type-397 tether, and the Twins
            //     pair for the Mech Queen's centre point), so a connector renders detached or not at all.
            // A regression this mod created, not a vanilla gap — and the only one of its kind found so far.
            //
            //     int num9 = (int)ai[2];
            //     if (num9 < 0 || num9 >= 200) { num9 = FindFirstNPC(134); ai[2] = num9; netUpdate = true; }
            //
            // We widened FindFirstNPC, so it now legitimately RETURNS a slot at or above 200 — which this
            // guard, still on vanilla's bound, then rejects. It re-runs the search, gets the same high slot
            // back, stores it, and sets netUpdate. Every tick. It is not a feature quietly failing: it is a
            // permanent re-search that also flags a network update on every frame for as long as the state
            // holds, so in multiplayer it is a broadcast the server did not ask for.
            //
            // Narrow in practice (it is behind IsMechQueenUp, the Twins-fused Mech Queen), which is exactly
            // why it needs to be written down — nobody would reproduce this from a bug report. Confirmed
            // against the IL: one int gate, no float slot gate, and the `Main.npc[num9]` that proves it is a
            // slot sits 18 instructions after the literal.
            Apply(mod, "NPC.AI_005_EaterOfSouls (FindFirstNPC result gate)",
                typeof(NPC).GetMethod("AI_005_EaterOfSouls", BindingFlags.NonPublic | BindingFlags.Instance),
                il => Patch_TargetSlotGates(il, expectInt: 1, expectFloat: 0));

            // Coin Loss Revenge — another NewNPC SUCCESS SENTINEL, the family this mod creates rather than
            // inherits. We widen NewNPC's failure return from 200 to the cap, so every caller still testing
            // `< 200` now reads a perfectly good high-zone slot as a failure:
            //
            //     int num = NPC.NewNPC(...);  NPC nPC = Main.npc[num];  ... nPC.life = ...;
            //     if (num < 200) { if (Main.netMode == 0) nPC.moneyPing(_location); else SendData(23, ...); }
            //
            // The enemy IS spawned and its life IS set — only the notification is skipped. Single-player loses
            // the money ping that tells you the marker paid out; on a server the spawn is never synced, so the
            // revenge enemy exists only server-side and clients fight something they cannot see. Since our
            // allocator fills enemies from the top down, `num < 200` is false essentially always.
            //
            // Not owned by the multiplayer patcher despite the SendData: its matcher requires a Main.netMode
            // read BEFORE the literal, and here the netMode read comes after. Exactly one 200 in the method,
            // and one in the whole type.
            Apply(mod, "CoinLossRevengeSystem.RevengeMarker.SpawnEnemy (NewNPC success sentinel)",
                typeof(Main).Assembly
                    .GetType("Terraria.GameContent.CoinLossRevengeSystem+RevengeMarker")
                    ?.GetMethod("SpawnEnemy", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance),
                il => Patch_TargetSlotGates(il, expectInt: 1, expectFloat: 0));

            PatchNpcLoops(mod, typeof(NPC), "checkDead");
            // AUDIT-SKIP: NPC.TargetClosestUpgraded — unreachable, and superseded. Zero call sites in the
            // whole assembly. Its 0-199 scan looks for type 548, the Old One's Army crystal, which is what
            // makes it read like an event-breaking gap — but the live crystal search is
            // NPCUtils.TargetClosestOldOnesInvasion -> SearchForTarget (SearchFilters.OnlyCrystal), patched
            // below. This looks like an earlier draft vanilla replaced with the NPCUtils family and never
            // deleted. It is `public`, so a mod could in principle call it; nothing observed does, and the one
            // job it would be called for is already covered by the method that actually runs.

            // StrikeNPC canNOT go through PatchNpcLoops, and the reason is a trap worth spelling out.
            //
            // There are TWO StrikeNPC overloads: `public int StrikeNPC(HitInfo, bool, bool)` — the real one,
            // holding the Wall of Flesh loop — and `internal int StrikeNPC(int, float, int, bool, bool, bool)`,
            // a three-line legacy shim that just forwards to it. PatchNpcLoops resolves by NAME ONLY with
            // Public|NonPublic|Instance|Static, and `internal` IS NonPublic, so both overloads match and
            // GetMethod throws AmbiguousMatchException. That is caught and logged, so the failure is loud —
            // but the patch simply never happens, and every comment in this file said it did.
            //
            // Worth noting how easily this hid: grepping the decompile for `public .*StrikeNPC(` and
            // `private .*StrikeNPC(` finds exactly one declaration and looks conclusive. The second overload
            // is neither. Match on the parameter list, not on the accessibility keyword.
            //
            // Resolved by shape rather than by a typeof(NPC.HitInfo) reference so this does not depend on the
            // nested type's visibility, and so an added overload changes the count instead of silently
            // rebinding us to the wrong method.
            MethodInfo strikeNpc = typeof(NPC)
                .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Where(m => m.Name == "StrikeNPC"
                            && m.GetParameters().Length == 3
                            && m.GetParameters()[0].ParameterType.Name == "HitInfo")
                .ToArray() is { Length: 1 } hit ? hit[0] : null;

            if (strikeNpc == null)
                mod.Logger.Error("[MMM] NPC.StrikeNPC(HitInfo, bool, bool) not resolvable; " +
                                 "Wall of Flesh hit propagation not widened.");
            else
                Apply(mod, "NPC.StrikeNPC (npc loops)", strikeNpc, Patch_NpcLoops);
            PatchNpcLoops(mod, typeof(NPC), "SpawnNPC");
            PatchNpcLoops(mod, typeof(NPC), "AnyHelpfulFairies");
            PatchNpcLoops(mod, typeof(Main), "DrawNPCDirect_Inner");
            PatchNpcLoops(mod, typeof(Wiring), "HitWireSingle");

            // ── Magic surface (0.7.7.2). Found by audit; each literal count checked against the decompile. ──

            // Spectre Mask's damage set bonus is completely inert. ghostHurt is magic-only by construction
            // (`if (!magic || damage <= 0) return;`) and scans 0-199 for a bolt target, so with every enemy
            // above 199 it finds nothing and no spectre bolt is ever spawned. Three `200` literals and ALL
            // THREE must move together: `new int[200]`, a second discarded `new int[200]`, and the loop bound
            // — the two counters index that array with no cap, so widening the loop alone would turn a dead
            // set bonus into an IndexOutOfRange. The blanket manipulator happens to be exactly right here.
            Apply(mod, "Projectile.ghostHurt (Spectre Mask set bonus)",
                typeof(Projectile).GetMethod(nameof(Projectile.ghostHurt), BindingFlags.Public | BindingFlags.Instance),
                Patch_AllNpcLoopBounds200);

            // Life Drain never charges mana. Payment is deliberately deferred from the item to the projectile
            // (Player.ItemCheck_PayMana special-cases item 3006 and only CHECKS), and the projectile pays only
            // when its 0-199 scan finds an NPC it is touching. Above 199 it never does, so the weapon channels
            // free forever. One literal, the loop bound.
            Apply(mod, "Projectile.AI_185_LifeDrain (mana charge)",
                typeof(Projectile).GetMethod("AI_185_LifeDrain", BindingFlags.NonPublic | BindingFlags.Instance),
                Patch_AllNpcLoopBounds200);

            // Dryad's Blessing never applies Dryad's Bane (the damage-amp mark) to enemies. The town-NPC half
            // of the same loop still works only because town NPCs are zoned low. Two literals; the other is a
            // float `/ 200f` radius lerp, which SafeLdcI4 cannot match, so blanket touches only the bound.
            Apply(mod, "Projectile.AI_111_DryadsWard (Dryad's Bane)",
                typeof(Projectile).GetMethod("AI_111_DryadsWard", BindingFlags.NonPublic | BindingFlags.Instance),
                Patch_AllNpcLoopBounds200);

            // Betsy's flame breath validates her slot as `ai[1] > 200f` — float, so no int patch could see it.
            // Boss zoning normally keeps her under 200, so this only bites when the low zone overflows; the
            // same conditional shape as the worm health bar. Note vanilla's off-by-one: `> 200f` lets slot
            // exactly 200 through, which is a real enemy under a raised cap. Float-only method, hence the
            // relaxed check in Patch_TargetSlotGates. Its other `200` is a dust alpha and must NOT be touched.
            // 0 int / 1 float, confirmed against the decompile and against the shipped log line.
            Apply(mod, "Projectile.AI_136_BetsyBreath (target slot gate)",
                typeof(Projectile).GetMethod("AI_136_BetsyBreath", BindingFlags.NonPublic | BindingFlags.Instance),
                il => Patch_TargetSlotGates(il, expectInt: 0, expectFloat: 1));

            // Inferno Potion does nothing at all — its buff scans 0-199 to apply the burn. Anchored via the
            // npc-loop heuristic rather than blanket: UpdateBuffs is 2165 lines with eight 200/199 literals,
            // including `buffType[j] == 200`, `ownedProjectileCounts[199]`, and two projectile type ids.
            PatchNpcLoops(mod, typeof(Player), "UpdateBuffs");

            // Crystal Dart's post-bounce re-aim (Dart Pistol / Dart Rifle's premium ammo). HandleMovement
            // scans 0-199 for a new target after a tile bounce, so the dart flies straight instead of curving
            // into an enemy — its entire selling point. Note the asymmetry that made this look intermittent:
            // the ON-HIT twin of this scan lives in Projectile.Damage and IS covered, so the dart re-aims
            // after hitting an enemy but not after hitting a wall. Exactly one `200` in the whole 2656-line
            // method (checked against the decompile) and it is this bound, so blanket is safe.
            Apply(mod, "Projectile.HandleMovement (dart re-aim after bounce)",
                typeof(Projectile).GetMethod("HandleMovement", BindingFlags.NonPublic | BindingFlags.Instance),
                Patch_AllNpcLoopBounds200);

            // Stored-target-slot gates inside VanillaAI — see Patch_TargetSlotGates. Not loops, so no loop
            // patcher reaches them; anchored individually because the method is far too large to blanket.
            //
            // THREE int gates, all in the aiStyle 113 "stuck in an enemy" block and all indexing Main.npc by
            // the same local: the kill gate `slot < 0 || slot >= 200`, and the two per-frame dust emitters for
            // types 971 and 975. Plus ONE float gate, the aiStyle 55 Horseman's Blade pumpkin re-target.
            // Counted in the decompiled method body; the assertion in the manipulator is what keeps that count
            // honest, because until 0.7.8.0 the int arm matched ZERO of the three and said so only in a log
            // line nobody had reason to re-read.
            Apply(mod, "Projectile.VanillaAI (stored target-slot gates)",
                typeof(Projectile).GetMethod("VanillaAI", BindingFlags.Public | BindingFlags.Instance),
                il => Patch_TargetSlotGates(il, expectInt: 3, expectFloat: 1));

            // -- Sibling pass (0.7.8.0). Each of these is the OTHER half of a fix already made: the same
            // feature reached by a second code path that the original pass stopped short of. Literal counts
            // re-verified against the decompile; all are loop bounds unless noted.

            // Scutlix and Santank never fire. CastSuperCartLaser was patched; UpdateEffects -- the same file,
            // the per-frame mount update -- holds the auto-target scan for both those mounts, so the "found a
            // target" flag never sets and AimAbility/UseAbility are never called. Not overflow-gated: broken
            // in every game. 628 lines, one literal.
            Apply(mod, "Mount.UpdateEffects (Scutlix/Santank auto-target)",
                typeof(Mount).GetMethod("UpdateEffects", BindingFlags.NonPublic | BindingFlags.Instance)
                    ?? typeof(Mount).GetMethod("UpdateEffects", BindingFlags.Public | BindingFlags.Instance),
                Patch_AllNpcLoopBounds200);

            // CacheProjDraws was patched; CacheNPCDraws is the method directly above it and is called on the
            // line before it at both call sites. It runs NPCLoader.DrawBehind for each active NPC, and the
            // documented contract for that is to pair it with `NPC.hide = true` -- which DrawNPCs then skips.
            // So a modded NPC using that standard pair, above slot 199, is drawn by nobody at all. Two
            // literals, both loop bounds.
            Apply(mod, "Main.CacheNPCDraws (hidden/DrawBehind NPCs)",
                typeof(Main).GetMethod("CacheNPCDraws", BindingFlags.NonPublic | BindingFlags.Instance),
                Patch_AllNpcLoopBounds200);

            // Boss head icons on the map. The health bars and the in-world markers were fixed; the three map
            // loops (overlay, minimap, fullscreen) were not. NPCID.Sets.BossHeadTextures covers several types
            // that are neither `boss` nor in ShouldBeCountedAsBoss, so the categorizer files them as ordinary
            // enemies -- Mourning Wood, Pumpking, Everscream, Ice Queen, SantaNK1, Flying Dutchman, Dungeon
            // Guardian. Their map marker never appears, during exactly the events this mod amplifies.
            // 1208 lines, three literals, all three loop bounds.
            Apply(mod, "Main.DrawMap (boss/town map icons)",
                typeof(Main).GetMethod("DrawMap", BindingFlags.NonPublic | BindingFlags.Instance)
                    ?? typeof(Main).GetMethod("DrawMap", BindingFlags.Public | BindingFlags.Instance),
                Patch_AllNpcLoopBounds200);

            // Invasion battle music never starts. Sibling of CheckInvasionProgressDisplay ("the bar never
            // appeared") -- same 0-199 scan, different consumer. Both variants are live: the dispatcher picks
            // _DecideOnTOWMusic when the Otherworldly music toggle is on and _DecideOnNewMusic otherwise, so
            // neither can be dismissed as legacy. Boss music survives only because bosses are zoned low.
            // One literal each.
            Apply(mod, "Main.UpdateAudio_DecideOnNewMusic (invasion music)",
                typeof(Main).GetMethod("UpdateAudio_DecideOnNewMusic", BindingFlags.NonPublic | BindingFlags.Instance),
                Patch_AllNpcLoopBounds200);
            Apply(mod, "Main.UpdateAudio_DecideOnTOWMusic (invasion music, otherworldly)",
                typeof(Main).GetMethod("UpdateAudio_DecideOnTOWMusic", BindingFlags.NonPublic | BindingFlags.Instance),
                Patch_AllNpcLoopBounds200);

            // Bone Helm does nothing. Exactly the Volatile Gelatin shape: the accessory only spawns its
            // projectile when its 0-199 candidate scan finds something, and it never does. One literal.
            Apply(mod, "Player.SpawnHallucination (Bone Helm)",
                typeof(Player).GetMethod("SpawnHallucination", BindingFlags.NonPublic | BindingFlags.Instance)
                    ?? typeof(Player).GetMethod("SpawnHallucination", BindingFlags.Public | BindingFlags.Instance),
                Patch_AllNpcLoopBounds200);

            // The read half of a write we already widened. NPC.UpdateFoundActiveNPCs (the writer of the
            // "is a type of NPC alive" flags) was patched, but both consumers pass the now-correct flag and
            // then re-scan 0-199 for the position, so they still answer false. isNearFairy gates the fairy
            // spawn-rate modifier inside the already-patched SpawnNPC -- and our own diagnostics report
            // "Near Fairy: false" as a consequence. One literal each; the other loop in isNearNPC is a
            // 255-player bound and is not touched.
            Apply(mod, "Player.isNearFairy (fairy spawn modifier)",
                typeof(Player).GetMethod("isNearFairy", BindingFlags.NonPublic | BindingFlags.Instance)
                    ?? typeof(Player).GetMethod("isNearFairy", BindingFlags.Public | BindingFlags.Instance),
                Patch_AllNpcLoopBounds200);
            Apply(mod, "Player.isNearNPC (proximity check)",
                typeof(Player).GetMethod("isNearNPC", BindingFlags.NonPublic | BindingFlags.Instance)
                    ?? typeof(Player).GetMethod("isNearNPC", BindingFlags.Public | BindingFlags.Instance),
                Patch_AllNpcLoopBounds200);

            // Smart Cursor's NPC targeting. Main.HoverOverNPCs was widened deliberately so a friendly that
            // lands high can still be hovered and rescued; this is the same interaction through a different
            // provider, and it was left at 200 -- so a modded chattable non-townNPC above slot 199 cannot be
            // smart-targeted. Same argument, opposite decision, now consistent. One literal.
            Apply(mod, "NPCSmartInteractCandidateProvider.ProvideCandidate (smart cursor)",
                typeof(Main).Assembly.GetType("Terraria.GameContent.ObjectInteractions.NPCSmartInteractCandidateProvider")
                    ?.GetMethod("ProvideCandidate", BindingFlags.Public | BindingFlags.Instance),
                Patch_AllNpcLoopBounds200);

            // Eater of Worlds loot. Sibling of the PlayerInteraction fix and carrying its exact caveat --
            // dormant under boss zoning, live once the guaranteed-spawn path spills it. This is the "am I the
            // last segment alive?" test, so with the survivors all above 199 it reads true early and boss loot
            // can drop before the worm is dead, possibly more than once. One literal.
            Apply(mod, "NPC.DropEoWLoot (last-segment check)",
                typeof(NPC).GetMethod("DropEoWLoot", BindingFlags.NonPublic | BindingFlags.Instance)
                    ?? typeof(NPC).GetMethod("DropEoWLoot", BindingFlags.Public | BindingFlags.Instance),
                Patch_AllNpcLoopBounds200);

            // Old One's Army enemy AI, missed by the dedicated OOA pass which covered the event and the gate
            // spawners but not the enemies' own scans. The Dark Mage counts damaged nearby allies to choose
            // between healing and summoning; they are all above 199, so it always summons and never heals.
            // The lightning bugs' separation scan likewise, so they stack on each other. One literal each.
            Apply(mod, "NPC.AI_109_DarkMage (heal-vs-summon choice)",
                typeof(NPC).GetMethod("AI_109_DarkMage", BindingFlags.NonPublic | BindingFlags.Instance),
                Patch_AllNpcLoopBounds200);
            Apply(mod, "NPC.AI_111_DD2LightningBug (separation)",
                typeof(NPC).GetMethod("AI_111_DD2LightningBug", BindingFlags.NonPublic | BindingFlags.Instance),
                Patch_AllNpcLoopBounds200);

            // Lets you place blocks inside an enemy in a high slot. One literal.
            Apply(mod, "Collision.EmptyTile (block placement vs NPCs)",
                typeof(Collision).GetMethod("EmptyTile", BindingFlags.Public | BindingFlags.Static),
                Patch_AllNpcLoopBounds200);

            // -- World-load / lifecycle pass (0.7.8.0). Every literal count verified against the decompile.

            // NPC.IsMechQueenUp reads `mechQueen >= 0 && mechQueen < 200` while THIS MOD has widened the
            // writer. `mechQueen = FindFirstNPC(127)` goes through NPC.FindFirstNPC, which we patch to the
            // full range, so the field can now hold a slot the getter refuses to accept. That inconsistency
            // is ours, not vanilla's.
            //
            // Thirty-six reads across the whole Mechdusa AI plus three draw sites depend on it, so in a
            // getGood/remix world a Prime head above slot 199 means the fused boss never assembles. The
            // getter's own self-heal (`mechQueen = -1` when the slot is dead or the wrong type) sits INSIDE
            // the failing guard, so it cannot recover either. One literal in the getter.
            Apply(mod, "NPC.IsMechQueenUp (Mechdusa slot guard)",
                typeof(NPC).GetProperty("IsMechQueenUp", BindingFlags.Public | BindingFlags.Static)?.GetGetMethod(),
                Patch_AllNpcLoopBounds200);

            // Bestiary credit for critters. ScanWorldForFinds runs every frame from DoUpdateInWorld and scans
            // 0-199 for "seen near the player" — but critters are Critter-category, so they are allocated
            // top-down and essentially all of them live above 199. Critter bestiary entries therefore never
            // unlock from proximity. One literal.
            Apply(mod, "NPCWasNearPlayerTracker.ScanWorldForFinds (critter bestiary credit)",
                typeof(Main).Assembly.GetType("Terraria.GameContent.Bestiary.NPCWasNearPlayerTracker")
                    ?.GetMethod("ScanWorldForFinds", BindingFlags.Public | BindingFlags.Instance),
                Patch_AllNpcLoopBounds200);

            // Lunar Apocalypse. GetRidOfCultists is the cleanup that removes the ritual's Devotees, Archers
            // and the Lunatic Cultist when Impending Doom starts; they are Enemy-category, so they are all
            // above 199 and none of them is cleaned up. UpdateLunarApocalypse's own scan tracks the four
            // pillars and Moon Lord, which are boss-zoned, so that half only bites on boss-zone overflow --
            // where it would clear the TowerActive flags while a pillar is still alive. One literal each.
            Apply(mod, "WorldGen.GetRidOfCultists (post-ritual cleanup)",
                typeof(WorldGen).GetMethod("GetRidOfCultists", BindingFlags.Public | BindingFlags.Static),
                Patch_AllNpcLoopBounds200);
            Apply(mod, "WorldGen.UpdateLunarApocalypse (pillar/Moon Lord tracking)",
                typeof(WorldGen).GetMethod("UpdateLunarApocalypse", BindingFlags.Public | BindingFlags.Static),
                Patch_AllNpcLoopBounds200);

            // Teleporters ignored anything above slot 199 entirely, and the `teleporting` flag they set was
            // only ever cleared for 0-199 -- so on the rare occasion a high-slot NPC did get flagged it stayed
            // flagged for the rest of the world. Both of the method's literals are these two loop bounds.
            Apply(mod, "Wiring.Teleport (teleporter NPC pass)",
                typeof(Wiring).GetMethod("Teleport", BindingFlags.NonPublic | BindingFlags.Static),
                Patch_AllNpcLoopBounds200);

            // "Is a boss/event NPC of this type alive?" — the answer feeds Deerclops proximity, two Mechdusa
            // AI tuning reads and the RGB-peripheral lighting tier. The CLEAR is full-range (the array is
            // keyed by NPC type, not slot) while the repopulate scanned 0-199, so the flag was permanently
            // false for any type that only exists in the bonus zone. Write-narrow / clear-wide -- the same
            // pairing as the immunity table, on per-frame state rather than cross-world state. One literal.
            Apply(mod, "NPC.UpdateFoundActiveNPCs (active-type flags)",
                typeof(NPC).GetMethod("UpdateFoundActiveNPCs", BindingFlags.Public | BindingFlags.Static),
                Patch_AllNpcLoopBounds200);

            // Duke Fishron's and the Wall of Flesh's screen darkening. Boss-zoned, so overflow-only. One literal.
            Apply(mod, "ScreenDarkness.Update (boss screen darkening)",
                typeof(Main).Assembly.GetType("Terraria.GameContent.Events.ScreenDarkness")
                    ?.GetMethod("Update", BindingFlags.Public | BindingFlags.Static),
                Patch_AllNpcLoopBounds200);

            // The Mechanical Cart's laser could not target anything above slot 199 -- open since the
            // whole-assembly sweep first surfaced it. One literal.
            Apply(mod, "Mount.CastSuperCartLaser (minecart laser targeting)",
                typeof(Mount).GetMethod("CastSuperCartLaser", BindingFlags.NonPublic | BindingFlags.Instance),
                Patch_AllNpcLoopBounds200);

            // WorldGen.clearWorld — the world-clear that rebuilds Main.npc, and the root of the
            // "does a world change actually reset the bonus slots?" question.
            //
            // Vanilla replaces every NPC with a fresh object on world clear: `Main.npc[n] = new NPC()` for
            // n < 200. Under a raised cap that leaves slots 200-749 holding the PREVIOUS world's NPC objects,
            // still carrying their active flag, ai[], buffs, type and life. Since ordinary enemies are
            // allocated from the top down, that is where essentially every enemy lives.
            //
            // MaxNpcCapRaise.OnWorldLoad already deactivates that range, and that mitigation is what has kept
            // this from being a headline bug — but it runs AFTER world load completes, it only clears `active`,
            // and it leaves a window during load itself (town-NPC placement, housing scoring) where the stale
            // objects are still visible. Widening the loop is the root fix and makes the bonus range behave on
            // a world change exactly as vanilla's 0-199 does, which is the only standard worth holding to here.
            // The mitigation stays as belt-and-braces; it is cheap and it covers the cap-raise-toggled-off case.
            //
            // ANCHORED, and this one would be genuinely destructive to blanket: the method's other 200 is
            // `(Main.maxTilesX - 1) / 200 + 1`, the world SECTION size. Widening that would rebuild the section
            // manager at the wrong granularity for the whole world. The npc-loop heuristic takes only
            // backward-branch bounds whose body references a Terraria.NPC-declared member, and a divisor is
            // neither.
            //
            // What satisfies that test here is `newobj Terraria.NPC::.ctor()` — NOT `whoAmI`, which an earlier
            // version of this comment claimed. `whoAmI` is declared on Terraria.Entity and does not satisfy it
            // at all. The distinction is not pedantry: it is exactly the confusion that left
            // NPCUtils.SearchForTarget silently unpatched. If this loop were ever rewritten to reuse existing
            // NPC objects instead of allocating new ones, the constructor call would vanish and this patch
            // would quietly stop matching.
            PatchNpcLoops(mod, typeof(WorldGen), "clearWorld");

            // Boss summon items stop working as duplicate guards once the boss zone overflows. SummonItemCheck
            // scans 0-199 for a live boss of the matching type and refuses the item if it finds one; above
            // that it finds nothing and happily summons a second Eye of Cthulhu, Destroyer, Queen Bee, Brain,
            // Queen Slime or Empress on top of the first. Bosses are zoned low so this normally holds, but the
            // guaranteed-spawn path overflows that zone on purpose - and two simultaneous mechanical bosses is
            // a far louder failure than most. One literal in a fourteen-line method.
            Apply(mod, "Player.SummonItemCheck (duplicate-boss guard)",
                typeof(Player).GetMethod("SummonItemCheck", BindingFlags.Public | BindingFlags.Instance),
                Patch_AllNpcLoopBounds200);

            // The "did NewNPC succeed?" sentinel, which this mod turns from a safe idiom into a wrong one.
            //
            // Vanilla's NewNPC returns 200 on failure -- one past the last real slot -- so callers test the
            // result with `== 200` or `< 200` to mean "did I get a real NPC?". We patch that failure return to
            // EngineState.NpcCap (750), which is correct on its own, but it invalidates every caller that
            // still compares against 200. Real slots now run 200-749, so a successful spawn reads as a
            // failure.
            //
            // NPC.SpawnBoss is the one that bites in single-player. `if (num == 200) return;` no longer
            // catches an actual failure, and worse, a boss that legitimately lands in slot 200 is treated as
            // one: the method returns before setting the boss's target, before multiplying its timeLeft by 20
            // and before the mech-boss achievement check, so the boss drifts with a normal despawn timer.
            // Only reachable once the reserved boss zone overflows -- which the guaranteed-spawn path does on
            // purpose. Two of its three 200s are this sentinel; the third is a multiplayer broadcast guard
            // owned by MMMultiplayer, so this takes only its own two -- see Patch_SpawnBossSentinel.
            Apply(mod, "NPC.SpawnBoss (NewNPC success sentinel)",
                typeof(NPC).GetMethod("SpawnBoss", BindingFlags.Public | BindingFlags.Static),
                Patch_SpawnBossSentinel);

            // Windy Day balloons lose track of the slime tied to them. The balloon stores its partner's slot
            // in ai[3] and validates it with `>= 0 && < 200` before use; above 199 the lookup returns null, so
            // the pair never behaves as a pair. Not netMode-gated, so this is single-player too. One literal
            // in a twenty-line method.
            Apply(mod, "NPC.AI_113_WindyBalloon_GetSlaveNPC (paired-slot lookup)",
                typeof(NPC).GetMethod("AI_113_WindyBalloon_GetSlaveNPC", BindingFlags.Public | BindingFlags.Instance),
                Patch_AllNpcLoopBounds200);

            // The same sentinel appears 23 more times across the engine, always as
            // `if (Main.netMode == 2 && spawned < 200) NetMessage.SendData(23, ...)`: 16 in NPC.cs, 5 in
            // DD2Event (already covered by the multiplayer patcher's gate-spawner pass) and 2 in WorldGen.
            // Every one is server-only, and the symptom is uniform -- an NPC spawned into a high slot by
            // another NPC's AI is never broadcast, so clients never see it. They are deliberately left for the
            // multiplayer work rather than fixed piecemeal here: most sit inside VanillaAI_Inner and
            // VanillaHitEffect, which are far too literal-rich to blanket, so each needs its own anchor and
            // none of them can be verified without a server to test against.

            // Brain of Cthulhu's gravity inversion, and the cleanup that is supposed to forget it.
            //
            // NPC.brainOfGravity is written UNBOUNDED (`brainOfGravity = whoAmI` in NPC.cs) but read behind
            // `>= 0 && < 200` in both of its two consumers. Above slot 199 that means the Expert-mode gravity
            // flip never happens AND the value is never cleared when the Brain dies, so it keeps pointing at a
            // slot that has since been reused. Write-wide / guard-narrow, the same shape as the immunity table.
            //
            // Dormant while boss zoning keeps the Brain under 200 - but "dormant because of zoning" was also
            // the argument for leaving the worm health bar alone, and the guaranteed-spawn path deliberately
            // overflows the boss zone. Both sites are patched together because a half-fix here is worse than
            // none: widening the Player.Update read while leaving Main's cleanup narrow would apply the
            // gravity effect from a stale index forever.
            Apply(mod, "Main.DoUpdateInWorld (brainOfGravity cleanup)",
                typeof(Main).GetMethod("DoUpdateInWorld", BindingFlags.NonPublic | BindingFlags.Instance),
                Patch_BrainOfGravityGate);
            Apply(mod, "Player.Update (brainOfGravity gravity flip)",
                typeof(Player).GetMethod("Update", BindingFlags.Public | BindingFlags.Instance),
                Patch_BrainOfGravityGate);

            // Talking to a town NPC in a high slot did not register the chat with the Bestiary, so that NPC
            // could never be completed there. Only reachable once the town zone overflows. One literal in a
            // twenty-line method, so blanket is exact.
            Apply(mod, "Player.SetTalkNPC (bestiary chat registration)",
                typeof(Player).GetMethod("SetTalkNPC", BindingFlags.Public | BindingFlags.Instance),
                Patch_AllNpcLoopBounds200);

            // -- Melee-class pass (0.7.7.2). Every literal count below was verified against the decompile;
            // "blanket-safe" means the method's ONLY 200-family literals are NPC-slot loop bounds.

            // Ghastly Glaive's ghast never had a target. On hit the glaive summons a ghast that is meant to
            // spawn beside a randomly chosen nearby enemy and fly through it; the candidate list is built by
            // a 0-199 scan, so it comes back empty and the ghast spawns at the glaive with a random heading
            // and wanders off. That ghast is most of the weapon's damage. One literal - blanket-safe.
            Apply(mod, "Projectile.SummonMonkGhast (Ghastly Glaive target list)",
                typeof(Projectile).GetMethod("SummonMonkGhast", BindingFlags.NonPublic | BindingFlags.Instance),
                Patch_AllNpcLoopBounds200);

            // Volatile Gelatin was completely inert. The accessory only fires its ball when its nearest-enemy
            // scan finds something, and the scan was 0-199. One literal - blanket-safe. Note this accessory
            // was ALSO gated by the perIDStaticNPCImmunity bug (its projectile sets usesIDStaticNPCImmunity),
            // so it had two independent reasons not to work; fixing one alone would have looked like a failed
            // fix, which is worth remembering the next time a "fixed" weapon is reported still broken.
            Apply(mod, "Player.VolatileGelatin (accessory target scan)",
                typeof(Player).GetMethod("VolatileGelatin", BindingFlags.Public | BindingFlags.Instance),
                Patch_AllNpcLoopBounds200);

            // Multi-part boss loot credit. Called from StrikeNPC on every hit; its seven loops propagate
            // "this player interacted" across the segments of Eater of Worlds, Destroyer, Skeletron, Wall of
            // Flesh, Golem and the Twins. Boss zoning normally keeps those low, so this is dormant - but the
            // guaranteed-spawn path deliberately spills the boss zone, and when it does, killing a part in a
            // high slot silently drops loot credit for the whole boss. All seven literals are loop bounds.
            Apply(mod, "NPC.PlayerInteraction (multi-part boss loot credit)",
                typeof(NPC).GetMethod("PlayerInteraction", BindingFlags.Public | BindingFlags.Instance),
                Patch_AllNpcLoopBounds200);

            // Multiplayer only. netOffset is the client-side interpolation offset, written unbounded by
            // MessageBuffer and added to npc.position around every hit test in Projectile.Damage and
            // ItemCheck_MeleeHitNPCs. The reset - called on teleport and camera pan - cleared only 0-199, so
            // a high-slot NPC keeps a stale offset across a teleport and melee hitboxes test against a
            // phantom position. Same write-wide / reset-narrow shape as the immunity table. One literal.
            Apply(mod, "NPC.ResetNetOffsets (stale interpolation offset)",
                typeof(NPC).GetMethod("ResetNetOffsets", BindingFlags.Public | BindingFlags.Static),
                Patch_AllNpcLoopBounds200);

            // Cosmetic: a spear stuck in a high-slot enemy (Daybreak, Bone Javelin) drew in the wrong layer,
            // because the "which NPC am I stuck in" check is `num >= 0 && num < 200`. One literal.
            Apply(mod, "Main.CacheProjDraws (stuck-spear draw layer)",
                typeof(Main).GetMethod("CacheProjDraws", BindingFlags.NonPublic | BindingFlags.Instance),
                Patch_AllNpcLoopBounds200);

            // Brain of Confusion's defensive half never fired - the on-hurt AoE confusion scans 0-199.
            // ANCHORED, not blanket: the method's other literal is `Main.rand.Next(200 + (int)num / 2, ...)`,
            // the confusion RADIUS. Blanket would turn a 200px radius into a 750px one and make the accessory
            // absurd. The npc-loop heuristic takes only the backward-branch bound, and a call argument is
            // neither.
            PatchNpcLoops(mod, typeof(Player), "OnHurt_Part3");

            // Minecart ramming dealt no damage to anything above slot 199, i.e. to essentially every enemy.
            // ANCHORED: Player.Update is 4013 lines with seven 200-family literals - `AddBuff(199, 3)` (a
            // buff id), two dust alphas, `nPC.direction * 200` (a distance), `num85 = 200` (a remix-world
            // depth constant) and a brainOfGravity sentinel. Only the minecart collision loop is a
            // backward-branch NPC loop, so the heuristic takes exactly one of the seven.
            PatchNpcLoops(mod, typeof(Player), "Update");

            // Projectile.Kill - the two gaps that have been on the open list since 0.7.2, now closed.
            //
            // Seedler's nut collects nearby enemies on death and aims its thorns at them; with the scan
            // stopping at 199 the thorns still spawn but fly in random directions, so the weapon's homing
            // shrapnel becomes a scatter of misses. The second loop is the Love/Foul Potion aura applying its
            // buff. Its companion buffer is `new int[Main.rand.Next(4, 8)]` with a length break, so it is
            // dynamically sized and the loop can be widened without touching it.
            //
            // This method has been flagged repeatedly as UNSAFE TO BLANKET and that has not changed: it holds
            // `width = 200; height = 200` (Dynamite's blast box), a colour channel, `Main.rand.Next(10, 201)`,
            // `Main.rand.Next(-200, 201)` and six firework staging guards. What makes the heuristic safe HERE
            // specifically is a property that does NOT hold inside NPC itself: a dust loop in Projectile.Kill
            // references Terraria.Projectile members, so the "body mentions an NPC member" test genuinely
            // discriminates. Verified against the decompile - exactly two `< 200` loops in 13672 lines, both
            // indexing Main.npc, and every other literal is a store, a call argument or a forward branch, and
            // TryFindLoopBodyStart rejects forward branches by construction.
            PatchNpcLoops(mod, typeof(Projectile), "Kill");

            // Daybreak's on-death debuff spread. A killed Daybreak-marked enemy is supposed to re-apply the
            // debuff to everything within 100px; the scan was 0-199, so in a crowd Daybreak loses its entire
            // chain value. See Patch_DaybreakSpread for why this one cannot use the heuristic.
            Apply(mod, "NPC.VanillaHitEffect (Daybreak debuff spread)",
                typeof(NPC).GetMethod("VanillaHitEffect", BindingFlags.NonPublic | BindingFlags.Instance),
                Patch_DaybreakSpread);

            // Critter release gate. Counts active NPCs against a float ceiling derived from 200 — see
            // Patch_CanReleaseNPCs for why the loop bound must NOT be widened on its own.
            Apply(mod, "NPC.CanReleaseNPCs (critter release density gate)",
                typeof(NPC).GetMethod("CanReleaseNPCs", BindingFlags.Public | BindingFlags.Static),
                Patch_CanReleaseNPCs);

            // Projectile.ResetImmunity — hit-immunity timestamps that survive a world change (0.7.7.1b).
            //
            // `perIDStaticNPCImmunity[projType][npcSlot]` holds the game tick after which that projectile type
            // may hit that NPC slot again. Two things run on EVERY world load, and together they break:
            //   Main.ResetGameCounter   -> Main.GameUpdateCount = 0        (Main.cs:6631, hooked at :6811)
            //   Projectile.ResetImmunity -> zeroes the table, but only `for (j = 0; j < 200; j++)`
            // So the counter restarts at zero while slots 200+ keep the timestamps written during the PREVIOUS
            // world — and every ordinary enemy lives above 199 under this mod. The gate is
            // `stored <= GameUpdateCount` (Projectile.cs:759), so a stale stamp of, say, 400000 blocks that
            // slot until the new session has run 400000 ticks. Play an hour, load another world, and those
            // slots are unhittable for about an hour.
            //
            // Symptom shape, which is why this took three reports to find: it only affects projectiles that set
            // `usesIDStaticNPCImmunity` (a SUBSET of weapons — hence "half of the whips and swords"), it needs
            // no crowding at all (one enemy is enough, it is per-slot state), plain broadswords are immune to
            // it because they never touch this table, and it CANNOT be reproduced in a freshly launched game:
            // the table is allocated zeroed, so the first world of a session is always clean. It appears on the
            // second and later world loads.
            //
            // One `ldc.i4 200` in the method and it is the loop bound, so the blanket manipulator is safe.
            Apply(mod, "Projectile.ResetImmunity (stale cross-world hit immunity)",
                typeof(Projectile).GetMethod(nameof(Projectile.ResetImmunity), BindingFlags.Public | BindingFlags.Static),
                Patch_AllNpcLoopBounds200);

            // Worm-boss health bar position. DrawInterface_Healthbar_Worm scans forward from the head for
            // the tail and draws the bar midway between them; the scan is `for (i = head.whoAmI + 1; i < 200)`.
            // Boss zoning normally keeps a worm boss under 200, so this only bites once the low zone overflows
            // — three Destroyers want ~246 slots against a default BossCap of 100, which the guaranteed-spawn
            // path deliberately allows to spill. Then the tail is never found, `nPC` stays as the head, and the
            // bar renders at the head instead of the worm's midpoint. 22-line method, one literal, the bound.
            //
            // Its initialiser is `head.whoAmI + 1`, not 0 — which is why the loop audit never listed it. The
            // inventory regex demanded `= 0;`. It now accepts any initialiser.
            Apply(mod, "Main.DrawInterface_Healthbar_Worm (tail seek)",
                typeof(Main).GetMethod("DrawInterface_Healthbar_Worm", BindingFlags.NonPublic | BindingFlags.Instance),
                Patch_AllNpcLoopBounds200);

            // ── Multi-part body chain walks (0.7.7.1). A DIFFERENT loop SHAPE from everything above, and the
            // reason these survived every earlier audit pass: a chain walk is a `while`, not a `for`, and the
            // audit's inventory regex only matched `for (int i = 0; i < 200; i++)`. It reported them as
            // nothing at all — not even as a GAP. tools/audit-npc-loops.sh now matches the while form too. ──

            // A worm is a head plus dozens of segments, each following the next through ai[0]/ai[1]. When one
            // despawns, the head is supposed to walk that chain and switch the whole body off silently. The
            // walk is bounded `num < 200`, and our allocator fills Enemy from the TOP down, so for our worms
            // the bound is never satisfied: the walk runs ZERO iterations and cleans up nothing. The head goes
            // inactive and leaves a live body behind, which then tears itself apart one segment at a time
            // through each segment's own orphan check — and that check runs HitEffect() at zero life, which is
            // the DEATH GORE. Loot is suppressed the whole way down because segments carry realLife = head.
            // So instead of a worm quietly vanishing you get body parts popping one by one and no drops.
            //
            // Every int 200 in all four methods is an NPC bound (checked against the decompiled 1.4.4 source:
            // 1, 3, 2 and 1 literals respectively; AI_006_Worms' fourth is `200f`, a float, which SafeLdcI4
            // cannot match anyway), so the blanket manipulator is safe here and throws if the engine changes.
            Apply(mod, "NPC.CheckActive_WormSegments (chain walk)",
                typeof(NPC).GetMethod("CheckActive_WormSegments", BindingFlags.NonPublic | BindingFlags.Instance),
                Patch_AllNpcLoopBounds200);

            // Same walk in the worm AI's biome-departure despawn (its type checks name the two Underground
            // Desert worms, Dune Splicer and Tomb Crawler, by number). Also widens the two separation scans
            // that keep worms from overlapping each other — those are per-segment per-tick, so this is the one
            // patch in this group with a real cost attached; measure before assuming it is free.
            Apply(mod, "NPC.AI_006_Worms (chain walk + separation)",
                typeof(NPC).GetMethod("AI_006_Worms", BindingFlags.NonPublic | BindingFlags.Instance),
                Patch_AllNpcLoopBounds200);

            // The Destroyer counts its own segments through the same kind of walk, and wipes them through a
            // 0-199 scan. Both blind above slot 199.
            Apply(mod, "NPC.AI_037_Destroyer (segment census)",
                typeof(NPC).GetMethod("AI_037_Destroyer", BindingFlags.NonPublic | BindingFlags.Instance),
                Patch_AllNpcLoopBounds200);

            // GetNPCLocation(seekHead: true) walks a segment chain back to its head. This is what the boss
            // health bar asks "which NPC am I actually drawing?", so a worm boss whose head sits above slot
            // 199 answers wrong -- the same failure class as the 0.7.6.6 health-bar bug.
            Apply(mod, "NPC.GetNPCLocation (worm-head seek)",
                typeof(NPC).GetMethod(nameof(NPC.GetNPCLocation), BindingFlags.Public | BindingFlags.Static),
                Patch_AllNpcLoopBounds200);

            // ── Dropped-item / NPC interactions (0.7.6.2). Terraria.Item was never covered by the loop audit
            // (it only decompiled Player/Projectile/Main/NPC), so these went unnoticed until a player reported
            // the Copper Slime not working. ──

            // Throwing a Copper Shortsword at a slime turns it into the Copper Town Slime. The dropped item
            // scans for a convertible slime in a hardcoded 0-199 loop, and slimes live in the expanded zone,
            // so it found nothing and the pet was unobtainable. Single loop in the method — safe to widen.
            PatchNpcLoops(mod, typeof(Item), "GetPickedUpByMonsters_Special");

            // Enemies stealing dropped coins ("lunch money") — same shape, so no enemy above slot 199 could
            // ever pick up a coin.
            PatchNpcLoops(mod, typeof(Item), "GetPickedUpByMonsters_Money");

            // AUDIT-SKIP: Item.CheckLavaDeath — its 0-199 scan looks only for the Guide (type 22) to kill when
            // a Guide Voodoo Doll hits lava. Town NPCs are always zoned into the low slots, so the loop already
            // finds him; widening it would be a no-op.

            // ── Dummy-slot sentinels (0.7.6.4). A DIFFERENT failure shape from the loop bounds above, and one
            // the loop audit cannot see. Vanilla uses the literal 200 as a safe "no NPC here" index precisely
            // BECAUSE Main.npc[200] is the inert dummy slot at the end of a 201-long array. Raising the cap
            // turns index 200 into an ordinary, occupied enemy slot, so those sentinels stop being harmless
            // and start aliasing a live NPC. ──
            Apply(mod, "NPC.SpawnNPC (dummy-slot sentinel)",
                typeof(NPC).GetMethod("SpawnNPC", BindingFlags.Public | BindingFlags.Static),
                Patch_SpawnNpcSentinel);

            // Whip / minion target marker: the reticle drawn over the minion-attack-target (and whip-tagged)
            // NPC is in DrawInterface_1_2_DrawEntityMarkersInWorld, which scans only 0-199 — so no marker
            // appears over enemies in slots 200+. Single NPC loop in the method, so blanket-safe.
            PatchMethod(mod, typeof(Main), "DrawInterface_1_2_DrawEntityMarkersInWorld");

            // Enemy health bars. The MAIN draw loop is a DESCENDING `for (num2 = 199; num2 >= 0; num2--)` —
            // a 199 literal the blanket 200-patch never touches, so with enemies in slots 200+ no bars draw.
            // Patch_HealthBars widens both that 199 and the two `< 200` helper loops.
            Apply(mod, "Main.DrawInterface_14_EntityHealthBars",
                typeof(Main).GetMethod("DrawInterface_14_EntityHealthBars", BindingFlags.NonPublic | BindingFlags.Instance),
                Patch_HealthBars);

            // BOSS health bar — a separate system from the enemy bars above, and a whole namespace the loop
            // audit never scanned (it reads Player/Projectile/Main/NPC/Item/DD2Event only).
            ApplyBossHealthBarPatches(mod);

            // ── Town NPC <-> enemy combat: let town NPCs/critters interact with enemies in the bonus zone. ──
            // GetHurtByOtherNPCs is the single chokepoint for town NPCs AND critters taking damage from
            // hostile NPCs (one clean npc loop). AI_007_TownEntities holds the town NPC's enemy-detect
            // (flee/approach) and melee-swing hit loops — anchored individually because that method also
            // contains unrelated `200` literals (defense value, AI timers) that must NOT be touched.
            PatchMethod(mod, typeof(NPC), "GetHurtByOtherNPCs");
            Apply(mod, "NPC.AI_007_TownEntities (town combat)",
                typeof(NPC).GetMethod("AI_007_TownEntities", BindingFlags.NonPublic | BindingFlags.Instance),
                Patch_TownNPCCombat);

            // ── Per-type spawn-limit counters. NPC.CountNPCS / NPC.AnyNPCs scan only 0-199, so every vanilla
            // "max N of this type" / "only spawn if none exist" check undercounts NPCs in the bonus zone and
            // over-spawns — most visibly the Moon minibosses (Mourning Wood / Pumpking / Everscream …) piling
            // up far past their 2/2/3 caps and tanking the framerate. Both are single clean NPC loops. ──
            PatchMethod(mod, typeof(NPC), "CountNPCS");
            PatchMethod(mod, typeof(NPC), "AnyNPCs");

            // ── Event length scaling (EventsConfig): high spawn rates make vanilla-size events end in
            // seconds. Detour Main.StartInvasion to multiply the invasion's size, and scale the Pumpkin/Frost
            // Moon per-wave point thresholds in-place. ──
            ApplyInvasionScaling(mod);
            Apply(mod, "NPC.CheckProgressFrostMoon (wave scaling)",
                typeof(NPC).GetMethod("CheckProgressFrostMoon", BindingFlags.NonPublic | BindingFlags.Instance),
                Patch_ScaleMoonWave);
            Apply(mod, "NPC.CheckProgressPumpkinMoon (wave scaling)",
                typeof(NPC).GetMethod("CheckProgressPumpkinMoon", BindingFlags.NonPublic | BindingFlags.Instance),
                Patch_ScaleMoonWave);

            // ── Old One's Army (v0.5). The v0.4 AnyNPCs fix already un-stalls the start (DD2Event stops the
            // event when it can't see the Eternia Crystal). These widen the remaining 0-199 scans so the
            // crystal/portals are found, enemies can target the crystal, and victory/loot/cleanup work. ──
            ApplyOldOnesArmyPatches(mod);

            // ── Per-item spawn modifiers: route each vanilla spawn-item constant in NPC.SpawnNPC through the
            // user-configurable strength knobs in SpawnItemsConfig (see SpawnItemModifiers). ──
            Apply(mod, "NPC.SpawnNPC (spawn-item modifiers)",
                typeof(NPC).GetMethod("SpawnNPC", BindingFlags.Public | BindingFlags.Static),
                Patch_SpawnItemModifiers);

            // ── Rare "lottery" spawns: widen their per-attempt odds so cranking the spawn rate doesn't
            // multiply how often they fire (see RareSpawnNormalizer). ──
            Apply(mod, "NPC.SpawnNPC (rare-spawn rolls)",
                typeof(NPC).GetMethod("SpawnNPC", BindingFlags.Public | BindingFlags.Static),
                Patch_RareSpawnRolls);
        }

        /// <summary>
        /// Scale the two per-spawn-attempt rare rolls in <c>NPC.SpawnNPC</c> by the spawn-rate amplification,
        /// so they keep vanilla's expected frequency however high the spawn dial goes.
        /// <list type="bullet">
        /// <item><b>King Slime</b> — <c>Main.rand.Next(300) == 0 &amp;&amp; !AnyNPCs(50)</c></item>
        /// <item><b>Prismatic Lacewing</b> — <c>RollLuck(10) == 0 &amp;&amp; !AnyNPCs(661)</c></item>
        /// </list>
        /// Both are anchored on their unique <c>AnyNPCs(&lt;type&gt;)</c> guard and then walked BACKWARD to the
        /// odds literal, because the bare literals (300, 10) are far from unique in a method this size. We
        /// insert a call after the literal rather than replacing it, so the odds stay a plain
        /// <c>int -&gt; int</c> transform and the surrounding IL is untouched.
        /// <para/>
        /// Scoped deliberately to these two. <c>SpawnNPC</c> contains a whole family of the same pattern —
        /// bound Goblin/Wizard (<c>RollLuck(20)</c>), bound slimes (25/30), Nymph (30), and the many
        /// <c>goldCritterChance</c> rolls. The rescue NPCs are deliberately LEFT inflated (players want to
        /// find them, and 0.7.1 went to some trouble to make them spawnable at all), and the gold-critter
        /// rolls use a variable rather than a literal so they have no stable anchor. Each is patched in its
        /// own try/catch: one missing anchor is logged and skipped, never fatal.
        /// </summary>
        private static void Patch_RareSpawnRolls(ILContext il)
        {
            MethodInfo anyNpcs = typeof(NPC).GetMethod(nameof(NPC.AnyNPCs), BindingFlags.Public | BindingFlags.Static);
            MethodInfo scaleOdds = typeof(RareSpawnNormalizer).GetMethod(nameof(RareSpawnNormalizer.ScaleOdds),
                BindingFlags.Public | BindingFlags.Static);
            MethodInfo scaleLacewing = typeof(RareSpawnNormalizer).GetMethod(nameof(RareSpawnNormalizer.ScaleLacewingOdds),
                BindingFlags.Public | BindingFlags.Static);

            if (anyNpcs == null || scaleOdds == null || scaleLacewing == null)
                throw new Exception("rare-spawn anchors/target not resolvable");

            // Insert `-> <scaler>(...)` immediately after the odds literal the cursor is sitting before,
            // turning `Next(300)` into `Next(ScaleOdds(300))`.
            void ScaleAfterLiteral(ILCursor c, MethodInfo scaler)
            {
                c.Index++;
                c.Emit(OpCodes.Call, il.Import(scaler));
            }

            void Roll(string name, Action act)
            {
                try { act(); }
                catch (Exception e) { ModRef?.Logger.Warn($"[MMM] rare-spawn roll '{name}' not patched (skipped): {e.Message}"); }
            }

            Roll("King Slime", () =>
            {
                var c = new ILCursor(il);
                // `... && Main.rand.Next(300) == 0 && !AnyNPCs(50)` — anchor on the AnyNPCs(50) guard.
                if (!c.TryGotoNext(i => SafeLdcI4(i, 50), i => i.MatchCall(anyNpcs)))
                    throw new Exception("AnyNPCs(50) anchor");
                if (!c.TryGotoPrev(i => SafeLdcI4(i, 300)))
                    throw new Exception("rand.Next(300) literal");
                ScaleAfterLiteral(c, scaleOdds);
            });

            Roll("Prismatic Lacewing", () =>
            {
                var c = new ILCursor(il);
                // `... && Main.player[k].RollLuck(10) == 0 && !AnyNPCs(661)` — anchor on the AnyNPCs(661) guard,
                // which is the only one in the game, then step back onto RollLuck's own argument.
                if (!c.TryGotoNext(i => SafeLdcI4(i, 661), i => i.MatchCall(anyNpcs)))
                    throw new Exception("AnyNPCs(661) anchor");
                if (!c.TryGotoPrev(i => SafeLdcI4(i, 10),
                                   i => i.Operand is MethodReference mr && mr.Name == nameof(Player.RollLuck)))
                    throw new Exception("RollLuck(10) literal");
                // Lacewing keeps a boosted rate outside the post-Empress cooldown — see ScaleLacewingOdds.
                ScaleAfterLiteral(c, scaleLacewing);
            });
        }

        // NPC.SpawnNPC applies a list of player-driven spawn modifiers as hardcoded constants. We replace each
        // constant with a call to SpawnItemModifiers, so the user's per-item strength scales it. Every item is
        // anchored on its own unique flag (field load or property getter) and patched independently in its own
        // try/catch: a single missing anchor is logged and skipped, leaving the others (and the game) intact.
        private static void Patch_SpawnItemModifiers(ILContext il)
        {
            var P = typeof(Player);
            var SIM = typeof(SpawnItemModifiers);

            // Replace the float/double constant the cursor is positioned before with a call to SIM.<method>.
            void Call(ILCursor c, string method)
            {
                c.Next.OpCode = OpCodes.Call;
                c.Next.Operand = il.Import(SIM.GetMethod(method, BindingFlags.Public | BindingFlags.Static));
            }

            // Anchor predicates.
            bool Field(Instruction i, string name) => i.MatchLdfld(P.GetField(name));
            bool MethodNamed(Instruction i, string name) => i.Operand is MethodReference mr && mr.Name == name;

            void Item(string name, Action act)
            {
                try { act(); }
                catch (Exception e) { ModRef?.Logger.Warn($"[MMM] spawn-item '{name}' not patched (skipped): {e.Message}"); }
            }

            // Each block is `if (flag) { spawnRate = (..)spawnRate * RATE; maxSpawns = (..)maxSpawns * MAX; }`.
            // Anchoring on the flag first means the value-matched constants resolve to that block's copies even
            // when the same literal (e.g. 1.2f / 0.8f) appears in several blocks.
            Item("Invisibility", () =>
            {
                var c = new ILCursor(il);
                if (!c.TryGotoNext(i => Field(i, nameof(Player.invis)))) throw new Exception("flag");
                if (!c.TryGotoNext(i => i.MatchLdcR4(1.2f))) throw new Exception("rate");
                Call(c, nameof(SpawnItemModifiers.InvisRate));
                if (!c.TryGotoNext(i => i.MatchLdcR4(0.8f))) throw new Exception("max");
                Call(c, nameof(SpawnItemModifiers.InvisMax));
            });

            Item("CalmingPotion", () =>
            {
                var c = new ILCursor(il);
                if (!c.TryGotoNext(i => Field(i, nameof(Player.calmed)))) throw new Exception("flag");
                if (!c.TryGotoNext(i => i.MatchLdcR4(1.65f))) throw new Exception("rate");
                Call(c, nameof(SpawnItemModifiers.CalmingRate));
                if (!c.TryGotoNext(i => i.MatchLdcR4(0.6f))) throw new Exception("max");
                Call(c, nameof(SpawnItemModifiers.CalmingMax));
            });

            Item("Sunflower", () =>
            {
                var c = new ILCursor(il);
                if (!c.TryGotoNext(i => Field(i, nameof(Player.sunflower)))) throw new Exception("flag");
                if (!c.TryGotoNext(i => i.MatchLdcR4(1.2f))) throw new Exception("rate");
                Call(c, nameof(SpawnItemModifiers.SunflowerRate));
                if (!c.TryGotoNext(i => i.MatchLdcR4(0.8f))) throw new Exception("max");
                Call(c, nameof(SpawnItemModifiers.SunflowerMax));
            });

            Item("AnglerSet", () =>
            {
                var c = new ILCursor(il);
                if (!c.TryGotoNext(i => Field(i, nameof(Player.anglerSetSpawnReduction)))) throw new Exception("flag");
                if (!c.TryGotoNext(i => i.MatchLdcR4(1.3f))) throw new Exception("rate");
                Call(c, nameof(SpawnItemModifiers.AnglerRate));
                if (!c.TryGotoNext(i => i.MatchLdcR4(0.7f))) throw new Exception("max");
                Call(c, nameof(SpawnItemModifiers.AnglerMax));
            });

            Item("BattlePotion", () =>
            {
                var c = new ILCursor(il);
                if (!c.TryGotoNext(i => Field(i, nameof(Player.enemySpawns)))) throw new Exception("flag");
                if (!c.TryGotoNext(i => i.MatchLdcR8(0.5))) throw new Exception("rate");
                Call(c, nameof(SpawnItemModifiers.BattleRate));
                if (!c.TryGotoNext(i => i.MatchLdcR4(2f))) throw new Exception("max");
                Call(c, nameof(SpawnItemModifiers.BattleMax));
            });

            Item("WaterCandle", () =>
            {
                var c = new ILCursor(il);
                if (!c.TryGotoNext(i => MethodNamed(i, "get_ZoneWaterCandle"))) throw new Exception("flag");
                if (!c.TryGotoNext(i => i.MatchLdcR8(0.75))) throw new Exception("rate");
                Call(c, nameof(SpawnItemModifiers.WaterRate));
                if (!c.TryGotoNext(i => i.MatchLdcR4(1.5f))) throw new Exception("max");
                Call(c, nameof(SpawnItemModifiers.WaterMax));
            });

            Item("PeaceCandle", () =>
            {
                var c = new ILCursor(il);
                if (!c.TryGotoNext(i => MethodNamed(i, "get_ZonePeaceCandle"))) throw new Exception("flag");
                if (!c.TryGotoNext(i => i.MatchLdcR8(1.3))) throw new Exception("rate");
                Call(c, nameof(SpawnItemModifiers.PeaceRate));
                if (!c.TryGotoNext(i => i.MatchLdcR4(0.7f))) throw new Exception("max");
                Call(c, nameof(SpawnItemModifiers.PeaceMax));
            });

            // Water Candle deep-cave bonus: a SECOND `get_ZoneWaterCandle` block, `spawnRate *= 0.5`.
            Item("WaterCandleDeep", () =>
            {
                var c = new ILCursor(il);
                if (!c.TryGotoNext(i => MethodNamed(i, "get_ZoneWaterCandle"))) throw new Exception("flag #1");
                if (!c.TryGotoNext(i => MethodNamed(i, "get_ZoneWaterCandle"))) throw new Exception("flag #2");
                if (!c.TryGotoNext(i => i.MatchLdcR8(0.5))) throw new Exception("rate");
                Call(c, nameof(SpawnItemModifiers.WaterDeepRate));
            });

            Item("Fairy", () =>
            {
                var c = new ILCursor(il);
                if (!c.TryGotoNext(i => MethodNamed(i, "isNearFairy"))) throw new Exception("flag");
                if (!c.TryGotoNext(i => i.MatchLdcR4(1.2f))) throw new Exception("rate");
                Call(c, nameof(SpawnItemModifiers.FairyRate));
                if (!c.TryGotoNext(i => i.MatchLdcR4(0.8f))) throw new Exception("max");
                Call(c, nameof(SpawnItemModifiers.FairyMax));
            });

            // Shadow Candle: vanilla does `townNPCs = 0f`. Rewrite to `townNPCs = townNPCs * ShadowFactor()`,
            // so strength scales the suppression (1 => 0/full vanilla, 0 => unchanged).
            Item("ShadowCandle", () =>
            {
                var c = new ILCursor(il);
                FieldInfo townNPCs = P.GetField(nameof(Player.townNPCs));
                if (!c.TryGotoNext(i => MethodNamed(i, "get_ZoneShadowCandle"))) throw new Exception("flag");
                if (!c.TryGotoNext(i => i.MatchStfld(townNPCs))) throw new Exception("townNPCs store");

                // The instruction before the store is `ldc.r4 0` (the value). Replace it with the player ref's
                // current townNPCs * factor. At this point the stack already holds the player reference.
                c.Index--;
                if (!c.Next.MatchLdcR4(0f)) throw new Exception("expected `ldc.r4 0`");
                c.Remove();
                c.Emit(OpCodes.Dup);                 // duplicate the player ref
                c.Emit(OpCodes.Ldfld, townNPCs);     // current townNPCs
                c.Emit(OpCodes.Call, SIM.GetMethod(nameof(SpawnItemModifiers.ShadowFactor), BindingFlags.Public | BindingFlags.Static));
                c.Emit(OpCodes.Mul);                 // townNPCs * factor
            });
        }

        // Two enemy-scan loops in the town-NPC AI, anchored so the method's non-NPC 200s are left alone:
        //  A) enemy detection (flee/fight) — the loop right after the NPCID.Sets.DangerDetectRange lookup.
        //  B) melee-swing hit detection — the loop right after the TweakSwingStats call.
        private static void Patch_TownNPCCombat(ILContext il)
        {
            var c = new ILCursor(il);

            if (!(c.TryGotoNext(i => i.MatchLdsfld(out FieldReference f) && f.Name == "DangerDetectRange")
                  && c.TryGotoNext(MoveType.Before, i => SafeLdcI4(i, 200))))
                throw new Exception("town danger-detect loop not found");
            ReplaceWithNpcCap(c, il);

            if (!(c.TryGotoNext(i => i.MatchCall(out MethodReference m) && m.Name == "TweakSwingStats")
                  && c.TryGotoNext(MoveType.Before, i => SafeLdcI4(i, 200))))
                throw new Exception("town melee-attack loop not found");
            ReplaceWithNpcCap(c, il);
        }

        // ItemCheck_MeleeHitNPCs: route the `attackCD` gate through EngineState.MeleeAttackGate (so the
        // "melee hits all in swing" toggle works at runtime) AND widen the npc loop. The attackCD read lives
        // in the loop body, which precedes the loop-bound `200` in IL, so patch it first.
        private static void Patch_MeleeHitNPCs(ILContext il)
        {
            var c = new ILCursor(il);

            FieldInfo attackCD = typeof(Player).GetField(nameof(Player.attackCD));
            if (!c.TryGotoNext(i => i.MatchLdfld(attackCD)))
                throw new Exception("attackCD read not found");
            c.Next.OpCode = OpCodes.Call;
            c.Next.Operand = il.Import(typeof(EngineState).GetMethod(nameof(EngineState.MeleeAttackGate)));

            if (!c.TryGotoNext(MoveType.Before, i => SafeLdcI4(i, 200)))
                throw new Exception("melee loop bound not found");
            ReplaceWithNpcCap(c, il);
        }

        // Projectile.Update: replace the `localNPCImmunity` decrement loop's hardcoded `200` bound with the
        // array's actual length, so per-NPC hit cooldowns clear for enemies in slots 200+ (otherwise a minion
        // can only ever hit a high-slot enemy once). Anchored on the `usesLocalNPCImmunity` field read that
        // immediately precedes the loop. Uses `localNPCImmunity.Length` (not NpcCap) so it can never overrun a
        // projectile whose array is, for any reason, shorter than the cap.
        private static void Patch_LocalImmunityDecrement(ILContext il)
        {
            var c = new ILCursor(il);
            FieldInfo uses = typeof(Projectile).GetField(nameof(Projectile.usesLocalNPCImmunity));
            FieldInfo arr = typeof(Projectile).GetField(nameof(Projectile.localNPCImmunity));

            if (!c.TryGotoNext(i => i.MatchLdfld(uses)))
                throw new Exception("usesLocalNPCImmunity anchor not found");
            if (!c.TryGotoNext(MoveType.Before, i => SafeLdcI4(i, 200)))
                throw new Exception("localNPCImmunity decrement bound `ldc.i4 200` not found");

            c.Remove();                          // remove the `ldc.i4 200` loop bound
            c.Emit(OpCodes.Ldarg_0);             // this
            c.Emit(OpCodes.Ldfld, arr);          // this.localNPCImmunity
            c.Emit(OpCodes.Ldlen);               // length (native int)
            c.Emit(OpCodes.Conv_I4);             // -> int  => loop runs k < localNPCImmunity.Length
        }

        // Projectile.ResetLocalNPCHitImmunity(): `for (int i = 0; i < 200; i++) localNPCImmunity[i] = 0;`.
        // Replace the single `ldc.i4 200` bound with `this.localNPCImmunity.Length` so the reset clears the
        // whole (raised) array — otherwise high-slot enemies keep their stale -1 "hit once" flag forever.
        private static void Patch_ResetLocalImmunity(ILContext il)
        {
            var c = new ILCursor(il);
            FieldInfo arr = typeof(Projectile).GetField(nameof(Projectile.localNPCImmunity));

            if (!c.TryGotoNext(MoveType.Before, i => SafeLdcI4(i, 200)))
                throw new Exception("ResetLocalNPCHitImmunity bound `ldc.i4 200` not found");

            c.Remove();                          // remove the `ldc.i4 200` loop bound
            c.Emit(OpCodes.Ldarg_0);             // this
            c.Emit(OpCodes.Ldfld, arr);          // this.localNPCImmunity
            c.Emit(OpCodes.Ldlen);               // length (native int)
            c.Emit(OpCodes.Conv_I4);             // -> int  => loop runs i < localNPCImmunity.Length
        }

        // Fixes the two NPC-scanning info accessories in Main.DrawInfoAccs. Radar: anchored on the unique
        // NPC.dontCountMe read inside its count loop, widen the loop bound, then (a) route
        // `accThirdEyeNumber++` through SaturatingByteInc, which saturates the vanilla byte at 255 (so its
        // "any enemies nearby?" gate stays sane) while tallying the TRUE count into an int, and (b) swap the
        // displayed number to read that int, so the HUD shows the real count instead of "255".
        // Lifeform Analyzer: see Patch_LifeformAnalyzer below.
        private static void Patch_InfoAccessories(ILContext il)
        {
            Patch_LifeformAnalyzer(il);
            var c = new ILCursor(il);
            FieldInfo dontCountMe = typeof(NPC).GetField(nameof(NPC.dontCountMe));

            if (!c.TryGotoNext(i => i.MatchLdfld(dontCountMe)))
                throw new Exception("dontCountMe anchor not found");
            if (!c.TryGotoNext(MoveType.Before, i => SafeLdcI4(i, 200)))
                throw new Exception("radar count loop bound not found");

            ReplaceWithNpcCap(c, il);

            // accThirdEyeNumber++ compiles to `ldfld accThirdEyeNumber; ldc.i4.1; add; conv.u1; stfld`.
            // Swap the `add` for a saturating add so the byte tops out at 255 rather than wrapping.
            var c2 = new ILCursor(il);
            FieldInfo eye = typeof(Player).GetField(nameof(Player.accThirdEyeNumber));
            if (c2.TryGotoNext(i => i.MatchLdfld(eye)) && c2.TryGotoNext(i => i.OpCode == OpCodes.Add))
            {
                c2.Next.OpCode = OpCodes.Call;
                c2.Next.Operand = il.Import(typeof(EngineState).GetMethod(nameof(EngineState.SaturatingByteInc)));
            }
            else
            {
                ModRef?.Logger.Warn("[MMM] radar count clamp not applied (accThirdEyeNumber++ anchor not found).");
            }

            // Show the TRUE count instead of the byte-capped 255. The displayed number is the `accThirdEyeNumber`
            // read fed into GetTextValue("GameUI.EnemiesNearby", ...); anchor on that string literal, then swap
            // the following `ldfld accThirdEyeNumber` for a call returning our uncapped int (the call consumes
            // the Player the field-read had loaded, so the stack stays balanced), and widen the box that follows
            // from System.Byte to System.Int32 so values >255 aren't truncated to a byte.
            var c3 = new ILCursor(il);
            if (c3.TryGotoNext(i => i.MatchLdstr("GameUI.EnemiesNearby")) && c3.TryGotoNext(i => i.MatchLdfld(eye)))
            {
                c3.Next.OpCode = OpCodes.Call;
                c3.Next.Operand = il.Import(typeof(EngineState).GetMethod(nameof(EngineState.GetRadarDisplayCount)));
                if (c3.TryGotoNext(i => i.OpCode == OpCodes.Box))
                    c3.Next.Operand = il.Import(typeof(int));
            }
            else
            {
                ModRef?.Logger.Warn("[MMM] radar true-count display not applied (EnemiesNearby anchor not found).");
            }
        }

        // Lifeform Analyzer (rare creature detector) — three fixes, all anchored inside its block (which sits
        // BEFORE the radar block in DrawInfoAccs, scanning `npc[k].rarity > 0`, which covers rare enemies AND
        // rare critters like golden critters / the Truffle Worm — exactly what lives in the expanded zone):
        //  1. widen its `< 200` scan loop to the cap,
        //  2. its found-NPC SLOT INDEX is stored in `byte accCritterGuideNumber` (slot 300 truncates to 44 and
        //     the HUD names the wrong NPC) — record the true slot via TrackLifeformSlot before the byte store,
        //  3. the cached read of that byte on non-scan frames -> GetLifeformSlot (the true slot), and widen the
        //     display's own `num < 200` validity check so expanded-zone slots aren't rejected.
        private static void Patch_LifeformAnalyzer(ILContext il)
        {
            var c = new ILCursor(il);
            FieldInfo rarity = typeof(NPC).GetField(nameof(NPC.rarity));
            FieldInfo guide = typeof(Player).GetField(nameof(Player.accCritterGuideNumber));

            // 1. Scan loop bound: the first NPC.rarity read in the method is inside the analyzer's scan loop;
            // the loop's `ldc.i4 200` bound check sits just after the body.
            if (!c.TryGotoNext(i => i.MatchLdfld(rarity)))
                throw new Exception("analyzer rarity anchor not found");
            if (!c.TryGotoNext(MoveType.Before, i => SafeLdcI4(i, 200)))
                throw new Exception("analyzer scan loop bound not found");
            ReplaceWithNpcCap(c, il);

            // 3b. Display validity check `num14 < 200` — the next 200 after the loop bound, still inside the
            // analyzer block (the radar's own 200 comes later and is handled by the radar patch).
            if (!c.TryGotoNext(MoveType.Before, i => SafeLdcI4(i, 200)))
                throw new Exception("analyzer display bound not found");
            ReplaceWithNpcCap(c, il);

            // 2. Store site: `accCritterGuideNumber = (byte)num14` compiles to `ldloc num14; conv.u1; stfld`.
            // Insert a pass-through recorder before the truncation so the true slot lands in our int.
            var c2 = new ILCursor(il);
            if (c2.TryGotoNext(MoveType.Before, i => i.OpCode == OpCodes.Conv_U1, i => i.MatchStfld(guide)))
            {
                c2.Emit(OpCodes.Call, il.Import(typeof(EngineState).GetMethod(nameof(EngineState.TrackLifeformSlot))));
            }
            else
            {
                ModRef?.Logger.Warn("[MMM] analyzer slot tracker not applied (accCritterGuideNumber store anchor not found).");
            }

            // 3a. Cached read on non-scan frames: swap the byte field read for the true tracked slot (the call
            // consumes the Player the field-read had loaded, so the stack stays balanced).
            var c3 = new ILCursor(il);
            if (c3.TryGotoNext(i => i.MatchLdfld(guide)))
            {
                c3.Next.OpCode = OpCodes.Call;
                c3.Next.Operand = il.Import(typeof(EngineState).GetMethod(nameof(EngineState.GetLifeformSlot)));
            }
            else
            {
                ModRef?.Logger.Warn("[MMM] analyzer true-slot read not applied (accCritterGuideNumber read anchor not found).");
            }

            // 4. QoL: prioritize rescueable NPCs. The scan picks by raw rarity, so a golden critter (3+)
            // out-shines a Bound Goblin (1) and the display just reads "Gold Bunny" while your Goblin
            // Tinkerer waits. Swap every rarity read in the block for LifeformPriorityScore — verified: the
            // analyzer's three reads (loop compare, best-so-far store, display validity check) are the ONLY
            // NPC.rarity reads in DrawInfoAccs, and all three must agree or a boosted pick would be
            // re-out-scored / rejected at display time.
            var c4 = new ILCursor(il);
            var score = il.Import(typeof(EngineState).GetMethod(nameof(EngineState.LifeformPriorityScore)));
            int swapped = 0;
            while (swapped < 3 && c4.TryGotoNext(i => i.MatchLdfld(rarity)))
            {
                c4.Next.OpCode = OpCodes.Call;
                c4.Next.Operand = score;
                swapped++;
            }
            if (swapped != 3)
                ModRef?.Logger.Warn($"[MMM] analyzer rescue-priority incomplete ({swapped}/3 rarity reads swapped).");
        }

        // Enemy health bars: widen the two ascending `< 200` helper loops AND the descending main draw loop,
        // which starts at the literal 199 (so it isn't caught by the 200 replacement). Without the 199 fix the
        // main loop only draws slots 0-199 — where almost no enemies live after slot-zoning.
        private static void Patch_HealthBars(ILContext il)
        {
            var c = new ILCursor(il);
            int n200 = 0;
            while (c.TryGotoNext(MoveType.Before, i => SafeLdcI4(i, 200)))
            {
                ReplaceWithNpcCap(c, il);
                c.Index++;
                n200++;
            }
            if (n200 == 0)
                throw new Exception("health-bar `ldc.i4 200` helper loops not found");

            var c2 = new ILCursor(il);
            if (!c2.TryGotoNext(MoveType.Before, i => SafeLdcI4(i, 199)))
                throw new Exception("health-bar main draw loop init `ldc.i4 199` not found");
            ReplaceWithNpcCap(c2, il); // 199 -> NpcCap
            c2.Index++;
            c2.Emit(OpCodes.Ldc_I4_1);
            c2.Emit(OpCodes.Sub);      // NpcCap - 1
        }

        /// <summary>
        /// Moves <c>NPC.SpawnNPC</c>'s "nothing spawned" sentinel off the now-occupied slot 200.
        /// <para/>
        /// The method opens with <c>int newNPC = 200;</c> and closes with, unconditionally:
        /// <code>
        /// if (Main.npc[newNPC].type == 1 &amp;&amp; Main.player[k].RollLuck(180) == 0)
        ///     Main.npc[newNPC].SetDefaults(-4);              // Blue Slime -> Pinky
        /// </code>
        /// In vanilla that is safe by construction: <c>Main.npc[200]</c> is the dummy slot at the end of a
        /// 201-long array, permanently type 0, so the test can never pass. Raise the cap and slot 200 becomes
        /// an ordinary expanded enemy slot — so on any spawn attempt that leaves the sentinel untouched, the
        /// game inspects whoever is standing in slot 200 and can reroll them into a Pinky (or, on a tenth
        /// anniversary world, a Bunny) in place. Rare, cosmetic, and utterly unattributable in a bug report.
        /// <para/>
        /// <b>Anchored, not blanket:</b> the method holds 15 separate <c>200</c> literals. The one
        /// <c>call NPCLoader.SpawnNPC</c> in the method is followed by the store that names <c>newNPC</c>, and
        /// exactly one <c>ldc.i4 200</c> in the whole body feeds a store to that same local — the initializer.
        /// <para/>
        /// This is a cap consequence, not a network one, so it lives here and applies unconditionally. The
        /// multiplayer patcher widens the same class of sentinel in the Old One's Army gate spawners, where
        /// the only symptom is a redundant broadcast; whichever runs first wins and the other finds nothing.
        /// </summary>
        private static void Patch_SpawnNpcSentinel(ILContext il)
        {
            var c = new ILCursor(il);
            if (!c.TryGotoNext(i => i.Operand is MethodReference mr
                                    && mr.Name == "SpawnNPC"
                                    && mr.DeclaringType?.Name == "NPCLoader"))
                throw new Exception("anchor call NPCLoader.SpawnNPC not found");

            // The store immediately after the call is `newNPC = ...`.
            Instruction store = c.Next?.Next;
            while (store != null && store.OpCode == OpCodes.Nop)
                store = store.Next;

            int local = StoreLocalIndex(store);
            if (local < 0)
                throw new Exception("store into newNPC not found after the anchor");

            var c2 = new ILCursor(il);
            int widened = 0;
            while (c2.TryGotoNext(MoveType.Before, i => SafeLdcI4(i, 200)))
            {
                Instruction after = c2.Next.Next;
                while (after != null && after.OpCode == OpCodes.Nop)
                    after = after.Next;

                if (StoreLocalIndex(after) == local)
                {
                    ReplaceWithNpcCap(c2, il);
                    widened++;
                }
                c2.Index++;
            }

            if (widened != 1)
                throw new Exception($"expected exactly one `ldc.i4 200 -> stloc newNPC`, found {widened}");
        }

        /// <summary>Local index a stloc writes to, in any encoding; -1 if the instruction is not a store.</summary>
        private static int StoreLocalIndex(Instruction instr)
        {
            if (instr == null)
                return -1;

            OpCode op = instr.OpCode;
            if (op == OpCodes.Stloc_0) return 0;
            if (op == OpCodes.Stloc_1) return 1;
            if (op == OpCodes.Stloc_2) return 2;
            if (op == OpCodes.Stloc_3) return 3;
            if (op != OpCodes.Stloc && op != OpCodes.Stloc_S) return -1;

            return instr.Operand is VariableDefinition v ? v.Index : -1;
        }


        /// <summary>Local slot a <c>ldloc</c> family instruction reads, or -1. Mirror of <see cref="StoreLocalIndex"/>.</summary>
        private static int LoadLocalIndex(Instruction instr)
        {
            if (instr == null)
                return -1;

            OpCode op = instr.OpCode;
            if (op == OpCodes.Ldloc_0) return 0;
            if (op == OpCodes.Ldloc_1) return 1;
            if (op == OpCodes.Ldloc_2) return 2;
            if (op == OpCodes.Ldloc_3) return 3;
            if (op != OpCodes.Ldloc && op != OpCodes.Ldloc_S) return -1;

            return instr.Operand is VariableDefinition v ? v.Index : -1;
        }

        /// <summary>
        /// Widen the "is my stored target slot valid?" gates in <c>Projectile.VanillaAI</c> and friends.
        /// <para/>
        /// These are NOT loops, so no loop patcher can reach them, and the method is 20,000 lines with over a
        /// hundred lines carrying a 200/199/201 — blanket replacement would be reckless. Both forms are
        /// anchored on the shape of the comparison itself.
        /// <para/>
        /// <b>Callers must declare how many of each form they expect, and a shortfall throws.</b> The previous
        /// version failed only when BOTH forms matched zero, and that is exactly how it hid: in VanillaAI it
        /// matched the one float gate, reported "0 int, 1 float", and silently left all THREE int gates at 200
        /// for a whole version. "Zero of the thing I came for" has to be a failure, not a log line — the only
        /// reason this was ever caught is that this manipulator happens to print its counts.
        /// <list type="bullet">
        /// <item><b>Stuck projectiles (aiStyle 113).</b> <c>if (slot &lt; 0 || slot &gt;= 200) Kill();</c> — and the
        /// slot is written by our OWN widened loop in <c>Damage</c> (<c>ai[1] = i</c>). Bone Javelin, Daybreak,
        /// Tentacle Spike and Blood Butcherer stick for one frame, then die. Their damage-over-time is not a
        /// debuff — the NPC COUNTS live stuck projectiles each tick — so killing them removes 100% of it.
        /// Daybreak's counter multiplies by 100; that is the entire weapon.</item>
        /// <item><b>Horseman's Blade pumpkins (aiStyle 55).</b> <c>if (ai[0] &gt;= 0f &amp;&amp; ai[0] &lt; 200f)</c> …
        /// <c>else Kill();</c> A FLOAT literal, which <see cref="SafeLdcI4"/> cannot match, so it stayed at 200
        /// while we widened the re-target scan that writes it — we made this one worse, not better.</item>
        /// </list>
        /// </summary>
        private static void Patch_TargetSlotGates(ILContext il, int expectInt, int expectFloat)
        {
            var instrs = il.Body.Instructions;
            int intGates = 0, floatGates = 0;

            // Pass 1 — which locals are PROVEN to be NPC slot indices? A local L qualifies when the method
            // contains `ldsfld Main::npc · ldloc L · ldelem.ref` anywhere, i.e. it is literally used to index
            // Main.npc. This replaced a fixed 30-instruction look-ahead, and the difference is not cosmetic:
            // VanillaAI's third gate guards a DUST emitter whose body never touches Main.npc at all, so no
            // look-ahead of any size could ever have classified it. It is the same variable the other two
            // gates guard, and a slot local is a slot local everywhere in the method.
            var slotLocals = new HashSet<int>();
            for (int j = 0; j < instrs.Count; j++)
            {
                int L = LoadLocalIndex(instrs[j]);
                if (L < 0)
                    continue;
                Instruction before = PrevReal(instrs, j);
                if (before?.OpCode == OpCodes.Ldsfld
                    && before.Operand is FieldReference npcField
                    && npcField.Name == "npc"
                    && npcField.DeclaringType?.Name == "Main"
                    && NextReal(instrs[j])?.OpCode == OpCodes.Ldelem_Ref)
                {
                    slotLocals.Add(L);
                }
            }

            for (int j = 0; j < instrs.Count; j++)
            {
                Instruction cur = instrs[j];

                // ── int form: `ldloc X; ldc.i4 200; <cond branch>` where X indexes Main.npc shortly after.
                // PrevReal, not instrs[j - 1]: in a 255+-local method the real predecessor is `ldloc X` but the
                // raw neighbour is a nop, so this arm matched nothing at all. See PrevReal for the measurement.
                if (SafeLdcI4(cur, 200) && !TryFindLoopBodyStart(instrs, j, out _))
                {
                    int local = LoadLocalIndex(PrevReal(instrs, j));
                    Instruction branch = NextReal(cur);
                    if (local >= 0 && slotLocals.Contains(local)
                        && branch != null && branch.OpCode.FlowControl == FlowControl.Cond_Branch)
                    {
                        cur.OpCode = OpCodes.Ldsfld;
                        cur.Operand = il.Import(NpcCapField);
                        intGates++;
                    }
                }

                // ── float form: `ldelem.r4; ldc.r4 200; <cond branch>` — reading an ai[] slot, not a local.
                // The ldelem.r4 predecessor is what separates it from the 13 other 200f values in this method
                // (distances, colours, positions), every one of which must be left alone.
                // ldelem.r4 is never nop-padded, so this arm was never broken; it goes through the same
                // helpers anyway so the two arms cannot drift apart the next time the encoding shifts.
                //
                // LoadsNpcArrayWithin is what makes this arm safe to point at a new method, and it was added
                // because the arm was one registration away from causing a bug of its own. The three-part
                // shape `ldelem.r4 · ldc.r4 200 · branch` is NOT unique to a slot gate: NPC.AI_005_EaterOfSouls
                // matches it exactly with `if (ai[0] > 200f) ai[0] = -200f;`, which is the oscillation counter
                // that makes Eaters of Souls wobble — widening it to 750 would have stretched their movement
                // period nearly fourfold, with nothing in any log to connect the two. A real slot gate always
                // turns the value it just validated into an index; the counter never touches Main.npc at all.
                if (SafeLdcR4(cur, 200f)
                    && PrevReal(instrs, j)?.OpCode == OpCodes.Ldelem_R4
                    && NextReal(cur)?.OpCode.FlowControl == FlowControl.Cond_Branch
                    && LoadsNpcArrayWithin(instrs, j, 12))
                {
                    cur.OpCode = OpCodes.Ldsfld;
                    cur.Operand = il.Import(NpcCapField);
                    il.Body.Instructions.Insert(j + 1, Instruction.Create(OpCodes.Conv_R4));
                    j++;   // skip the instruction we just inserted
                    floatGates++;
                }
            }

            // Per-FORM accounting. A method with three int gates that widens one is broken in a way a combined
            // total hides, and "some of the weapon works" is the hardest kind of report to act on.
            if (intGates != expectInt || floatGates != expectFloat)
            {
                throw new Exception(
                    $"stored-slot gate count changed in {il.Method.Name}: expected {expectInt} int / " +
                    $"{expectFloat} float, found {intGates} int / {floatGates} float");
            }

            ModRef?.Logger.Info($"[MMM] target-slot gates widened in {il.Method.Name}: {intGates} int, {floatGates} float");
        }


        /// <summary>
        /// The instruction before <paramref name="index"/>, skipping <c>nop</c> padding. Null if there is none.
        /// </summary>
        /// <remarks>
        /// <b>Why this exists.</b> In this build EVERY long-form <c>ldloc</c> / <c>stloc</c> / <c>ldloca</c> is
        /// followed by exactly two <c>nop</c>s. Not usually — always: measured over the whole
        /// <c>Terraria.Projectile</c> type, 35,841 long-form local accesses and 35,841 of them nop-padded,
        /// accounting for 71,682 of the type's 71,826 nops. <c>Projectile.VanillaAI</c> alone is 18% nop.
        /// <para/>
        /// The long form is only emitted for local index 255 and above (measured, not assumed: the highest
        /// short-form index anywhere in the assembly is `ldloc.s 254`, the lowest long-form is `ldloc 255`, and
        /// `ldloc.s 255` does not occur at all -- so the boundary is 255, not the 256 the operand width would
        /// suggest), so the padding appears ONLY in methods
        /// with 255+ locals — <c>Projectile.VanillaAI</c>, <c>NPC.VanillaAI_Inner</c>, <c>Player.Update</c>.
        /// Those are exactly the methods too large to blanket-patch, so they are exactly the ones that need
        /// anchored patches, so they are exactly the ones whose patches inspect neighbouring instructions. The
        /// blindness concentrates in the code where it is hardest to notice, and it fails SILENTLY: the
        /// manipulator matches nothing, throws nothing, and <c>Apply</c> still logs "IL patched".
        /// <para/>
        /// Rule of thumb for any new anchored patch: <c>.Next</c> after a <c>ldc.i4</c>, <c>ldc.r4</c>,
        /// <c>ldsfld</c> or <c>ldelem.*</c> is safe, because nothing pads those. Any step onto or off a LOCAL
        /// access must go through these helpers.
        /// </remarks>
        private static Instruction PrevReal(IList<Instruction> instrs, int index)
        {
            for (int k = index - 1; k >= 0; k--)
            {
                if (instrs[k].OpCode != OpCodes.Nop)
                    return instrs[k];
            }
            return null;
        }

        /// <summary>
        /// True if <c>Main.npc</c> is loaded within <paramref name="window"/> REAL (non-<c>nop</c>) instructions
        /// after <paramref name="from"/> — i.e. the value the gate just validated is actually used as a slot.
        /// </summary>
        /// <remarks>
        /// The counterpart to the proven-slot-local set for the float form, where there is no local to
        /// track: the gate reads an <c>ai[]</c> element straight off the array, so the only thing that
        /// distinguishes "is this a valid NPC slot?" from "has this counter run past 200?" is whether an NPC
        /// lookup follows. Measured against all three known sites: Betsy's breath loads <c>Main.npc</c> four
        /// real instructions later, the Horseman's Blade re-target seven, and the Eater of Souls wobble
        /// counter never.
        /// </remarks>
        private static bool LoadsNpcArrayWithin(IList<Instruction> instrs, int from, int window)
        {
            for (int k = from + 1, seen = 0; k < instrs.Count && seen < window; k++)
            {
                if (instrs[k].OpCode == OpCodes.Nop)
                    continue;
                seen++;
                if (instrs[k].OpCode == OpCodes.Ldsfld
                    && instrs[k].Operand is FieldReference fr
                    && fr.Name == "npc"
                    && fr.DeclaringType?.Name == "Main")
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>The instruction after <paramref name="instr"/>, skipping <c>nop</c> padding; null if none.
        /// See <see cref="PrevReal"/> for why this is necessary.</summary>
        private static Instruction NextReal(Instruction instr)
        {
            Instruction n = instr?.Next;
            while (n != null && n.OpCode == OpCodes.Nop)
                n = n.Next;
            return n;
        }

        // Discrete projectile AIs (outside the monolithic AI()) that contain their own NPC-target scan.
        private static readonly string[] ChaseLoopAIMethods =
        {
            // AUDIT-SKIP: Projectile.AI_015_Flails_Old — unreachable. The engine kept the old flail AI body
            // but calls only AI_015_Flails; zero call sites in the whole assembly, and it is `private`, so no
            // mod can reach it either.
            "AI_001", "AI_009_MagicMissiles", "AI_015_Flails", "AI_016", "AI_026",
            "AI_047_MagnetSphere_TryAttacking", "AI_062" /*Abigail*/, "AI_067_TigerSpecialAttack",
            "AI_099_1", "AI_099_2", "AI_100_Medusa", "AI_120_StardustGuardian", "AI_121_StardustDragon",
            "AI_130_FlameBurstTower_FindTarget", "AI_134_Ballista_FindTarget", "AI_137_CanHit",
            "AI_137_LightningAura", "AI_138_ExplosiveTrap", "AI_147_Celeb2Rocket", "AI_152_SuperStarSlash",
            "AI_156_Think", "AI_156_TryAttackingNPCs", "AI_158_GetHomeLocation", "AI_169_Smolstars",
            "AI_171_HallowBossRainbowStreak", "AI_172_GetPelletStormInfo", "AI_176_EdgyLightning",
            "AI_177_IceWhipSlicer",
        };


        /// <summary>Resolve a projectile AI method by name and widen its NPC-iterating loops (broad NPC-member body check).</summary>
        private static void PatchNpcLoops(Mod mod, Type type, string name)
        {
            MethodInfo method;
            try
            {
                method = type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
            }
            catch (AmbiguousMatchException)
            {
                mod.Logger.Error($"[MMM] {type.Name}.{name} is ambiguous; npc-loop patch skipped.");
                return;
            }

            Apply(mod, $"{type.Name}.{name} (npc loops)", method, Patch_NpcLoops);
        }

        // Widen a backward-branch `< 200` loop if its body references a member whose DECLARING TYPE is
        // Terraria.NPC — e.g. `chaseable`, `type`, `ai[]`, `CanBeChasedBy` — i.e. it is iterating NPCs.
        // Catches homing/targeting loops that don't use CanBeChasedBy.
        //
        // TWO CORRECTIONS to what this comment used to say, both of which cost real bugs:
        //
        // 1. `active` is NOT such a member. Nor are `whoAmI`, `position`, `width`, `height`, `velocity` or
        //    `Center` — all seven are declared on Terraria.Entity. A loop body containing only those matches
        //    NOTHING here. That is exactly how NPCUtils.SearchForTarget went a whole version unpatched while
        //    reporting success.
        // 2. This is NOT "restricted to discrete per-weapon AI methods". It is applied to Projectile.VanillaAI
        //    (20k lines), Projectile.Kill (13.7k), Player.Update (4k), Player.UpdateBuffs (2.1k),
        //    WorldGen.clearWorld, and two methods on Terraria.NPC itself.
        //
        // The second point carries a live hazard worth stating plainly: inside Terraria.NPC the test barely
        // discriminates, because `Dust.NewDust(position, width, height, ...)` reads members that resolve to
        // Entity — but a dust loop in NPC that touches any NPC-declared field would be widened to 750
        // particles. The two NPC targets here (AI_084_LunaticCultist, MechSpawn) happen to contain no
        // 200-bounded dust loop, so this is currently safe by luck rather than by design. Prefer an anchored
        // patch for anything new inside Terraria.NPC — see Patch_DaybreakSpread for the pattern.
        private static void Patch_NpcLoops(ILContext il)
        {
            var instrs = il.Body.Instructions;
            int count = 0, arrays = 0;

            for (int j = 0; j < instrs.Count; j++)
            {
                if (!SafeLdcI4(instrs[j], 200) || !TryFindLoopBodyStart(instrs, j, out int target))
                    continue;

                bool npcLoop = false;
                for (int b = target; b < j; b++)
                {
                    if (instrs[b].Operand is MemberReference m && m.DeclaringType != null && m.DeclaringType.FullName == "Terraria.NPC")
                    {
                        npcLoop = true;
                        break;
                    }
                }
                if (!npcLoop)
                    continue;

                instrs[j].OpCode = OpCodes.Ldsfld;
                instrs[j].Operand = il.Import(NpcCapField);
                count++;

                // Widening a loop is not enough on its own if the loop FILLS a scratch array that was itself
                // allocated at 200. `Projectile.VanillaAI` aiStyle 59 (Spectre Wrath) does exactly that:
                //
                //     int[] array = new int[200];
                //     int n = 0;
                //     for (int i = 0; i < 200; i++)
                //         if (Main.npc[i].CanBeChasedBy(this) && dist < 800f) { array[n] = i; n++; }
                //
                // with no cap on `n`. Before we widened the loop the array could never overflow; after, 201
                // chaseable NPCs inside the search radius write array[200] and throw INSIDE projectile AI.
                // So this patch was creating a crash that vanilla does not have.
                //
                // An array of exactly 200 allocated immediately before a loop we just decided is NPC-indexed
                // is, by construction, indexed by NPC slot too. Widen it with the loop. Scoped tightly: only
                // `ldc.i4 200; newarr`, and only within a short window before the loop body.
                for (int b = Math.Max(0, target - 12); b < target; b++)
                {
                    if (!SafeLdcI4(instrs[b], 200)) continue;
                    if (instrs[b].Next?.OpCode != OpCodes.Newarr) continue;
                    instrs[b].OpCode = OpCodes.Ldsfld;
                    instrs[b].Operand = il.Import(NpcCapField);
                    arrays++;
                }
            }

            if (count > 0)
            {
                ModRef?.Logger.Info($"[MMM] npc-loop patch widened {count} loop(s)" +
                                    (arrays > 0 ? $" and {arrays} companion array(s)" : "") +
                                    $" in {il.Method.Name}");
            }
            else
            {
                // Deliberately NOT a throw. Eleven of the registrations routed here legitimately widen
                // nothing — several AI methods carry no `ldc.i4 200` at all, or only float ones — so throwing
                // would turn a normal outcome into a failure. But silence is worse: NPCUtils.SearchForTarget
                // matched nothing for an entire version because every member in its loop body is declared on
                // Terraria.Entity rather than Terraria.NPC, and nothing anywhere said so.
                //
                // So it says so. If a method you expected to be widened appears here, the likely cause is
                // that its loop touches only Entity-declared members (`active`, `whoAmI`, `position`,
                // `width`, `height`, `velocity`, `Center`) and it needs a different manipulator.
                ModRef?.Logger.Info($"[MMM] npc-loop patch matched NO loops in {il.Method.Name} " +
                                    "(expected for AI methods with no 200-bounded NPC scan; " +
                                    "otherwise its loop body may only touch Entity-declared members).");
            }
        }

        // Patch_NpcChaseLoops and its PatchChaseLoops wrapper lived here and were DEAD CODE: nothing ever
        // called them. The ChaseLoopAIMethods array above is fed to PatchNpcLoops, which uses the broader
        // "any Terraria.NPC-declared member in the loop body" test -- NOT the narrower "body calls
        // CanBeChasedBy" test the removed manipulator implemented. Keeping them meant two comments in
        // this file described a heuristic that never ran, which is how the array's name still misleads.
        // Removed rather than marked, because a dead manipulator that documents the wrong behaviour is
        // worse than no manipulator at all.

        // Find the body start of the loop whose bound is the `ldc.i4 200` at index j. Handles BOTH the direct
        // form (`ldc.i4 200; blt.s BODY`) and this build's compare-then-branch form (`ldc.i4 200; clt;
        // brtrue.s BODY`) by scanning a few instructions ahead for the loop's conditional branch. Returns true
        // only for a BACKWARD branch (a real loop); the body is then [target, j). This is the fix for the
        // detection silently matching zero loops (clt-based comparisons were being ignored).
        private static bool TryFindLoopBodyStart(IList<Instruction> instrs, int j, out int target)
        {
            target = -1;
            int limit = Math.Min(instrs.Count - 1, j + 5);
            for (int b = j + 1; b <= limit; b++)
            {
                if (!IsConditionalBranch(instrs[b].OpCode))
                    continue;
                Instruction ti = BranchTarget(instrs[b]);
                if (ti == null)
                    return false;
                int t = instrs.IndexOf(ti);
                if (t >= 0 && t < j)
                {
                    target = t;
                    return true; // backward branch => loop
                }
                return false; // forward branch => not a loop bound
            }
            return false;
        }

        // A branch's target operand is a Cecil Instruction in raw bodies, but a MonoMod ILLabel inside an
        // ILContext — handle both, else every branch looks targetless and no loop is ever found.
        private static Instruction BranchTarget(Instruction ins)
        {
            if (ins.Operand is Instruction i) return i;
            if (ins.Operand is ILLabel lbl) return lbl.Target;
            return null;
        }

        /// <summary>
        /// A <c>MatchLdcI4</c> that cannot throw, and that still matches when the operand's type is odd.
        /// <para/>
        /// MonoMod's matcher casts an <c>ldc.i4.s</c> operand straight to <c>sbyte</c>. When another mod has
        /// already rewritten the same method it can leave that instruction holding a boxed <c>int</c> instead,
        /// and the cast then throws <c>InvalidCastException</c> out of the whole manipulator — killing every
        /// remaining patch in that method, not just the one site. That is exactly how a Calamity-loaded session
        /// silently lost <c>Player.DashMovement</c> (so dash damage stopped reaching enemies above slot 199).
        /// Reading the operand ourselves recovers the match instead of dropping it.
        /// <para/>
        /// The two operand-carrying opcodes are handled here FIRST rather than in a catch block. Both orders
        /// give the same answer, but the throw is not free: it is a first-chance exception on a path that runs
        /// for every instruction of every patched method, and tModLoader logs each one with a full stack trace.
        /// </summary>
        internal static bool SafeLdcI4(Instruction instr, int value)
        {
            if (instr.OpCode == OpCodes.Ldc_I4 || instr.OpCode == OpCodes.Ldc_I4_S)
            {
                return instr.Operand switch
                {
                    int i32 => i32 == value,
                    sbyte sb => sb == value,
                    byte b => b == value,
                    short s16 => s16 == value,
                    _ => false,
                };
            }

            // Everything else (ldc.i4.0 … ldc.i4.8, ldc.i4.m1) encodes its value in the opcode and cannot throw.
            try { return instr.MatchLdcI4(value); }
            catch { return false; }
        }

        /// <summary>
        /// The float counterpart of <see cref="SafeLdcI4"/>, and the reason it had to exist.
        /// <para/>
        /// A slot bound written as <c>200f</c> compiles to <c>ldc.r4 200</c>, which <see cref="SafeLdcI4"/>
        /// structurally cannot match — it inspects only the <c>ldc.i4</c> family. Every blanket patch in this
        /// file is therefore blind to float-typed slot guards, and that blindness is total rather than
        /// partial: not "we forgot to patch it" but "no patch we can write with SafeLdcI4 could ever reach
        /// it". Two shipped bugs hid in that gap — Betsy's flame breath killing itself, and aiStyle 55 homing
        /// silently refusing to home — and both were found by reading the method, because no tool could see
        /// them. <c>tools/audit-npc-loops.sh</c> now reports the shape as FLOATGUARD; this is the fix it
        /// points at.
        /// <para/>
        /// Unlike the int case there is no throwing helper to route around. <c>ldc.r4</c> always carries a
        /// float operand, so the type test IS the whole check.
        /// <para/>
        /// Callers must remember the type: replacing the literal with <c>ldsfld EngineState.NpcCap</c> pushes
        /// an <c>int</c> where the following comparison expects a float, so a <c>conv.r4</c> has to be
        /// inserted after it. Forgetting that produces IL that verifies inconsistently and fails at JIT time
        /// rather than at patch time, which is much harder to trace back here.
        /// </summary>
        internal static bool SafeLdcR4(Instruction instr, float value)
            => instr.OpCode == OpCodes.Ldc_R4 && instr.Operand is float f && f == value;

        private static bool IsConditionalBranch(OpCode op)
            => op == OpCodes.Brtrue || op == OpCodes.Brtrue_S || op == OpCodes.Brfalse || op == OpCodes.Brfalse_S
            || op == OpCodes.Blt || op == OpCodes.Blt_S || op == OpCodes.Blt_Un || op == OpCodes.Blt_Un_S
            || op == OpCodes.Bge || op == OpCodes.Bge_S || op == OpCodes.Bge_Un || op == OpCodes.Bge_Un_S
            || op == OpCodes.Bgt || op == OpCodes.Bgt_S || op == OpCodes.Bgt_Un || op == OpCodes.Bgt_Un_S
            || op == OpCodes.Ble || op == OpCodes.Ble_S || op == OpCodes.Ble_Un || op == OpCodes.Ble_Un_S
            || op == OpCodes.Bne_Un || op == OpCodes.Bne_Un_S || op == OpCodes.Beq || op == OpCodes.Beq_S;

        /// <summary>Resolve a method by name (any visibility, instance or static) and blanket-patch its NPC 200-bounds.</summary>
        private static void PatchMethod(Mod mod, Type type, string name)
        {
            MethodInfo method;
            try
            {
                method = type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
            }
            catch (AmbiguousMatchException)
            {
                mod.Logger.Error($"[MMM] {type.Name}.{name} is ambiguous; targeting patch skipped.");
                return;
            }

            Apply(mod, $"{type.Name}.{name}", method, Patch_AllNpcLoopBounds200);
        }

        /// <summary>
        /// The boss health bar (<c>Terraria.GameContent.UI.BigProgressBar</c>) is blind to slots ≥ 200.
        /// <para/>
        /// Two independent 0-199 assumptions, both fatal to the bar rather than cosmetic-within-it:
        /// <list type="bullet">
        /// <item><c>BigProgressBarSystem.TryFindingNPCToTrack</c> scans <c>for (i &lt; 200)</c> to pick which NPC
        /// the bar follows, and <c>TryTracking</c> then rejects any index above 200 outright. A boss placed in
        /// the bonus zone therefore gets <b>no bar at all</b>.</item>
        /// <item>The individual bars re-scan the array to total up their multi-part bodies — the Eater of
        /// Worlds sums every active segment, the Twins/Golem/Moon Lord/Saucer/Pirate Ship/Brain look up their
        /// other halves — and every one of those scans stops at 200 too, so a bar that does appear reads a
        /// fraction of the real health.</item>
        /// </list>
        /// Bosses are zoned into 0-199 precisely so this sort of thing doesn't bite, which is why it went
        /// unnoticed; it surfaces as soon as the low zone is full (a Destroyer is 82 slots on its own, and a
        /// boss-multiplier mod trivially overflows it) and the overflow lands high.
        /// <para/>
        /// Blanket-widening every <c>200</c> in these types is safe in a way it would not be elsewhere: they
        /// are small display-only classes whose <i>only</i> 200s are NPC-index bounds — verified by
        /// decompiling all ten. Nothing here can affect gameplay state; the worst a mistake could do is draw a
        /// wrong bar.
        /// </summary>
        private static void ApplyBossHealthBarPatches(Mod mod)
        {
            string[] typeNames =
            {
                "BigProgressBarSystem",          // picks the tracked NPC + the >200 rejection
                "CommonBossBigProgressBar",      // the default single-NPC bar
                "EaterOfWorldsProgressBar",      // sums every active segment of type 13-15
                "TwinsBigProgressBar",
                "MoonLordProgressBar",
                "GolemHeadProgressBar",
                "MartianSaucerBigProgressBar",
                "PirateShipBigProgressBar",
                "BrainOfCthuluBigProgressBar",
                "DeerclopsBigProgressBar",
                // The eleventh. Missed for three versions because the VANILLA class name is misspelled
                // ("Progess"), so it did not match a search for the correctly-spelled word. Its own
                // ValidateAndCollectNecessaryInfo rejects `npcIndexToAimAt > 200`, so BigProgressBarSystem
                // (patched) would hand it a high-slot pillar and the bar would then refuse to draw it.
                "LunarPillarBigProgessBar",
            };

            BossBarNoMatch.Clear();
            int patched = 0, missing = 0;
            foreach (string name in typeNames)
            {
                Type t = typeof(Main).Assembly.GetType("Terraria.GameContent.UI.BigProgressBar." + name);
                if (t == null)
                {
                    mod.Logger.Error($"[MMM] boss-bar type not found: {name} (skipped)");
                    missing++;
                    continue;
                }

                foreach (MethodInfo m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                                                    | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    if (!ContainsLiteral200(m))
                        continue;

                    try
                    {
                        MonoModHooks.Modify(m, Patch_BossBarBounds);
                        patched++;
                    }
                    catch (Exception e)
                    {
                        mod.Logger.Error($"[MMM] boss-bar patch FAILED for {name}.{m.Name} (skipped): {e.Message}");
                    }
                }
            }

            mod.Logger.Info($"[MMM] IL patched: boss health bars — {patched} method(s) across {typeNames.Length - missing} type(s).");

            // Not an error on its own — the pre-filter is allowed to false-positive — but it is the only place
            // a boss bar that quietly stopped being widened can surface, so name the methods rather than
            // letting them vanish into the aggregate count above.
            if (BossBarNoMatch.Count > 0)
                mod.Logger.Warn(
                    $"[MMM] boss-bar patch widened NOTHING in {BossBarNoMatch.Count} hooked method(s): " +
                    string.Join(", ", BossBarNoMatch) +
                    ". Expected when the byte pre-filter false-positives; if a boss health bar misbehaves at " +
                    "high slots, start here.");
        }

        /// <summary>
        /// Cheap pre-filter so we only hook methods that actually contain a literal 200.
        /// <para/>
        /// 200 exceeds <c>ldc.i4.s</c>'s signed-byte range, so it is always the five-byte <c>ldc.i4</c> form
        /// (<c>0x20 C8 00 00 00</c>) — a raw byte scan can therefore never MISS one. It can produce a false
        /// positive by matching the operand bytes of some other instruction, which costs nothing: the
        /// manipulator finds no real match and leaves the method alone.
        /// </summary>
        private static bool ContainsLiteral200(MethodInfo m)
        {
            try
            {
                byte[] il = m.GetMethodBody()?.GetILAsByteArray();
                if (il == null)
                    return false;

                for (int i = 0; i + 4 < il.Length; i++)
                    if (il[i] == 0x20 && il[i + 1] == 0xC8 && il[i + 2] == 0 && il[i + 3] == 0 && il[i + 4] == 0)
                        return true;
            }
            catch { /* dynamic / no body — nothing to patch */ }

            return false;
        }

        /// <summary>
        /// Boss-bar methods that were hooked but widened nothing, reported together at the end of
        /// <see cref="ApplyBossHealthBarPatches"/>.
        /// <para/>
        /// This cannot throw the way the other manipulators do — methods are chosen by a byte-level
        /// pre-filter that is allowed to false-positive, so "widened nothing" is sometimes correct. But it
        /// must not be MUTE either: the only thing logged was an aggregate "patched N method(s)" count, and
        /// `patched` counts methods HOOKED, not methods changed. A bar whose real slot bound stopped matching
        /// would just make that number one smaller, with nothing naming which one.
        /// </summary>
        private static readonly List<string> BossBarNoMatch = new List<string>();

        // Widen every NPC-index 200 in a boss-bar method. Unlike Patch_AllNpcLoopBounds200 this must not throw
        // on zero matches: the byte-level pre-filter is allowed to produce false positives. It records them
        // instead, so a false positive and a broken anchor are at least distinguishable after the fact.
        private static void Patch_BossBarBounds(ILContext il)
        {
            var c = new ILCursor(il);
            int count = 0;
            while (c.TryGotoNext(MoveType.Before, i => SafeLdcI4(i, 200)))
            {
                ReplaceWithNpcCap(c, il);
                c.Index++;
                count++;
            }

            if (count > 0)
                ModRef?.Logger.Info($"[MMM] boss-bar patch widened {count} NPC index bound(s) in {il.Method.DeclaringType?.Name}.{il.Method.Name}");
            else
                BossBarNoMatch.Add($"{il.Method.DeclaringType?.Name}.{il.Method.Name}");
        }

        // Old One's Army (DD2) relies on 0-199 NPC scans to find the Eternia Crystal (548) / portals (549),
        // to let enemies target the crystal, and to run victory/loot/cleanup — all of which miss those NPCs in
        // the bonus zone. (The crystal stop-check, AnyNPCs(548), is already covered by the v0.4 AnyNPCs patch.)
        private static void ApplyOldOnesArmyPatches(Mod mod)
        {
            // Shared crystal/portal finder (DD2Event victory + Betsy summon).
            PatchMethod(mod, typeof(NPC), "FindFirstNPC");

            // How DD2 enemies find the crystal: NPCUtils.SearchForTarget's NPC scan is 0-199. Patch the 5-arg
            // overload (the one that actually contains the loops; the others delegate to it).
            Type npcUtils = typeof(Main).Assembly.GetType("Terraria.Utilities.NPCUtils");
            MethodInfo search = npcUtils?.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "SearchForTarget" && m.GetParameters().Length == 5);
            // Patch_AllNpcLoopBounds200, NOT Patch_NpcLoops — and the reason is the sharpest trap in this file.
            //
            // Patch_NpcLoops only widens a loop whose body references a member whose DECLARING TYPE is
            // Terraria.NPC. This loop's body is:
            //
            //     NPC nPC = Main.npc[i];
            //     if (nPC.active && nPC.whoAmI != searcher.whoAmI && (npcFilter == null || npcFilter(nPC)))
            //         ... Vector2.DistanceSquared(position, nPC.Center) ...
            //
            // and every one of those members is declared on **Terraria.Entity**, not Terraria.NPC — `active`
            // (Entity.cs:22), `whoAmI` (:17), `Center` (:85). `Main.npc` is a Terraria.Main field. So the test
            // found nothing, the heuristic widened nothing, and because it neither throws nor logs on a zero
            // match, Apply still reported "IL patched" and counted it as applied. A patch that did nothing at
            // all looked identical to one that worked, in the log AND in /mmmdebug.
            //
            // The method has exactly one 200 (the loop bound), so the blanket manipulator is exact here, and
            // it throws when it matches nothing — which is the property that actually matters.
            Apply(mod, "NPCUtils.SearchForTarget (crystal/NPC targeting)", search, Patch_AllNpcLoopBounds200);

            // DD2Event crystal/portal/enemy scans: victory scene, arena hitbox, medal/crystal loot, cleanup.
            Type dd2 = typeof(Main).Assembly.GetType("Terraria.GameContent.Events.DD2Event");
            if (dd2 == null)
            {
                mod.Logger.Error("[MMM] DD2Event type not found; Old One's Army patches skipped.");
                return;
            }
            foreach (string name in new[] { "StartVictoryScene", "ClearAllDD2HostilesInGame", "FindArenaHitbox", "DropMedals", "DropStarterCrystals" })
                Apply(mod, $"DD2Event.{name}",
                    dd2.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static),
                    Patch_NpcLoops);

            // AUDIT-SKIP: DD2Event.IsStandActive — its 0-199 scan looks only for the Eternia Crystal (type 548),
            // which IsStructuralLowZone pins into the low zone at spawn, so the loop always finds it. Left alone
            // deliberately: it also drives the "right-click to skip the wait" interaction, and a needless widen
            // there would scan the whole expanded array on every hover.

            // OOA wave length: scale required kills per wave (EventsConfig).
            MethodInfo getStatus = dd2.GetMethod("GetInvasionStatus", BindingFlags.NonPublic | BindingFlags.Static);
            AddHook(mod, getStatus, new HookGetInvasionStatus(GetInvasionStatus_Hook), "DD2Event.GetInvasionStatus (wave length)");

            // OOA spawn ramp: each difficulty's GetEnemiesForWave sets DD2Event.LaneSpawnRate per wave; lower it
            // afterwards so the portals spawn faster (EventsConfig).
            foreach (string name in new[] { "Difficulty_1_GetEnemiesForWave", "Difficulty_2_GetEnemiesForWave", "Difficulty_3_GetEnemiesForWave" })
                AddHook(mod, dd2.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static),
                    new HookGetEnemiesForWave(GetEnemiesForWave_Hook), $"DD2Event.{name} (spawn ramp)");
        }

        // Small helper: register a MonoMod detour with the same not-found / failure logging as Apply.
        private static void AddHook(Mod mod, MethodInfo method, Delegate hook, string name)
        {
            if (method == null)
            {
                mod.Logger.Error($"[MMM] hook target not found: {name}.");
                return;
            }
            try
            {
                MonoModHooks.Add(method, hook);
                mod.Logger.Info($"[MMM] Hooked {name}.");
            }
            catch (Exception e)
            {
                mod.Logger.Error($"[MMM] hook FAILED for {name} (skipped): {e.Message}");
            }
        }

        private delegate void OrigGetInvasionStatus(out int wave, out int req, out int cur, bool inCheck);
        private delegate void HookGetInvasionStatus(OrigGetInvasionStatus orig, out int wave, out int req, out int cur, bool inCheck);
        private static void GetInvasionStatus_Hook(OrigGetInvasionStatus orig, out int wave, out int req, out int cur, bool inCheck)
        {
            orig(out wave, out req, out cur, inCheck);
            req = EventScaling.ScaleOldOnesArmyWaveKills(req);
        }

        private delegate short[] OrigGetEnemiesForWave(int wave);
        private delegate short[] HookGetEnemiesForWave(OrigGetEnemiesForWave orig, int wave);
        private static short[] GetEnemiesForWave_Hook(OrigGetEnemiesForWave orig, int wave)
        {
            short[] result = orig(wave);
            EventScaling.RampLaneSpawnRate();
            return result;
        }

        private delegate void OrigStartInvasion(int type);
        private delegate void HookStartInvasion(OrigStartInvasion orig, int type);

        // Detour Main.StartInvasion: after vanilla sets the invasion size, scale it up (EventsConfig). Only
        // when a NEW invasion actually started (invasionType went 0 -> non-0), so we don't rescale a no-op call.
        private static void ApplyInvasionScaling(Mod mod)
        {
            MethodInfo m = typeof(Main).GetMethod(nameof(Main.StartInvasion), BindingFlags.Public | BindingFlags.Static);
            if (m == null)
            {
                mod.Logger.Error("[MMM] Main.StartInvasion not found; invasion length scaling skipped.");
                return;
            }

            try
            {
                MonoModHooks.Add(m, new HookStartInvasion(StartInvasion_Hook));
                mod.Logger.Info("[MMM] Hooked Main.StartInvasion (event length scaling).");
            }
            catch (Exception e)
            {
                mod.Logger.Error($"[MMM] StartInvasion hook failed (skipped): {e.Message}");
            }
        }

        private static void StartInvasion_Hook(OrigStartInvasion orig, int type)
        {
            int before = Main.invasionType;
            orig(type);
            if (before == 0 && Main.invasionType != 0)
                EventScaling.ScaleInvasion();
        }

        // Scale the Pumpkin/Frost Moon per-wave point thresholds: after each `MoonEventRequiredPointsPerWaveLookup[..]`
        // read, route the value through EventScaling.ScaleMoonWavePoints so the advance threshold AND the
        // progress-bar max grow together.
        private static void Patch_ScaleMoonWave(ILContext il)
        {
            var c = new ILCursor(il);
            FieldInfo lookup = typeof(NPC).GetField(nameof(NPC.MoonEventRequiredPointsPerWaveLookup));
            MethodInfo scale = typeof(EventScaling).GetMethod(nameof(EventScaling.ScaleMoonWavePoints));
            int count = 0;

            while (c.TryGotoNext(i => i.MatchLdsfld(lookup)))
            {
                if (!c.TryGotoNext(i => i.OpCode == OpCodes.Ldelem_I4))
                    break;
                c.Index++; // position after the ldelem.i4 (the threshold int is on the stack)
                c.Emit(OpCodes.Call, scale);
                count++;
            }

            if (count == 0)
                throw new Exception("MoonEventRequiredPointsPerWaveLookup read not found");
        }

        private delegate void OrigApplyAttackCooldown(Player self);
        private delegate void HookApplyAttackCooldown(OrigApplyAttackCooldown orig, Player self);

        // Detour Player.ApplyAttackCooldown() (the parameterless, post-melee-hit one) and skip it while the
        // "melee hits all in swing" option is on, so attackCD never rises and the per-frame one-hit gate stays
        // open. Leaves the (int) overload and all other attackCD uses alone; auto-undone on unload.
        private static void ApplyAttackCooldownSuppressor(Mod mod)
        {
            MethodInfo m = typeof(Player).GetMethod(nameof(Player.ApplyAttackCooldown),
                BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (m == null)
            {
                mod.Logger.Error("[MMM] Player.ApplyAttackCooldown() not found; melee-hits-all may not work.");
                return;
            }

            try
            {
                MonoModHooks.Add(m, new HookApplyAttackCooldown(ApplyAttackCooldown_Hook));
                mod.Logger.Info("[MMM] Hooked Player.ApplyAttackCooldown (melee-hits-all).");
            }
            catch (Exception e)
            {
                mod.Logger.Error($"[MMM] ApplyAttackCooldown hook failed (skipped): {e.Message}");
            }
        }

        private static void ApplyAttackCooldown_Hook(OrigApplyAttackCooldown orig, Player self)
        {
            var config = ModContent.GetInstance<HordeCombatConfig>();
            if (config != null && config.MeleeHitsAllInSwing)
                return; // keep attackCD at 0 so the swing hits every overlapping enemy

            orig(self);
        }

        private delegate bool OrigSavesAndLoads(NPC npc);
        private delegate bool HookSavesAndLoads(OrigSavesAndLoads orig, NPC npc);

        private static void ApplySavesAndLoadsSafeguard(Mod mod)
        {
            MethodInfo method = typeof(NPCLoader).GetMethod("SavesAndLoads", BindingFlags.Public | BindingFlags.Static);
            if (method == null)
            {
                mod.Logger.Error("[MMM] NPCLoader.SavesAndLoads not found; save safeguard not applied.");
                return;
            }

            try
            {
                MonoModHooks.Add(method, new HookSavesAndLoads(SavesAndLoads_Safeguard));
                mod.Logger.Info("[MMM] Safeguarded NPCLoader.SavesAndLoads");
            }
            catch (Exception e)
            {
                mod.Logger.Error($"[MMM] SavesAndLoads safeguard failed (skipped): {e.Message}");
            }
        }

        private static bool SavesAndLoads_Safeguard(OrigSavesAndLoads orig, NPC npc)
        {
            try { return orig(npc); }
            catch { return false; }
        }

        // ── Patch tally, for /mmm info ──
        // A failed patch is the single most diagnostic fact about a content-mod conflict: another mod rewrote
        // the same method first and our anchor no longer matches. It was only ever an Error line in client.log,
        // which a player reporting a bug has no reason to open. Counted here so the triage command can say
        // "3 failed" and name them.
        internal static int PatchesApplied, PatchesFailed, PatchesMissing;

        /// <summary>Names of patches that failed or whose target was not found, for the triage readout.</summary>
        internal static readonly List<string> PatchProblems = new();

        private static void Apply(Mod mod, string name, MethodBase method, ILContext.Manipulator manip)
        {
            if (method == null)
            {
                mod.Logger.Error($"[MMM] IL target not found: {name} — cap raise incomplete.");
                PatchesMissing++;
                Record(name + " (target not found)");
                return;
            }

            try
            {
                MonoModHooks.Modify(method, manip);
                mod.Logger.Info($"[MMM] IL patched: {name}");
                PatchesApplied++;
            }
            catch (Exception e)
            {
                mod.Logger.Error($"[MMM] IL patch FAILED for {name} (skipped; game stays stable): {e.Message}");
                PatchesFailed++;
                Record(name);
            }
        }

        /// <summary>
        /// <c>GetMethod</c> that returns null instead of throwing when the name is overloaded.
        /// <para/>
        /// Use this for any name whose overload set cannot be checked against a decompile. A null degrades to
        /// <c>Apply</c>'s "IL target not found" path, which is counted and reported; an exception escapes to
        /// <see cref="ApplyAll"/>'s guard and costs every registration after it.
        /// </summary>
        private static MethodInfo SafeGetMethod(Type type, string name, BindingFlags flags)
        {
            if (type == null)
                return null;
            try
            {
                return type.GetMethod(name, flags);
            }
            catch (AmbiguousMatchException)
            {
                ModRef?.Logger.Error($"[MMM] {type.Name}.{name} is ambiguous; patch skipped.");
                return null;
            }
        }

        /// <summary>Remember a problem patch by name, bounded so a pathological load can't grow this forever.</summary>
        private static void Record(string name)
        {
            if (PatchProblems.Count < 40)
                PatchProblems.Add(name);
        }

        /// <summary>Turn the <c>ldc.i4</c> the cursor is positioned before into <c>ldsfld EngineState.NpcCap</c>.</summary>
        private static void ReplaceWithNpcCap(ILCursor c, ILContext il)
        {
            Instruction instr = c.Next!;
            instr.OpCode = OpCodes.Ldsfld;
            instr.Operand = il.Import(NpcCapField);
        }

        /// <summary>
        /// Widen the <c>NPC.brainOfGravity &gt;= 0 &amp;&amp; &lt; 200</c> slot-validity gates.
        /// <para/>
        /// Anchored on the FIELD rather than on the literal, because both of its consumers are methods where a
        /// blanket pass would be catastrophic: <c>Main.DoUpdateInWorld</c> and <c>Player.Update</c> between
        /// them hold buff ids, dust alphas, a remix-world depth constant and a chase distance, none of which
        /// is a slot. Matching <c>ldsfld NPC::brainOfGravity</c> and then taking the <c>ldc.i4 200</c> that
        /// follows it within a few instructions cannot pick up any of those.
        /// <para/>
        /// Both call sites are registered, and this throws when it matches nothing, so if the engine ever
        /// renames or inlines the field the patch fails loudly instead of leaving one site widened and the
        /// other narrow -- which would be worse than not patching at all, since the gravity effect would then
        /// read an index that nothing clears.
        /// </summary>
        private static void Patch_BrainOfGravityGate(ILContext il)
        {
            var instrs = il.Body.Instructions;
            int count = 0;

            for (int j = 0; j < instrs.Count; j++)
            {
                if (instrs[j].OpCode != OpCodes.Ldsfld) continue;
                if (instrs[j].Operand is not FieldReference fr || fr.Name != "brainOfGravity") continue;

                // The comparison follows within a couple of instructions: ldsfld; ldc.i4 200; <cond branch>.
                int limit = Math.Min(instrs.Count - 1, j + 4);
                for (int k = j + 1; k <= limit; k++)
                {
                    if (!SafeLdcI4(instrs[k], 200)) continue;
                    if (instrs[k].Next == null || instrs[k].Next.OpCode.FlowControl != FlowControl.Cond_Branch)
                        continue;

                    instrs[k].OpCode = OpCodes.Ldsfld;
                    instrs[k].Operand = il.Import(NpcCapField);
                    count++;
                    break;
                }
            }

            if (count == 0)
                throw new Exception("no brainOfGravity slot gate found");

            ModRef?.Logger.Info($"[MMM] brainOfGravity gate widened in {il.Method.Name}: {count}");
        }

        /// <summary>
        /// <c>NPC.SpawnBoss</c>'s NewNPC-success sentinels, and ONLY the ones that are not a multiplayer
        /// broadcast guard.
        /// <para/>
        /// The method holds three 200s and they belong to two different owners. Two are the single-player
        /// sentinel — <c>int num = 200;</c> and <c>if (num == 200) return;</c> — which this file fixes.
        /// The third, <c>if (Main.netMode == 2 &amp;&amp; num &lt; 200)</c>, is a server broadcast guard owned by
        /// <c>MMMultiplayer.MultiplayerNetPatcher.Patch_ServerBroadcastGuards</c>, which routes it through
        /// <c>MpNetGuards.IndexLimit()</c> so the multiplayer config toggle can still switch it off.
        /// <para/>
        /// A blanket pass here was correct in its result but wrong in its bookkeeping: it consumed all three
        /// literals, so the multiplayer patcher then matched nothing, threw, and logged
        /// "broadcast guards in SpawnBoss not patched (skipped)". That log line is read during bug triage,
        /// and a false failure in it is worse than no line at all. It also quietly took one site out of the
        /// multiplayer toggle's control.
        /// <para/>
        /// So the discriminator is deliberately the same one the multiplayer patcher uses, inverted: skip any
        /// literal preceded within six real instructions by a read of <c>Main.netMode</c>. Whichever of the
        /// two patchers runs first, each takes only its own sites and both report honestly.
        /// </summary>
        private static void Patch_SpawnBossSentinel(ILContext il)
        {
            var instrs = il.Body.Instructions;
            int count = 0;

            for (int j = 0; j < instrs.Count; j++)
            {
                if (!SafeLdcI4(instrs[j], 200))
                    continue;

                bool afterNetModeRead = false;
                for (int b = j - 1, seen = 0; b >= 0 && seen < 6; b--)
                {
                    if (instrs[b].OpCode == OpCodes.Nop)
                        continue;
                    seen++;
                    if (instrs[b].OpCode == OpCodes.Ldsfld
                        && instrs[b].Operand is FieldReference fr
                        && fr.Name == "netMode")
                    {
                        afterNetModeRead = true;
                        break;
                    }
                }
                if (afterNetModeRead)
                    continue;

                instrs[j].OpCode = OpCodes.Ldsfld;
                instrs[j].Operand = il.Import(NpcCapField);
                count++;
            }

            if (count == 0)
                throw new Exception("no single-player NewNPC sentinel found in SpawnBoss");

            ModRef?.Logger.Info($"[MMM] SpawnBoss spawn-failure sentinels widened: {count}");
        }

        /// <summary>
        /// <c>NPC.VanillaHitEffect</c> - Daybreak's on-death debuff spread, and ONLY that loop.
        /// <para/>
        /// This is the method that proves the npc-loop heuristic is not a general answer. That heuristic
        /// widens a backward-branch <c>&lt; 200</c> loop when its body references any member declared on
        /// <c>Terraria.NPC</c> - a test that discriminates beautifully inside <c>Projectile</c>, where a dust
        /// loop touches <c>Projectile</c> members instead. Inside <c>NPC</c> it discriminates nothing:
        /// <c>Dust.NewDust(position, width, height, ...)</c> reads <c>position</c>, <c>width</c> and
        /// <c>height</c>, all of which ARE members declared on NPC. So the heuristic would happily widen the
        /// two 200-particle dust loops at the tail of this method and spray 750 particles per hit.
        /// <para/>
        /// Hence the anchor: take the loop bound whose BODY contains <c>ldc.i4 189</c> - the Daybreak buff id,
        /// read once for <c>buffImmune[189]</c> and again for <c>AddBuff(189, 300)</c>. Neither dust loop
        /// mentions it. If the engine ever renumbers that buff this throws rather than silently widening the
        /// wrong loop, which is the correct direction to fail in.
        /// </summary>
        private static void Patch_DaybreakSpread(ILContext il)
        {
            var instrs = il.Body.Instructions;
            int count = 0;

            for (int j = 0; j < instrs.Count; j++)
            {
                if (!SafeLdcI4(instrs[j], 200) || !TryFindLoopBodyStart(instrs, j, out int target))
                    continue;

                bool daybreak = false;
                for (int b = target; b < j; b++)
                {
                    if (SafeLdcI4(instrs[b], 189)) { daybreak = true; break; }
                }
                if (!daybreak)
                    continue;

                instrs[j].OpCode = OpCodes.Ldsfld;
                instrs[j].Operand = il.Import(NpcCapField);
                count++;
            }

            if (count != 1)
                throw new Exception($"expected exactly one Daybreak-buff loop, found {count}");
        }

        /// <summary>
        /// <c>NPC.CanReleaseNPCs</c> — the gate on releasing a caught critter (bug net, bucket, anything
        /// catchable). The first find made by the audit's FLOATGUARD detector rather than by a bug report.
        /// <para/>
        /// Three separate 200s decide one answer, and they are not the same type:
        /// <code>
        ///   for (int i = 0; i &lt; 200; i++)      // count active NPCs        -> ldc.i4 200
        ///   int quota = (int)(200f * f / players);                            -> ldc.r4 200
        ///   if ((float)count &lt; 200f * f &amp;&amp; mine &lt; quota) return true;      -> ldc.r4 200
        /// </code>
        /// <c>f</c> is 0.75 single-player, 0.7 in multiplayer, so vanilla's rule is "no releasing once the
        /// array is three-quarters full, and no one player may own more than their share".
        /// <para/>
        /// This one is a TRAP, and worth understanding before touching anything shaped like it. Widening only
        /// the loop — which is all any blanket patch in this file is capable of doing, since the other two are
        /// <c>ldc.r4</c> and <see cref="SafeLdcI4"/> cannot see them — leaves the count ranging over 750 slots
        /// while the ceiling stays at 150. Critter release would then stop working for the rest of the world's
        /// life the moment 150 NPCs are active, which under a raised cap is essentially always. The half-fix
        /// is strictly worse than the no-fix, and nothing in the report would have flagged it: the loop would
        /// simply have flipped from GAP to PATCHED.
        /// <para/>
        /// Untouched, the bug is mild and in the permissive direction: the count only sees slots 0-199, which
        /// under our top-down allocation hold Town and Boss NPCs almost exclusively, so it reads near zero and
        /// the gate never refuses. Vanilla's anti-spam limit is simply absent. Scaling all three together
        /// restores the intent at whatever cap is configured.
        /// <para/>
        /// The <c>255</c> player-count loop in the same method is a player bound, not an NPC bound, and must
        /// survive — which is why this is anchored on the literal TYPE rather than blanket-replacing.
        /// </summary>
        private static void Patch_CanReleaseNPCs(ILContext il)
        {
            var c = new ILCursor(il);
            int bounds = 0;
            while (c.TryGotoNext(MoveType.Before, i => SafeLdcI4(i, 200)))
            {
                ReplaceWithNpcCap(c, il);
                c.Index++;
                bounds++;
            }

            var cf = new ILCursor(il);
            int ceilings = 0;
            while (cf.TryGotoNext(MoveType.Before, i => SafeLdcR4(i, 200f)))
            {
                ReplaceWithNpcCap(cf, il);   // pushes an int...
                cf.Index++;
                cf.Emit(OpCodes.Conv_R4);    // ...which the float multiply below needs as a float
                ceilings++;
            }

            // Both halves or neither. A partial match here is the failure mode described above, so it must
            // throw rather than half-apply — an exception loses this one patch, a half-apply loses bug nets.
            if (bounds != 1 || ceilings != 2)
                throw new Exception($"unexpected shape: {bounds} loop bound(s), {ceilings} float ceiling(s); expected 1 and 2");
        }

        // Methods that contain exactly one NPC-bound `ldc.i4 200` (GetAvailableNPCSlot, NewNPC return).
        private static void Patch_SingleLiteral200(ILContext il)
        {
            var c = new ILCursor(il);
            int count = 0;
            while (c.TryGotoNext(MoveType.Before, i => SafeLdcI4(i, 200)))
            {
                ReplaceWithNpcCap(c, il);
                c.Index++;
                count++;
            }

            if (count != 1)
                throw new Exception($"expected exactly one `ldc.i4 200`, found {count}");
        }

        // Methods whose every `ldc.i4 200` is an NPC-loop bound (combat methods, verified by decompilation).
        // Replaces all of them; throws only if none are found (so engine changes fail loudly, not silently).
        // Projectile.Damage, same idea as Patch_AllNpcLoopBounds200 but routed through a per-projectile
        // decision instead of straight to the cap: `HostileHitScan.ScanBound(this)`.
        //
        // Damage() holds four `< 200` NPC loops. Only the first — the main hit scan — is reachable by a
        // HOSTILE projectile; the other three sit behind `type == 477`, `type == 10` and `type == 11 || 463`,
        // all player weapons, so ScanBound hands them the full cap anyway. That is why this rewrites every
        // match rather than trying to single one out: a bound that is correct for all four needs no anchor,
        // and an anchored patch here would be exactly the kind that silently stops matching after an update.
        //
        // The call is HOISTED to the top of the method and cached in a local, and each `ldc.i4 200` becomes a
        // read of that local. It must not be emitted in place of the literal: a C# `for (i = 0; i < N; i++)`
        // compiles its bound into the per-iteration condition, which the engine's own IL confirms —
        //
        //     IL_47bc: ldloc.s 15      // i
        //     IL_47be: ldc.i4 200      // <- the literal we replace
        //     IL_47c3: clt
        //     IL_47c5: ldloc.s 13      // flag4
        //     IL_47c7: and
        //     IL_47c8: brtrue IL_0aa7  // back edge
        //
        // so substituting the call there would run it once PER NPC. Hostile projectiles would barely notice
        // (their bound is tiny, so the loop turns over a couple of times), but every friendly projectile would
        // make one call per slot — thousands per projectile per tick, on the player's own weapons. That is a
        // measurable cost added to the exact path this patch is not supposed to touch.
        //
        // Hoisting is also the more correct shape. `Damage()` is one projectile in one tick, so the bound
        // cannot legitimately change part-way through; evaluating it per iteration left it free to move under
        // a running loop (NoteFriendlySlot can grow it mid-tick), which is a race with nothing to gain.
        private static void Patch_DamageHitScanBounds(ILContext il)
        {
            MethodInfo scanBound = typeof(HostileHitScan).GetMethod(
                nameof(HostileHitScan.ScanBound), BindingFlags.Public | BindingFlags.Static);
            if (scanBound == null)
                throw new Exception("HostileHitScan.ScanBound not found");

            var bound = new VariableDefinition(il.Import(typeof(int)));
            il.Body.Variables.Add(bound);

            // `int bound = HostileHitScan.ScanBound(this);` before anything else runs. Damage() is an instance
            // method with no parameters, so ldarg.0 is `this`.
            var c = new ILCursor(il) { Index = 0 };
            c.Emit(OpCodes.Ldarg_0);
            c.Emit(OpCodes.Call, il.Import(scanBound));
            c.Emit(OpCodes.Stloc, bound);

            // Mutated in place rather than removed and re-emitted, so the loop's back-edge branch target
            // survives untouched.
            int count = 0;
            while (c.TryGotoNext(MoveType.Before, i => SafeLdcI4(i, 200)))
            {
                Instruction instr = c.Next!;
                instr.OpCode = OpCodes.Ldloc;
                instr.Operand = bound;
                c.Index++;
                count++;
            }

            if (count == 0)
                throw new Exception("no `ldc.i4 200` found in Projectile.Damage");
        }

        private static void Patch_AllNpcLoopBounds200(ILContext il)
        {
            var c = new ILCursor(il);
            int count = 0;
            while (c.TryGotoNext(MoveType.Before, i => SafeLdcI4(i, 200)))
            {
                ReplaceWithNpcCap(c, il);
                c.Index++;
                count++;
            }

            if (count == 0)
                throw new Exception("no `ldc.i4 200` found");
        }

        // Replace every `ldsfld Main.maxNPCs` with the literal 200, pinning a method to vanilla behaviour.
        //
        // THROWS on zero matches. This used to be silent, justified as "if the JIT already baked maxNPCs to
        // 200, there is nothing to do" — which is wrong twice: a manipulator rewrites IL, where the field read
        // is always present, and the JIT has not run yet anyway. Both current targets (WorldIO.SaveNPCs and
        // LoadNPCs) read `Main.maxNPCs` live, several times each, so a zero match cannot be the benign case.
        // It can only mean the anchor stopped matching.
        //
        // Being silent here was the most expensive silence in the file: these two patches are what stop a
        // world saved at the raised cap from being written with vanilla's bounds, and Apply() would still log
        // "IL patched: WorldIO.SaveNPCs" with nothing behind it. Failing loudly costs one skipped patch and a
        // logged error; failing quietly costs a save file.
        private static void Patch_ForceVanilla200(ILContext il)
        {
            var c = new ILCursor(il);
            int count = 0;
            while (c.TryGotoNext(MoveType.Before, i => i.MatchLdsfld(MaxNPCsField)))
            {
                c.Next.OpCode = OpCodes.Ldc_I4;
                c.Next.Operand = 200;
                c.Index++;
                count++;
            }

            if (count == 0)
                throw new Exception("no `ldsfld Main.maxNPCs` found to pin to 200");
        }

        // DoUpdateInWorld: anchor on the `npc[l].UpdateNPC(l)` call, then patch the next loop bound.
        private static void Patch_UpdateLoop(ILContext il)
        {
            var c = new ILCursor(il);

            if (!c.TryGotoNext(i => i.MatchCallvirt(typeof(NPC).GetMethod(nameof(NPC.UpdateNPC)))))
                throw new Exception("UpdateNPC call anchor not found");

            if (!c.TryGotoNext(MoveType.Before, i => SafeLdcI4(i, 200)))
                throw new Exception("update-loop bound `ldc.i4 200` not found after UpdateNPC");

            ReplaceWithNpcCap(c, il);
        }

        // DrawNPCs: `for (num = 199; num >= 0; num--)` -> start the descending loop at maxNPCs - 1.
        private static void Patch_DrawLoop(ILContext il)
        {
            var c = new ILCursor(il);

            if (!c.TryGotoNext(MoveType.Before, i => SafeLdcI4(i, 199)))
                throw new Exception("draw-loop init `ldc.i4 199` not found");

            ReplaceWithNpcCap(c, il); // ldc.i4 199 -> ldsfld maxNPCs
            c.Index++;                 // move past the ldsfld
            c.Emit(OpCodes.Ldc_I4_1);  // push 1
            c.Emit(OpCodes.Sub);       // maxNPCs - 1
        }

        // Projectile..ctor: the `new int[200]` field initializer for localNPCImmunity.
        private static void Patch_ProjectileImmunityAlloc(ILContext il)
            => PatchArrayFieldInit(il, typeof(Projectile).GetField(nameof(Projectile.localNPCImmunity)));

        // Player..ctor: the `new int[200]` field initializer for meleeNPCHitCooldown.
        private static void Patch_PlayerMeleeCooldownAlloc(ILContext il)
            => PatchArrayFieldInit(il, typeof(Player).GetField(nameof(Player.meleeNPCHitCooldown)));

        /// <summary>
        /// Patch a `field = new T[200]` initializer in a constructor: anchor on the field store, walk back to
        /// the size literal, and swap it for <c>Main.maxNPCs</c>. Surgical so sibling [200] arrays in the same
        /// ctor (e.g. the Player bed-spawn arrays) are untouched.
        /// </summary>
        private static void PatchArrayFieldInit(ILContext il, FieldInfo field)
        {
            var c = new ILCursor(il);

            if (!c.TryGotoNext(i => i.MatchStfld(field)))
                throw new Exception($"`stfld {field?.Name}` not found");

            if (!c.TryGotoPrev(MoveType.Before, i => SafeLdcI4(i, 200)))
                throw new Exception($"{field?.Name} size literal `ldc.i4 200` not found");

            ReplaceWithNpcCap(c, il);
        }
    }
}
