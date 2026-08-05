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

        public static void ApplyAll(Mod mod)
        {
            ModRef = mod;

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
                Patch_AllNpcLoopBounds200);

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
            Type worldIO = typeof(Main).Assembly.GetType("Terraria.ModLoader.IO.WorldIO");
            Apply(mod, "WorldIO.SaveNPCs",
                worldIO?.GetMethod("SaveNPCs", BindingFlags.NonPublic | BindingFlags.Static),
                Patch_ForceVanilla200);
            Apply(mod, "WorldIO.LoadNPCs",
                worldIO?.GetMethod("LoadNPCs", BindingFlags.NonPublic | BindingFlags.Static),
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
            PatchMethod(mod, typeof(Player), "UpdateMeleeHitCooldowns");   // melee re-hit (decrement cooldown for high slots)
            PatchMethod(mod, typeof(Player), "ResetMeleeHitCooldowns");    // melee cooldown reset on death
            PatchMethod(mod, typeof(Player), "CollideWithNPCs");           // player contact/touch damage
            PatchMethod(mod, typeof(Player), "JumpMovement");             // slime-mount bounce damage
            PatchMethod(mod, typeof(Player), "DashMovement");            // dash (Tabi/Master Ninja) damage
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
                if (!c.TryGotoNext(i => i.MatchLdcI4(50), i => i.MatchCall(anyNpcs)))
                    throw new Exception("AnyNPCs(50) anchor");
                if (!c.TryGotoPrev(i => i.MatchLdcI4(300)))
                    throw new Exception("rand.Next(300) literal");
                ScaleAfterLiteral(c, scaleOdds);
            });

            Roll("Prismatic Lacewing", () =>
            {
                var c = new ILCursor(il);
                // `... && Main.player[k].RollLuck(10) == 0 && !AnyNPCs(661)` — anchor on the AnyNPCs(661) guard,
                // which is the only one in the game, then step back onto RollLuck's own argument.
                if (!c.TryGotoNext(i => i.MatchLdcI4(661), i => i.MatchCall(anyNpcs)))
                    throw new Exception("AnyNPCs(661) anchor");
                if (!c.TryGotoPrev(i => i.MatchLdcI4(10),
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
                  && c.TryGotoNext(MoveType.Before, i => i.MatchLdcI4(200))))
                throw new Exception("town danger-detect loop not found");
            ReplaceWithNpcCap(c, il);

            if (!(c.TryGotoNext(i => i.MatchCall(out MethodReference m) && m.Name == "TweakSwingStats")
                  && c.TryGotoNext(MoveType.Before, i => i.MatchLdcI4(200))))
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

            if (!c.TryGotoNext(MoveType.Before, i => i.MatchLdcI4(200)))
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
            if (!c.TryGotoNext(MoveType.Before, i => i.MatchLdcI4(200)))
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

            if (!c.TryGotoNext(MoveType.Before, i => i.MatchLdcI4(200)))
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
            if (!c.TryGotoNext(MoveType.Before, i => i.MatchLdcI4(200)))
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
            if (!c.TryGotoNext(MoveType.Before, i => i.MatchLdcI4(200)))
                throw new Exception("analyzer scan loop bound not found");
            ReplaceWithNpcCap(c, il);

            // 3b. Display validity check `num14 < 200` — the next 200 after the loop bound, still inside the
            // analyzer block (the radar's own 200 comes later and is handled by the radar patch).
            if (!c.TryGotoNext(MoveType.Before, i => i.MatchLdcI4(200)))
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
            while (c.TryGotoNext(MoveType.Before, i => i.MatchLdcI4(200)))
            {
                ReplaceWithNpcCap(c, il);
                c.Index++;
                n200++;
            }
            if (n200 == 0)
                throw new Exception("health-bar `ldc.i4 200` helper loops not found");

            var c2 = new ILCursor(il);
            if (!c2.TryGotoNext(MoveType.Before, i => i.MatchLdcI4(199)))
                throw new Exception("health-bar main draw loop init `ldc.i4 199` not found");
            ReplaceWithNpcCap(c2, il); // 199 -> NpcCap
            c2.Index++;
            c2.Emit(OpCodes.Ldc_I4_1);
            c2.Emit(OpCodes.Sub);      // NpcCap - 1
        }

        // Discrete projectile AIs (outside the monolithic AI()) that contain their own NPC-target scan.
        private static readonly string[] ChaseLoopAIMethods =
        {
            "AI_001", "AI_009_MagicMissiles", "AI_015_Flails", "AI_015_Flails_Old", "AI_016", "AI_026",
            "AI_047_MagnetSphere_TryAttacking", "AI_062" /*Abigail*/, "AI_067_TigerSpecialAttack",
            "AI_099_1", "AI_099_2", "AI_100_Medusa", "AI_120_StardustGuardian", "AI_121_StardustDragon",
            "AI_130_FlameBurstTower_FindTarget", "AI_134_Ballista_FindTarget", "AI_137_CanHit",
            "AI_137_LightningAura", "AI_138_ExplosiveTrap", "AI_147_Celeb2Rocket", "AI_152_SuperStarSlash",
            "AI_156_Think", "AI_156_TryAttackingNPCs", "AI_158_GetHomeLocation", "AI_169_Smolstars",
            "AI_171_HallowBossRainbowStreak", "AI_172_GetPelletStormInfo", "AI_176_EdgyLightning",
            "AI_177_IceWhipSlicer",
        };

        /// <summary>Resolve a projectile AI method by name and apply the NPC chase-loop widening heuristic.</summary>
        private static void PatchChaseLoops(Mod mod, Type type, string name)
        {
            MethodInfo method;
            try
            {
                method = type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
            }
            catch (AmbiguousMatchException)
            {
                mod.Logger.Error($"[MMM] {type.Name}.{name} is ambiguous; chase-loop patch skipped.");
                return;
            }

            Apply(mod, $"{type.Name}.{name} (chase loops)", method, Patch_NpcChaseLoops);
        }

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

        // Broader sibling of Patch_NpcChaseLoops: widen a backward-branch `< 200` loop if its body references
        // ANY member declared on NPC (a field like `active`/`chaseable` or a method like `CanBeChasedBy`),
        // i.e. it's iterating NPCs. Catches homing/targeting loops that don't use CanBeChasedBy. Restricted to
        // discrete per-weapon AI methods (small, NPC-focused) — NOT the monolithic AI() — to bound risk.
        private static void Patch_NpcLoops(ILContext il)
        {
            var instrs = il.Body.Instructions;
            int count = 0;

            for (int j = 0; j < instrs.Count; j++)
            {
                if (!instrs[j].MatchLdcI4(200) || !TryFindLoopBodyStart(instrs, j, out int target))
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
            }

            if (count > 0)
                ModRef?.Logger.Info($"[MMM] npc-loop patch widened {count} loop(s) in {il.Method.Name}");
        }

        // Widen NPC-targeting loops without touching unrelated 200s. Find every `ldc.i4 200` that is the bound
        // of a BACKWARD-branch loop (`ldloc i; ldc.i4 200; blt BODY`, the standard `for (i=0; i<200; i++)`
        // shape), then widen it ONLY if a `CanBeChasedBy` call appears inside that loop's actual body
        // (between the branch target and the bound). Checking the real body — not a fixed window — handles
        // loops with long bodies (e.g. the spider sentry's guard-at-top scan) and both the `npc[i]` and the
        // `NPC nPC = npc[i]` patterns. Unrelated 200s (no chase call in their loop, or not a loop bound at all)
        // are left untouched. Does not throw on zero matches.
        private static void Patch_NpcChaseLoops(ILContext il)
        {
            var instrs = il.Body.Instructions;
            int count = 0;

            for (int j = 0; j < instrs.Count; j++)
            {
                if (!instrs[j].MatchLdcI4(200) || !TryFindLoopBodyStart(instrs, j, out int target))
                    continue;

                bool chase = false;
                for (int b = target; b < j; b++)
                {
                    if (instrs[b].Operand is MethodReference m && m.Name == "CanBeChasedBy")
                    {
                        chase = true;
                        break;
                    }
                }
                if (!chase)
                    continue;

                instrs[j].OpCode = OpCodes.Ldsfld;
                instrs[j].Operand = il.Import(NpcCapField);
                count++;
            }

            if (count > 0)
                ModRef?.Logger.Info($"[MMM] chase-loop patch widened {count} loop(s) in {il.Method.Name}");
        }

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
            Apply(mod, "NPCUtils.SearchForTarget (crystal/NPC targeting)", search, Patch_NpcLoops);

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

        private static void Apply(Mod mod, string name, MethodBase method, ILContext.Manipulator manip)
        {
            if (method == null)
            {
                mod.Logger.Error($"[MMM] IL target not found: {name} — cap raise incomplete.");
                return;
            }

            try
            {
                MonoModHooks.Modify(method, manip);
                mod.Logger.Info($"[MMM] IL patched: {name}");
            }
            catch (Exception e)
            {
                mod.Logger.Error($"[MMM] IL patch FAILED for {name} (skipped; game stays stable): {e.Message}");
            }
        }

        /// <summary>Turn the <c>ldc.i4</c> the cursor is positioned before into <c>ldsfld EngineState.NpcCap</c>.</summary>
        private static void ReplaceWithNpcCap(ILCursor c, ILContext il)
        {
            Instruction instr = c.Next!;
            instr.OpCode = OpCodes.Ldsfld;
            instr.Operand = il.Import(NpcCapField);
        }

        // Methods that contain exactly one NPC-bound `ldc.i4 200` (GetAvailableNPCSlot, NewNPC return).
        private static void Patch_SingleLiteral200(ILContext il)
        {
            var c = new ILCursor(il);
            int count = 0;
            while (c.TryGotoNext(MoveType.Before, i => i.MatchLdcI4(200)))
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
        private static void Patch_AllNpcLoopBounds200(ILContext il)
        {
            var c = new ILCursor(il);
            int count = 0;
            while (c.TryGotoNext(MoveType.Before, i => i.MatchLdcI4(200)))
            {
                ReplaceWithNpcCap(c, il);
                c.Index++;
                count++;
            }

            if (count == 0)
                throw new Exception("no `ldc.i4 200` found");
        }

        // Replace every `ldsfld Main.maxNPCs` with the literal 200, pinning a method to vanilla behaviour.
        // Does not throw on zero matches: if the JIT already baked maxNPCs to 200, there is nothing to do.
        private static void Patch_ForceVanilla200(ILContext il)
        {
            var c = new ILCursor(il);
            while (c.TryGotoNext(MoveType.Before, i => i.MatchLdsfld(MaxNPCsField)))
            {
                c.Next.OpCode = OpCodes.Ldc_I4;
                c.Next.Operand = 200;
                c.Index++;
            }
        }

        // DoUpdateInWorld: anchor on the `npc[l].UpdateNPC(l)` call, then patch the next loop bound.
        private static void Patch_UpdateLoop(ILContext il)
        {
            var c = new ILCursor(il);

            if (!c.TryGotoNext(i => i.MatchCallvirt(typeof(NPC).GetMethod(nameof(NPC.UpdateNPC)))))
                throw new Exception("UpdateNPC call anchor not found");

            if (!c.TryGotoNext(MoveType.Before, i => i.MatchLdcI4(200)))
                throw new Exception("update-loop bound `ldc.i4 200` not found after UpdateNPC");

            ReplaceWithNpcCap(c, il);
        }

        // DrawNPCs: `for (num = 199; num >= 0; num--)` -> start the descending loop at maxNPCs - 1.
        private static void Patch_DrawLoop(ILContext il)
        {
            var c = new ILCursor(il);

            if (!c.TryGotoNext(MoveType.Before, i => i.MatchLdcI4(199)))
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

            if (!c.TryGotoPrev(MoveType.Before, i => i.MatchLdcI4(200)))
                throw new Exception($"{field?.Name} size literal `ldc.i4 200` not found");

            ReplaceWithNpcCap(c, il);
        }
    }
}
