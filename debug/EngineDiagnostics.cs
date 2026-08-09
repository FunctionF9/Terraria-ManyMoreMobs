using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace ManyMoreMobs
{
    /// <summary>
    /// Read-only inspection of the live engine state, focused on the invariants the cap raise depends on.
    /// Everything returns a human-readable report string (printed to chat as a summary and written in full to
    /// the state log via <see cref="MmmLog"/>). The validator is the high-value tool: it surfaces NPC-indexed
    /// arrays or instanced-global arrays that are too short for the raised cap BEFORE they crash.
    /// </summary>
    public static class EngineDiagnostics
    {
        private static readonly FieldInfo GlobalsField =
            typeof(NPC).GetField("_globals", BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly FieldInfo MaxNPCsField =
            typeof(Main).GetField(nameof(Main.maxNPCs), BindingFlags.Public | BindingFlags.Static);

        // Vanilla spawn baselines (private statics) — read by reflection for the spawninfo readout.
        private static readonly FieldInfo DefaultSpawnRateField =
            typeof(NPC).GetField("defaultSpawnRate", BindingFlags.NonPublic | BindingFlags.Static);
        private static readonly FieldInfo DefaultMaxSpawnsField =
            typeof(NPC).GetField("defaultMaxSpawns", BindingFlags.NonPublic | BindingFlags.Static);

        private static int DefaultSpawnRate() { try { return (int)(DefaultSpawnRateField?.GetValue(null) ?? 600); } catch { return 600; } }
        private static int DefaultMaxSpawns() { try { return (int)(DefaultMaxSpawnsField?.GetValue(null) ?? 5); } catch { return 5; } }

        /// <summary>Length of an NPC's tModLoader instanced-globals array (-1 if null, -2 on reflection error).</summary>
        public static int GlobalsLength(NPC npc)
        {
            try { return (GlobalsField?.GetValue(npc) as Array)?.Length ?? -1; }
            catch { return -2; }
        }

        private static int ReflectionMaxNPCs()
        {
            try { return (int)(MaxNPCsField?.GetValue(null) ?? -1); }
            catch { return -1; }
        }

        /// <summary>Cap/array state plus a per-category histogram split into vanilla (0-199) and bonus (200+) zones.</summary>
        public static string BuildStateReport()
        {
            var sb = new StringBuilder();
            var config = ModContent.GetInstance<ManyMoreMobsConfig>();

            sb.AppendLine($"AppliedCap={MaxNpcCapRaise.AppliedCap}  Main.maxNPCs(direct)={Main.maxNPCs} (reflection)={ReflectionMaxNPCs()}  Main.npc.Length={Main.npc.Length}");
            if (config != null)
            {
                var caps = config.GetEffectiveCaps();
                sb.AppendLine($"config: mode={config.CapMode} target={config.MaxNPCTotal} effectiveTotal={config.EffectiveTotal}");
                sb.AppendLine($"effective caps  T/B/C/E = {caps.town}/{caps.boss}/{caps.critter}/{caps.enemy}");
            }

            // Three buckets, not two. Main.npc is CAP+1 long — the extra entry is the dummy/failure slot that
            // NewNPC returns when it can't place anything, and it sits ABOVE the cap, outside every widened
            // loop. Counting it in with the rest is how this report used to disagree with /debugnpc counts
            // (which stops at the cap): a total of 751 against the cap's 750 looked like an off-by-one in the
            // report when it actually meant something had gone active in a slot the engine will never update.
            int[] low = new int[4], high = new int[4], over = new int[4];
            int cap = EngineState.NpcCap;
            for (int i = 0; i < Main.npc.Length; i++)
            {
                NPC n = Main.npc[i];
                if (n == null || !n.active)
                    continue;
                int c = (int)NpcCategorizer.Categorize(n);
                if (i >= cap) over[c]++;
                else if (i < 200) low[c]++;
                else high[c]++;
            }
            sb.AppendLine($"active 0-199  T/B/C/E = {low[0]}/{low[1]}/{low[2]}/{low[3]}  (total {low.Sum()})");
            sb.AppendLine($"active 200+   T/B/C/E = {high[0]}/{high[1]}/{high[2]}/{high[3]}  (total {high.Sum()})");
            sb.AppendLine($"active TOTAL  = {low.Sum() + high.Sum()}  (within cap {cap})");
            if (over.Sum() > 0)
                sb.AppendLine($"dummy slot occupied (slot >= {cap}) = {over.Sum()}  T/B/C/E = {over[0]}/{over[1]}/{over[2]}/{over[3]}"
                            + "  — expected: vanilla's blocked-spawn paths SetDefaults() the failure slot, which activates it. Inert (outside every loop). /debugnpc dumpall names it.");

            // List every NPC the mod counts as Town, so a surprising count (e.g. the world's Guide alive
            // off-screen, or a naturally-spawned Skeleton Merchant) can be identified at a glance.
            var towns = new List<string>();
            for (int i = 0; i < Main.npc.Length; i++)
            {
                NPC n = Main.npc[i];
                if (n != null && n.active && NpcCategorizer.Categorize(n) == NpcCategory.Town)
                    towns.Add($"#{i} {SafeName(n)}");
            }
            sb.AppendLine($"town NPCs ({towns.Count}): {(towns.Count == 0 ? "(none)" : string.Join(", ", towns))}");

            // Spawn pressure for the local player (QoL — full breakdown via /debugnpc spawninfo).
            if (SpawnRateMultiplier.CapturedTick >= 0)
                sb.AppendLine($"spawn (you): rate {SpawnRateMultiplier.InRate}->{SpawnRateMultiplier.OutRate}, max {SpawnRateMultiplier.InMax}->{SpawnRateMultiplier.OutMax}, src={SpawnRateMultiplier.UsedSource}, bossThrottle={(SpawnRateMultiplier.BossThrottled ? "on" : "off")}");
            else
                sb.AppendLine("spawn (you): no data captured yet (stand where enemies can spawn briefly)");
            return sb.ToString();
        }

        /// <summary>
        /// The local player's current effective spawn rate &amp; limit, the source (biome/blanket) and which
        /// spawn-item modifiers are active. Values are captured live in <see cref="SpawnRateMultiplier"/>.
        /// </summary>
        public static string BuildSpawnInfoReport(Player p)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"vanilla base: defaultSpawnRate={DefaultSpawnRate()} defaultMaxSpawns={DefaultMaxSpawns()}");

            if (SpawnRateMultiplier.CapturedTick < 0)
            {
                sb.AppendLine("no spawn data captured yet — stand somewhere enemies can spawn for a moment, then retry.");
            }
            else
            {
                long age = (long)Main.GameUpdateCount - SpawnRateMultiplier.CapturedTick;
                string stale = age > 120 ? $"   (STALE: last {age} ticks / ~{age / 60}s ago — spawns may be suppressed here)" : "";
                int outRate = SpawnRateMultiplier.OutRate;
                sb.AppendLine($"effective spawnRate: {SpawnRateMultiplier.InRate} -> {outRate}{stale}");
                sb.AppendLine($"  ~1 spawn attempt per {outRate} ticks (~{60f / Math.Max(1, outRate):0.00}/sec while under the limit). Lower = faster.");
                sb.AppendLine($"effective maxSpawns (near-you limit): {SpawnRateMultiplier.InMax} -> {SpawnRateMultiplier.OutMax}");
                sb.AppendLine($"source: {SpawnRateMultiplier.UsedSource}  (rate x{SpawnRateMultiplier.UsedRateMult:0.##}, max x{SpawnRateMultiplier.UsedMaxMult:0.##})");
                sb.AppendLine($"boss throttle: {(SpawnRateMultiplier.BossThrottled ? "ACTIVE" : "off")}");
                sb.AppendLine(RareSpawnNormalizer.Describe());
            }

            // Active spawn-item modifiers on this player (and the strength each is configured at).
            var items = ModContent.GetInstance<SpawnItemsConfig>();
            var active = new List<string>();
            void Add(bool on, string name, float strength) { if (on) active.Add($"{name} (x{strength:0.##})"); }
            Add(p.invis, "Invisibility", items?.Invisibility ?? 1f);
            Add(p.calmed, "Calming", items?.CalmingPotion ?? 1f);
            Add(p.sunflower, "Sunflower", items?.Sunflower ?? 1f);
            Add(p.anglerSetSpawnReduction, "Angler set", items?.AnglerSet ?? 1f);
            Add(p.enemySpawns, "Battle Potion", items?.BattlePotion ?? 1f);
            Add(p.ZoneWaterCandle || Holding(p, 148), "Water Candle", items?.WaterCandle ?? 1f);
            Add(p.ZonePeaceCandle || Holding(p, 3117), "Peace Candle", items?.PeaceCandle ?? 1f);
            Add(p.ZoneShadowCandle || Holding(p, 5322), "Shadow Candle", items?.ShadowCandle ?? 1f);
            Add(p.isNearFairy(), "Near Fairy", items?.Fairy ?? 1f);
            sb.AppendLine($"active spawn-item modifiers: {(active.Count == 0 ? "(none)" : string.Join(", ", active))}");
            return sb.ToString();
        }

        private static bool Holding(Player p, int itemType)
        {
            try { return p.inventory?[p.selectedItem]?.type == itemType; } catch { return false; }
        }

        /// <summary>
        /// Dumps the per-NPC hit-immunity state of the enemy nearest the player — to diagnose weapons that
        /// can't damage an enemy. Shows the NPC's per-player i-frames, the per-projectile-type static immunity
        /// for this slot (perIDStaticNPCImmunity), and any of your active projectiles' local immunity on it.
        /// </summary>
        private static NPC FindNearestEnemy(Player p, out float dist)
        {
            NPC target = null;
            float best = float.MaxValue;
            for (int i = 0; i < Main.npc.Length; i++)
            {
                NPC n = Main.npc[i];
                if (n == null || !n.active || n.friendly || n.townNPC)
                    continue;
                float d = n.Distance(p.Center);
                if (d < best) { best = d; target = n; }
            }
            dist = best;
            return target;
        }

        /// <summary>
        /// Directly strikes the nearest enemy with the engine's own damage path (<see cref="NPC.SimpleStrikeNPC"/>)
        /// and reports the life delta. This isolates the question "can this NPC take damage AT ALL?" from
        /// whether a projectile can reach it — if the strike lands but weapons can't, the fault is in the
        /// projectile reach/hit-detection, not the NPC's state.
        /// </summary>
        public static string BuildHitTestReport(Player p)
        {
            NPC target = FindNearestEnemy(p, out float dist);
            if (target == null)
                return "no nearby enemy found.";

            int slot = target.whoAmI;
            int before = target.life;
            int dealt = target.SimpleStrikeNPC(50, target.direction, false, 0f, DamageClass.Default, false, 0f, true);
            int after = target.life;
            return $"hittest: slot {slot} type {target.type} '{SafeName(target)}' dist {(int)dist}  life {before} -> {after}  (SimpleStrikeNPC reported {dealt})  "
                 + (before != after ? "=> NPC TOOK DAMAGE (fault is in projectile reach, not the NPC)" : "=> NPC IGNORED direct damage (NPC-side state)");
        }

        /// <summary>
        /// Live event state: invasions, moon waves, Lunar pillars and Old One's Army — plus the LOW-vs-HIGH slot
        /// split of the NPCs each one depends on.
        /// <para/>
        /// That split is the whole point. Most "event X is broken with this mod" reports are an engine loop that
        /// still stops at slot 200: the event works perfectly while its NPCs happen to land low, and silently
        /// fails once they spill into the expanded zone — which is exactly why these bugs look intermittent and
        /// why one player sees them and another doesn't. If a counter here reads 0 while the matching "high"
        /// column is non-zero, that's an unpatched loop, not bad luck. Cross-reference tools/audit-report.md.
        /// </summary>
        /// <summary>
        /// Reports the numbers the Old One's Army wave logic actually runs on, measured rather than reasoned
        /// about.
        /// <para/>
        /// Written because a code-level argument about whether our wave-length multiplier breaks the tier-1
        /// Dark Mage could not be settled either way: the reading said it must deadlock, the author had
        /// completed the event repeatedly at the same settings. Both cannot be true, so print the values.
        /// <para/>
        /// <c>reqLive</c> is what <c>GetInvasionStatus</c> hands the game AFTER our hook, and is the number the
        /// Dark Mage's <c>currentKillCount &gt; requiredKillCount * 0.5f</c> gate is measured against. If
        /// <c>reqLive</c> equals <c>reqVanilla</c>, the multiplier is not reaching this path at all and the
        /// theory is dead. If it is five times larger while <c>cur</c> is pinned at 139, it is confirmed.
        /// </summary>
        private static void AppendOldOnesArmyWaveMath(StringBuilder sb)
        {
            if (!Terraria.GameContent.Events.DD2Event.Ongoing)
            {
                sb.AppendLine("  (event not running — run this DURING an Old One's Army for the wave numbers)");
                return;
            }

            int difficulty = Terraria.GameContent.Events.DD2Event.OngoingDifficulty;
            int wave = NPC.waveNumber;
            float waveKills = NPC.waveKills;

            // GetInvasionStatus is private and hooked by us; calling it through reflection reports exactly what
            // the game sees, our hook included.
            int reqLive = -1, curLive = -1, waveLive = -1;
            try
            {
                MethodInfo m = typeof(Terraria.GameContent.Events.DD2Event)
                    .GetMethod("GetInvasionStatus", BindingFlags.NonPublic | BindingFlags.Static);
                if (m != null)
                {
                    object[] args = { 0, 0, 0, false };
                    m.Invoke(null, args);
                    waveLive = (int)args[0];
                    reqLive = (int)args[1];
                    curLive = (int)args[2];
                }
            }
            catch (Exception e)
            {
                sb.AppendLine($"  GetInvasionStatus probe failed: {e.GetType().Name}");
            }

            var events = ModContent.GetInstance<EventsConfig>();
            float multiplier = events?.OldOnesArmyWaveLengthMultiplier ?? -1f;
            int finalWave = difficulty == 1 ? 5 : 7;
            int reqVanilla = VanillaRequiredKills(difficulty, wave);

            sb.AppendLine($"  wave={wave} (final for this tier = {finalWave})  waveKills={waveKills}  "
                        + $"spawnOnHold={Terraria.GameContent.Events.DD2Event.EnemySpawningIsOnHold}");
            sb.AppendLine($"  GetInvasionStatus -> wave={waveLive} req={reqLive} cur={curLive}   "
                        + $"(vanilla req for this wave = {reqVanilla}, WaveLengthMultiplier={multiplier})");

            if (difficulty == 1 && wave == 5 && reqLive > 0)
            {
                // The one gate that decides whether the tier-1 Dark Mage may spawn.
                float threshold = reqLive * 0.5f;
                bool passes = curLive > threshold;
                bool alreadyOut = NPC.AnyNPCs(NPCID.DD2DarkMageT1);
                sb.AppendLine($"  DARK MAGE GATE: cur({curLive}) > req({reqLive})*0.5 = {threshold}  -> {passes}"
                            + $"   alreadyAlive={alreadyOut}");
                if (!passes)
                    sb.AppendLine($"    cur cannot exceed 139 on this wave until the Dark Mage dies, so a "
                                + $"threshold above 139 can never be met.");
            }

            int mages = 0, mageSlotLow = -1, mageSlotHigh = -1;
            for (int i = 0; i < EngineState.NpcCap; i++)
            {
                NPC n = Main.npc[i];
                if (n == null || !n.active)
                    continue;
                if (n.type == NPCID.DD2DarkMageT1 || n.type == NPCID.DD2DarkMageT3)
                {
                    mages++;
                    if (i < 200) mageSlotLow = i; else mageSlotHigh = i;
                }
            }
            sb.AppendLine($"  Dark Mages alive: {mages} (lowSlot={mageSlotLow} highSlot={mageSlotHigh})");
        }

        /// <summary>Vanilla's un-multiplied required kills, so the report can show what our hook changed.</summary>
        private static int VanillaRequiredKills(int difficulty, int wave) => difficulty switch
        {
            1 => wave switch { 1 => 60, 2 => 80, 3 => 100, 4 => 120, 5 => 140, _ => 10 },
            2 => wave switch { 1 => 60, 2 => 80, 3 => 100, 4 => 120, 5 => 140, 6 => 180, 7 => 220, _ => 10 },
            _ => wave switch { 1 => 60, 2 => 80, 3 => 100, 4 => 120, 5 => 140, 6 => 180, 7 => 100, _ => 10 },
        };

        public static string BuildEventReport()
        {
            var sb = new StringBuilder();
            int cap = EngineState.NpcCap;
            const int lowZone = 200; // the native engine range every unpatched vanilla loop is limited to

            sb.AppendLine($"NpcCap={cap}  npc.Length={Main.npc.Length}  GameUpdateCount={Main.GameUpdateCount}");

            // Invasions. invasionProgressNearInvasion is set by Main.CheckInvasionProgressDisplay, which we
            // patch — if it reads False while invaders are on screen, that patch stopped matching.
            sb.AppendLine($"INVASION type={Main.invasionType} size={Main.invasionSize}/{Main.invasionSizeStart} x={(int)Main.invasionX} delay={Main.invasionDelay}");
            sb.AppendLine($"  progress={Main.invasionProgress}/{Main.invasionProgressMax} wave={Main.invasionProgressWave} mode={Main.invasionProgressMode} nearInvasion={Main.invasionProgressNearInvasion}");

            // Moon events.
            sb.AppendLine($"MOON pumpkin={Main.pumpkinMoon} frost={Main.snowMoon} wave={NPC.waveNumber} kills={NPC.waveKills}");

            // Lunar pillars: shields only drop as their guards die, and the kill-credit scan is slot-bounded.
            sb.AppendLine($"PILLARS apocalypse={NPC.LunarApocalypseIsUp} shieldMax={NPC.ShieldStrengthTowerMax}  solar={NPC.ShieldStrengthTowerSolar} vortex={NPC.ShieldStrengthTowerVortex} nebula={NPC.ShieldStrengthTowerNebula} stardust={NPC.ShieldStrengthTowerStardust}");

            sb.AppendLine($"OLD ONE'S ARMY ongoing={Terraria.GameContent.Events.DD2Event.Ongoing} difficulty={Terraria.GameContent.Events.DD2Event.OngoingDifficulty}");
            AppendOldOnesArmyWaveMath(sb);

            // Slot split for the NPC groups these events are counted from.
            int totLow = 0, totHigh = 0, invLow = 0, invHigh = 0, moonLow = 0, moonHigh = 0, pillarLow = 0, pillarHigh = 0;
            for (int i = 0; i < cap; i++)
            {
                NPC n = Main.npc[i];
                if (n == null || !n.active)
                    continue;

                bool high = i >= lowZone;
                if (high) totHigh++; else totLow++;

                if (n.type >= 0 && n.type < NPCID.Sets.BelongsToInvasionOldOnesArmy.Length && NPCID.Sets.BelongsToInvasionOldOnesArmy[n.type])
                {
                    if (high) invHigh++; else invLow++;
                }
                if (n.value > 0f && (Main.pumpkinMoon || Main.snowMoon) && !n.friendly && n.damage > 0)
                {
                    if (high) moonHigh++; else moonLow++;
                }
                if (n.type == NPCID.LunarTowerSolar || n.type == NPCID.LunarTowerVortex
                    || n.type == NPCID.LunarTowerNebula || n.type == NPCID.LunarTowerStardust)
                {
                    if (high) pillarHigh++; else pillarLow++;
                }
            }

            sb.AppendLine($"SLOT SPLIT (low 0-{lowZone - 1} / high {lowZone}+):  all active {totLow}/{totHigh}   OOA-invasion {invLow}/{invHigh}   moon-enemies {moonLow}/{moonHigh}   pillars {pillarLow}/{pillarHigh}");
            if (pillarHigh > 0)
                sb.AppendLine("  !! a Lunar pillar is in a HIGH slot — pillars should be Boss-zoned in 0-199; shield/kill-credit scans may miss it");
            sb.AppendLine("  (an event counter stuck at 0 while its 'high' column is non-zero == an unpatched engine loop)");
            return sb.ToString();
        }

        public static string BuildImmunityReport(Player p)
        {
            NPC target = FindNearestEnemy(p, out float best);
            if (target == null)
                return "no nearby enemy found.";

            int slot = target.whoAmI;
            var sb = new StringBuilder();
            sb.AppendLine($"GameUpdateCount={Main.GameUpdateCount}  NpcCap={EngineState.NpcCap}  npc.Length={Main.npc.Length}");
            sb.AppendLine($"target enemy (nearest player): slot {slot} type {target.type} '{SafeName(target)}' life {target.life}/{target.lifeMax} dist {(int)best}");
            bool canHitProj = p.CanNPCBeHitByPlayerOrPlayerProjectile(target);
            sb.AppendLine($"  reach: slot<NpcCap={slot < EngineState.NpcCap}  dontTakeDamage={target.dontTakeDamage}  noTileCollide={target.noTileCollide}  CountsAsCritter={NPCID.Sets.CountsAsCritter[target.type]}  dontHurtCritters={p.dontHurtCritters}  CanNPCBeHitByProj={canHitProj}");

            var imm = new List<string>();
            if (target.immune != null)
                for (int pi = 0; pi < target.immune.Length; pi++)
                    if (target.immune[pi] != 0) imm.Add($"p{pi}={target.immune[pi]}");
            sb.AppendLine($"npc.immune nonzero: {(imm.Count == 0 ? "(none)" : string.Join(", ", imm))}");

            uint[][] perID = Projectile.perIDStaticNPCImmunity;
            var perSlot = new List<string>();
            if (perID != null)
                for (int t = 0; t < perID.Length; t++)
                {
                    uint[] a = perID[t];
                    if (a != null && slot < a.Length && a[slot] != 0) perSlot.Add($"projType{t}={a[slot]}");
                }
            sb.AppendLine($"perID static-immunity nonzero for this slot ({perSlot.Count}): {(perSlot.Count == 0 ? "(none)" : string.Join(", ", perSlot.Take(40)))}");

            // Dump EVERY active player projectile with its localNPCImmunity array SIZE + immunity flags. If a
            // projectile's array length is <= slot, the engine hit loop throws IndexOutOfRange at this slot and
            // the hit silently dies — the prime suspect for "projectile passes through, no damage" at high slots.
            var projs = new List<string>();
            int shortArrays = 0;
            for (int i = 0; i < Main.projectile.Length; i++)
            {
                Projectile pr = Main.projectile[i];
                if (pr == null || !pr.active || pr.owner != p.whoAmI)
                    continue;
                int len = pr.localNPCImmunity?.Length ?? -1;
                bool tooShort = len >= 0 && len <= slot;
                if (tooShort) shortArrays++;
                string immVal = (pr.localNPCImmunity != null && slot < len) ? pr.localNPCImmunity[slot].ToString() : "OOR";
                projs.Add($"#{i} type{pr.type} pen{pr.penetrate} local={pr.usesLocalNPCImmunity} id={pr.usesIDStaticNPCImmunity} arr.Len={len}{(tooShort ? " <<TOO-SHORT" : "")} [slot]={immVal}");
            }
            sb.AppendLine($"your active projectiles ({projs.Count}, {shortArrays} with TOO-SHORT arrays):");
            if (projs.Count == 0)
                sb.AppendLine("  (none — re-run while the weapon's projectile/flail head is out)");
            foreach (string s in projs.Take(20))
                sb.AppendLine("  " + s);
            return sb.ToString();
        }

        /// <summary>Checks every NPC-indexed array against the applied cap. <paramref name="anomalies"/> = problem count.</summary>
        public static string BuildValidationReport(out int anomalies)
        {
            anomalies = 0;
            var sb = new StringBuilder();
            int cap = MaxNpcCapRaise.AppliedCap;
            int len = Main.npc.Length;

            sb.AppendLine($"AppliedCap={cap}  Main.npc.Length={len} (expected {cap + 1})");
            if (len < cap + 1)
            {
                sb.AppendLine("  !! Main.npc is shorter than AppliedCap+1");
                anomalies++;
            }

            // Instanced-globals (_globals) length across active NPCs — this is what crashed world save.
            int expected = 0, active = 0;
            for (int i = 0; i < len; i++)
            {
                NPC n = Main.npc[i];
                if (n == null || !n.active) continue;
                int g = GlobalsLength(n);
                if (g > expected) expected = g;
            }
            var dist = new Dictionary<int, int>();
            var offenders = new List<string>();
            for (int i = 0; i < len; i++)
            {
                NPC n = Main.npc[i];
                if (n == null || !n.active) continue;
                active++;
                int g = GlobalsLength(n);
                dist[g] = dist.TryGetValue(g, out int c) ? c + 1 : 1;
                if (g >= 0 && g < expected)
                {
                    anomalies++;
                    if (offenders.Count < 20)
                        offenders.Add($"slot {i}: type {n.type} '{SafeName(n)}' _globals.Length={g} (expected {expected})");
                }
            }
            sb.AppendLine($"active NPCs={active}  _globals lengths: {(dist.Count == 0 ? "(none)" : string.Join(", ", dist.OrderBy(k => k.Key).Select(kv => $"len{kv.Key}x{kv.Value}")))}  expected={expected}");
            foreach (string o in offenders)
                sb.AppendLine("  !! " + o);

            // Companion arrays indexed by NPC slot.
            CheckArray(sb, ref anomalies, "NPC.lazyNPCOwnedProjectileSearchArray", NPC.lazyNPCOwnedProjectileSearchArray?.Length ?? -1, cap);

            uint[][] perId = Projectile.perIDStaticNPCImmunity;
            if (perId != null)
            {
                int min = int.MaxValue, max = 0, bad = 0;
                foreach (uint[] a in perId)
                {
                    if (a == null) continue;
                    min = Math.Min(min, a.Length);
                    max = Math.Max(max, a.Length);
                    if (a.Length < cap) bad++;
                }
                sb.AppendLine($"perIDStaticNPCImmunity: {perId.Length} types, inner len min={(min == int.MaxValue ? 0 : min)} max={max}, {bad} < cap");
                if (bad > 0) anomalies += bad;
            }

            int projBad = 0;
            for (int i = 0; i < Main.projectile.Length; i++)
            {
                int[] arr = Main.projectile[i]?.localNPCImmunity;
                if (arr != null && arr.Length < cap) projBad++;
            }
            sb.AppendLine($"projectiles localNPCImmunity < cap: {projBad}/{Main.projectile.Length}");
            if (projBad > 0) anomalies += projBad;

            int plBad = 0;
            for (int i = 0; i < Main.player.Length; i++)
            {
                int[] arr = Main.player[i]?.meleeNPCHitCooldown;
                if (arr != null && arr.Length < cap) plBad++;
            }
            sb.AppendLine($"players meleeNPCHitCooldown < cap: {plBad}");
            if (plBad > 0) anomalies += plBad;

            sb.AppendLine($"TOTAL anomalies: {anomalies}");
            return sb.ToString();
        }

        /// <summary>Deep-inspect a single NPC slot.</summary>
        public static string BuildSlotReport(int index)
        {
            if (index < 0 || index >= Main.npc.Length)
                return $"slot {index} out of range (0..{Main.npc.Length - 1})";

            NPC n = Main.npc[index];
            if (n == null)
                return $"slot {index}: <null>";

            var sb = new StringBuilder();
            sb.AppendLine($"slot {index}: active={n.active} type={n.type} netID={n.netID} '{SafeName(n)}'");
            sb.AppendLine($"  whoAmI={n.whoAmI} realLife={n.realLife} boss={n.boss} townNPC={n.townNPC} category={(n.active ? NpcCategorizer.Categorize(n).ToString() : "(inactive)")}");
            // Chain membership is what decides a worm segment's category, and it is invisible from the NPC
            // itself — an Eater of Worlds body has no realLife and no boss flag, so this line is the only way
            // to see why it counts as Boss.
            sb.AppendLine($"  aiStyle={n.aiStyle} chainMember={SegmentChain.IsMember(index)}"
                        + (SegmentChain.IsMember(index) ? $" chainCategory={SegmentChain.CategoryOf(index)}" : ""));
            sb.AppendLine($"  _globals.Length={GlobalsLength(n)}  life={n.life}/{n.lifeMax} damage={n.damage} dontTakeDamage={n.dontTakeDamage}");
            return sb.ToString();
        }

        /// <summary>
        /// Full census: one line per active NPC, plus a per-type histogram and a machine-checkable anomaly list.
        /// <para/>
        /// The aggregate reports answer "how many of each category" — this one answers "which NPC, in which
        /// slot, counted under which cap, and <b>why</b>". That last column is the point. Category is decided by
        /// three different routes (chain tag → <c>realLife</c> head → own flags) and the routes disagree in
        /// exactly the cases that produce bugs: an Eater of Worlds body has no boss flag and no <c>realLife</c>,
        /// so if its tag is missing it silently reverts to Enemy and nothing in the aggregate counts looks wrong
        /// — the boss budget is simply short and the enemy budget is quietly overspent. Printing the route makes
        /// that visible in one line instead of one <c>/debugnpc slot</c> call per segment.
        /// <para/>
        /// Written as TSV rather than prose: it is meant to be read back out of the state log and diffed.
        /// </summary>
        public static string BuildFullDumpReport(out int anomalies)
        {
            anomalies = 0;
            var sb = new StringBuilder();
            var problems = new List<string>();
            var notes = new List<string>();

            int cap = EngineState.NpcCap;
            int len = Main.npc.Length;
            var config = ModContent.GetInstance<ManyMoreMobsConfig>();
            var caps = config?.GetEffectiveCaps() ?? (0, 0, 0, 0);

            int[] byCat = new int[4];
            int lowUsed = 0, highUsed = 0, overUsed = 0;
            var typeHist = new Dictionary<(int type, NpcCategory cat), int>();
            var rows = new List<string>();

            for (int i = 0; i < len; i++)
            {
                NPC n = Main.npc[i];
                if (n == null || !n.active)
                    continue;

                bool over = i >= cap;
                string zone = over ? "X" : i < 200 ? "L" : "H";
                if (over) overUsed++;
                else if (i < 200) lowUsed++;
                else highUsed++;

                NpcCategory cat = NpcCategorizer.Categorize(n);
                if (!over)
                    byCat[(int)cat]++;

                var key = (n.type, cat);
                typeHist[key] = typeHist.TryGetValue(key, out int c) ? c + 1 : 1;

                string flags = string.Concat(
                    n.boss ? "b" : "-",
                    n.townNPC ? "t" : "-",
                    n.CountsAsACritter ? "c" : "-",
                    n.friendly ? "f" : "-",
                    n.dontTakeDamage ? "i" : "-");

                rows.Add(string.Join("\t",
                    i, zone, n.type, n.netID, cat, CategoryRoute(n),
                    SegmentChain.IsMember(i) ? SegmentChain.CategoryOf(i).ToString() : "-",
                    n.life, n.lifeMax, n.realLife, n.aiStyle, flags,
                    (int)(n.Center.X / 16f), (int)(n.Center.Y / 16f), n.timeLeft,
                    n.ModNPC?.Mod?.Name ?? "Terraria", SafeName(n)));

                // Anomalies. Each of these is a state that should be impossible, phrased so the line itself
                // says which rule was broken.
                //
                // The dummy slot is NOT one of them — see the notes section below. It was, until the cause
                // turned out to be plain vanilla behaviour; leaving it as an anomaly would mean re-triaging
                // the same non-bug on every dump, which is how a report stops being read.
                if (over)
                    notes.Add($"DUMMY_SLOT\tslot={i}\ttype={n.type}\tnetID={n.netID}\t'{SafeName(n)}'\tlife={n.life}\ttimeLeft={n.timeLeft}");
                if (n.whoAmI != i)
                    problems.Add($"WHOAMI_MISMATCH\tslot={i}\twhoAmI={n.whoAmI}\ttype={n.type}\t'{SafeName(n)}'");
                if (cat == NpcCategory.Town && !over && i >= 200)
                    problems.Add($"TOWN_HIGH\tslot={i}\ttype={n.type}\t'{SafeName(n)}'\t(SlotRezoner should have moved this below 200)");
            }

            anomalies = problems.Count;

            int lowFree = 0, highFree = 0;
            for (int i = 0; i < Math.Min(len, cap); i++)
                if (Main.npc[i] != null && !Main.npc[i].active) { if (i < 200) lowFree++; else highFree++; }

            sb.AppendLine($"cap={cap}\tnpcLength={len}\tnetMode={Main.netMode}\tmode={config?.CapMode}\tcapsTBCE={caps.Item1}/{caps.Item2}/{caps.Item3}/{caps.Item4}");
            sb.AppendLine($"activeTBCE={byCat[0]}/{byCat[1]}/{byCat[2]}/{byCat[3]}\ttotal={byCat.Sum()}\tzones L={lowUsed}(free {lowFree}) H={highUsed}(free {highFree}) overCap={overUsed}");

            // Which categories are over their configured ceiling, and why that is not automatically a bug:
            // segments of a multi-part body bypass the ceiling by design, so a worm boss can legitimately
            // carry its whole length past BossCap. The line reports it either way; the chain column above is
            // what tells the two apart.
            AppendCapLine(sb, "town", byCat[0], caps.Item1);
            AppendCapLine(sb, "boss", byCat[1], caps.Item2);
            AppendCapLine(sb, "critter", byCat[2], caps.Item3);
            AppendCapLine(sb, "enemy", byCat[3], caps.Item4);

            sb.AppendLine($"--- byType ({typeHist.Count} distinct) ---");
            foreach (var kv in typeHist.OrderByDescending(k => k.Value).ThenBy(k => k.Key.type))
                sb.AppendLine($"{kv.Value}\ttype={kv.Key.type}\t{kv.Key.cat}\t{Lang.GetNPCNameValue(kv.Key.type)}");

            if (notes.Count > 0)
            {
                // Traced 2026-08-09 from a census that reported it as an anomaly. NPC.SpawnNPC's natural-spawn
                // branches write to Main.npc[newNPC] WITHOUT checking whether the spawn succeeded — e.g. the
                // corruption branch rolls `Main.npc[newNPC].SetDefaults(-11/-12)` on the Eater of Souls it just
                // asked for. When the array is full, newNPC is the failure index, so that lands on the dummy
                // slot; and SetDefaults ends with `active = Type != 0`, which brings the dummy to life.
                // Vanilla does exactly this at Main.npc[200] — the difference is only which index the dummy
                // sits at. It stays inert either way: every loop stops one short of it, so it never updates,
                // draws, takes damage, or counts toward a cap. Listed rather than hidden because the slot is
                // also where a genuinely misbehaving mod would show up.
                sb.AppendLine($"--- notes ({notes.Count}) — expected, not faults ---");
                foreach (string n in notes)
                    sb.AppendLine(n + "\t(vanilla: a blocked NPC.SpawnNPC still SetDefaults() the failure slot, which activates it; inert, outside every loop)");
            }

            sb.AppendLine($"--- anomalies ({problems.Count}) ---");
            foreach (string p in problems.Take(50))
                sb.AppendLine(p);
            if (problems.Count > 50)
                sb.AppendLine($"... {problems.Count - 50} more");

            sb.AppendLine($"--- slots ({rows.Count}) ---");
            sb.AppendLine("slot\tzone\ttype\tnetID\tcat\tvia\tchain\tlife\tlifeMax\trealLife\taiStyle\tflags(b/t/c/f/i)\ttileX\ttileY\ttimeLeft\tmod\tname");
            foreach (string r in rows)
                sb.AppendLine(r);

            return sb.ToString();
        }

        private static void AppendCapLine(StringBuilder sb, string name, int count, int cap)
            => sb.AppendLine($"cap.{name}\t{count}/{cap}{(count > cap ? "\tOVER (expected only when multi-part bodies bypassed the ceiling)" : "")}");

        /// <summary>
        /// Which of <see cref="NpcCategorizer"/>'s three routes decided this NPC's category — the chain tag,
        /// the <c>realLife</c> head, or its own flags (and which flag).
        /// </summary>
        private static string CategoryRoute(NPC n)
        {
            int slot = n.whoAmI;
            if (SegmentChain.IsMember(slot))
                return "chain";

            int head = n.realLife;
            if (head >= 0 && head != slot && head < Main.npc.Length)
            {
                NPC h = Main.npc[head];
                if (h != null && h.active && h.whoAmI != slot)
                    return $"realLife:{head}";
            }

            if (NpcCategorizer.IsRescueNpc(n.type)) return "flag:rescue";
            if (n.isLikeATownNPC) return "flag:townlike";
            if (n.boss) return "flag:boss";
            if (NpcCategorizer.IsEventMiniboss(n.type)) return "flag:miniboss";
            if ((uint)n.type < (uint)NPCID.Sets.ShouldBeCountedAsBoss.Length && NPCID.Sets.ShouldBeCountedAsBoss[n.type]) return "flag:countedAsBoss";
            if (n.CountsAsACritter) return "flag:critter";
            return "flag:enemy";
        }

        private static void CheckArray(StringBuilder sb, ref int anomalies, string name, int length, int cap)
        {
            bool ok = length >= cap;
            sb.AppendLine($"{name}.Length={length} (need >= {cap}){(ok ? "" : "  !! TOO SHORT")}");
            if (!ok) anomalies++;
        }

        private static string SafeName(NPC n)
        {
            try { return n.TypeName; }
            catch { return "?"; }
        }
    }
}
