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

            int[] low = new int[4], high = new int[4];
            for (int i = 0; i < Main.npc.Length; i++)
            {
                NPC n = Main.npc[i];
                if (n == null || !n.active)
                    continue;
                int c = (int)NpcCategorizer.Categorize(n);
                if (i < 200) low[c]++; else high[c]++;
            }
            sb.AppendLine($"active 0-199  T/B/C/E = {low[0]}/{low[1]}/{low[2]}/{low[3]}  (total {low.Sum()})");
            sb.AppendLine($"active 200+   T/B/C/E = {high[0]}/{high[1]}/{high[2]}/{high[3]}  (total {high.Sum()})");
            sb.AppendLine($"active TOTAL  = {low.Sum() + high.Sum()}");

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
            sb.AppendLine($"  _globals.Length={GlobalsLength(n)}  life={n.life}/{n.lifeMax} damage={n.damage} dontTakeDamage={n.dontTakeDamage}");
            return sb.ToString();
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
