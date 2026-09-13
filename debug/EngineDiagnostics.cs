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

        /// <summary>One line naming any mod whose NPC arrays are still vanilla-sized. Must never throw: this
        /// runs inside the world-load dump, and reflecting over every loaded mod is exactly the kind of thing
        /// one awkward assembly can spoil.</summary>
        private static void AppendModArraySummary(StringBuilder sb)
        {
            try
            {
                ModArrayScanner.Build(out int stale, out int checkedCount, out string staleMods);
                sb.AppendLine(stale == 0
                    ? $"mod NPC arrays: {checkedCount} checked, 0 stale."
                    : $"mod NPC arrays: {checkedCount} checked, !! {stale} STALE (still sized 200) in: {staleMods}"
                      + "  — run /mmmdebug modarrays for the field names");
            }
            catch (Exception e)
            {
                sb.AppendLine($"mod NPC arrays: scan failed ({e.GetType().Name})");
            }
        }

        /// <summary>Cap/array state plus a per-category histogram split into vanilla (0-199) and bonus (200+) zones.</summary>
        public static string BuildStateReport()
        {
            var sb = new StringBuilder();
            var config = ModContent.GetInstance<ManyMoreMobsConfig>();

            sb.AppendLine($"AppliedCap={MaxNpcCapRaise.AppliedCap}  Main.maxNPCs(direct)={Main.maxNPCs} (reflection)={ReflectionMaxNPCs()}  Main.npc.Length={Main.npc.Length}");
            // Whether other mods got to size their own NPC arrays against the raised cap. When this says "no",
            // every content mod in the list built itself for 200 slots and any crash above slot 199 starts here.
            sb.AppendLine($"early raise (other mods see the real cap): {(EarlyCapRaise.Applied ? "yes" : "no")}");
            // Cheap summary of the same scan /mmmdebug modarrays prints in full. Worth having unprompted: a mod
            // array stuck at 200 does not crash, it silently ignores every NPC above slot 199 — so nobody knows
            // to go looking. The one line that would have named the culprit is the one that has to appear on
            // its own. It also catches the residual case the early raise cannot: a mod that loaded a
            // vanilla-sized array back out of an old world's saved data.
            AppendModArraySummary(sb);
            if (config != null)
            {
                var caps = config.GetEffectiveCaps();
                sb.AppendLine($"config: mode={config.CapMode} target={config.MaxNPCTotal} effectiveTotal={config.EffectiveTotal}");
                sb.AppendLine($"effective caps  T/B/C/E = {caps.town}/{caps.boss}/{caps.critter}/{caps.enemy}");
            }

            // Three buckets, not two. Main.npc is CAP+1 long — the extra entry is the dummy/failure slot that
            // NewNPC returns when it can't place anything, and it sits ABOVE the cap, outside every widened
            // loop. Counting it in with the rest is how this report used to disagree with /mmm counts
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
                            + "  — expected: vanilla's blocked-spawn paths SetDefaults() the failure slot, which activates it. Inert (outside every loop). /mmmdebug dumpall names it.");

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

            // Spawn pressure for the local player (QoL — full breakdown via /mmm spawninfo).
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
         /// <summary>
        /// <c>/mmm info</c> — the one command to ask a non-technical reporter to run.
        /// <para/>
        /// Every other <c>/mmmdebug</c> command answers one subsystem in depth, which is only useful once you
        /// already know which subsystem to suspect. This runs the cheap health check for all of them and prints
        /// OK/WARN per area, so a pasted screenshot says WHICH specialised command to ask for next. Four lines
        /// when nothing is wrong; a pointer line per warning when something is.
        /// <para/>
        /// Every check here must be side-effect free and safe to run at any moment, since it goes to people who
        /// have no idea what it does. Note the one real cost: the mod-array scan reflects over other mods' static
        /// fields and can force a class constructor to run — see <see cref="ModArrayScanner"/>. That is why it
        /// stays on manually-typed debug commands and is never wired into an automatic path.
        /// </summary>
        public static string BuildInfoReport(Player p)
        {
            var sb = new StringBuilder();
            var warnings = new List<string>();

            void Check(bool ok, string label, string detail, string next)
            {
                if (!ok) warnings.Add($"{label}: {detail}  ->  {next}");
            }

            string version = ModContent.GetInstance<ManyMoreMobs>()?.Version?.ToString() ?? "?";
            var config = ModContent.GetInstance<ManyMoreMobsConfig>();
            int cap = EngineState.NpcCap;
            int activeNpcs = 0;
            for (int i = 0; i < cap && i < Main.npc.Length; i++)
                if (Main.npc[i] != null && Main.npc[i].active) activeNpcs++;

            sb.AppendLine($"[MMM] Many More Mobs {version} | cap {cap} {config?.CapMode.ToString() ?? "?"} | " +
                          $"early raise {(EarlyCapRaise.Applied ? "yes" : "NO")} | {activeNpcs} NPCs active");

            // A config mismatch between this client and the server mis-sizes OTHER mods' NPC arrays, and the
            // resulting crash or dead spawn loop names them, never us. It is the single least diagnosable
            // failure this mod can produce, so it goes first and it goes in plain language — a player reading
            // this line should be able to fix it without understanding any of the rest.
            Check(MaxNpcCapRaise.CapMismatch == null, "Cap config", MaxNpcCapRaise.CapMismatch ?? "",
                  "your cap settings differ from the server's; set Max NPC Total and Cap Mode to match it and rejoin");

            // If registration aborted, the patch counts below are describing a PARTIAL engine and every other
            // line in this report is measuring something half-built. Say so before any of them are read.
            Check(EngineILPatcher.RegistrationAborted == null, "Patch registration",
                  EngineILPatcher.RegistrationAborted ?? "",
                  "patch registration stopped partway, so the engine is only partly widened - please report this");

            // Caps are reported as a FACT, never as a warning.
            //
            // Being at the Enemy ceiling is this mod's normal steady state — at the default 200x rate the cap
            // fills within minutes of loading a world, and refusals climb into the thousands. An earlier
            // version warned on it and the readout looked broken every time. Worse, the "is this suspicious?"
            // test it used compared CategoryCounts (world-wide) against nearbyActiveNPCs (only what is near
            // YOU), which are not comparable at all: standing in a town while the cap is full elsewhere tripped
            // it. Whether a full ceiling is a problem depends on where the player is and what they expected to
            // see, which no automatic test here can know — so print the numbers and let a human read them.
            // /mmm blocked exists for exactly that question.
            if (config != null)
            {
                var caps = config.GetEffectiveCaps();
                var counts = CategoryCounts.Snapshot();
                sb.AppendLine($"caps T/B/C/E: {counts.town}({caps.town}) {counts.boss}({caps.boss}) " +
                              $"{counts.critter}({caps.critter}) {counts.enemy}({caps.enemy})   " +
                              "(at cap is normal — that is the cap doing its job)");
            }

            // Also a fact, not a warning. "Held back" is the correct, deliberate state when another mod wants
            // the full scan — but it is the first thing to look at in any "why is it still slow?" report, so it
            // has to be visible here rather than only in the load log.
            if (config != null)
            {
                string scan = !config.FastHostileProjectileScan ? "off (config)"
                            : HostileHitScan.ModHooksPresent
                                ? (config.FastScanEvenWithOtherMods
                                    ? $"ON, forced past {HostileHitScan.ConflictingMods.Count} mod(s): {string.Join(", ", HostileHitScan.ConflictingMods)}"
                                    : $"held back by {string.Join(", ", HostileHitScan.ConflictingMods)}")
                                : "ON";
                sb.AppendLine($"fast enemy-projectile scan: {scan}");
            }


            // Biome-specific multipliers, printed ONLY when switched on — and warned about in one precise case.
            // They REPLACE the general multipliers rather than stacking with them, and every biome starts at
            // 1x. So turning the option on and tuning a few biomes silently drops every biome you did not touch
            // to vanilla rates, which reads exactly like "no enemies spawn HERE but they're fine elsewhere".
            // Narrow on purpose: only fires when this biome is still at 1x while the general dial is well above
            // it, which is the trap and nothing else.
            var biomeCfg = ModContent.GetInstance<BiomeSpawnConfig>();
            if (biomeCfg?.UseBiomeSpecificModifiers == true && config != null)
            {
                BiomeSpawnRates here = biomeCfg.ResolveFor(p);
                string name = biomeCfg.ActiveBiomeName(p) ?? "?";
                sb.AppendLine($"biome modifiers: ON | here = {name} rate x{here?.SpawnRateMultiplier ?? 1f:0.##} " +
                              $"max x{here?.MaxSpawnMultiplier ?? 1f:0.##} (these REPLACE the general dials)");
                bool untuned = here != null && here.SpawnRateMultiplier <= 1f
                               && config.SpawnRateMultiplier > 2f;
                Check(!untuned, "biome modifiers",
                      $"on, but {name} is still at 1x while the general dial is x{config.SpawnRateMultiplier:0.##} — this biome is running at vanilla rates",
                      "/mmm spawninfo, and the Biome config page");
            }
            // Engine patches. A failure here means another mod rewrote the same method first and our anchor
            // stopped matching — the root cause behind most "works alone, breaks with mod X" reports.
            int failed = EngineILPatcher.PatchesFailed + EngineILPatcher.PatchesMissing;
            Check(failed == 0, "patches", $"{failed} engine patch(es) did not apply", "client.log, search [MMM]");

            // Arrays + immunity, from the same validation pass, reported separately because they fail for
            // different reasons: a short NPC array is a cap-raise problem, a short immunity array is a
            // weapons-can't-hit problem.
            BuildValidationReport(out _, out int arrayBad, out int immuneBad);
            Check(arrayBad == 0, "arrays", $"{arrayBad} NPC-array anomaly(s)", "/mmmdebug validate");
            Check(immuneBad == 0, "immunity", $"{immuneBad} hit-immunity array(s) too short", "/mmmdebug immune");

            // Other mods' arrays still sized for 200 — the content-mod compatibility check.
            int stale = -1, checkedCount = 0;
            try { ModArrayScanner.Build(out stale, out checkedCount, out _); } catch { stale = -1; }
            Check(stale <= 0, "mod arrays", $"{stale} other-mod array(s) still sized 200", "/mmmdebug modarrays");

            // Gate telemetry. Refusals are normal at a ceiling; segment evictions never are.
            long refused = SpawnGateTelemetry.TotalRefusals;
            Check(SpawnGateTelemetry.EvictionsOfChainMembers == 0, "evictions",
                  $"{SpawnGateTelemetry.EvictionsOfChainMembers} worm/boss segment(s) removed to make room, which unravels the body",
                  "/mmm blocked");

            sb.AppendLine($"patches {EngineILPatcher.PatchesApplied} ok/{failed} bad | arrays {Word(arrayBad)} | " +
                          $"immunity {Word(immuneBad)} | mod arrays {(stale < 0 ? "?" : Word(stale))} ({checkedCount} checked) | " +
                          $"caps enforced {refused}x, {SpawnGateTelemetry.Evictions} removed to fit");

            if (warnings.Count == 0)
                sb.AppendLine("=> nothing suspicious. If something is still wrong, say what you SEE and we'll pick a command.");
            else
                foreach (string w in warnings)
                    sb.AppendLine("!! " + w);

            return sb.ToString();
        }

        /// <summary>"OK" or the count, so a healthy row reads as words rather than a line of zeroes.</summary>
        private static string Word(int bad) => bad == 0 ? "OK" : bad.ToString();

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

        /// <summary>
        /// <c>/mmm blocked</c> — the "why is nothing spawning?" readout: every gate that can stop a spawn,
        /// with the live numbers and a one-line verdict naming whichever one is actually closed.
        /// <para/>
        /// Deliberately its OWN command and kept to seven lines. It started life appended to
        /// <c>spawninfo</c>, which was a mistake: Terraria's chat has no scrollback, so the extra lines pushed
        /// everything spawninfo had already said off the top of the screen. Anything added here has to earn its
        /// row against that limit.
        /// <para/>
        /// The multipliers above this only say what the dials are SET to. They are the first thing anyone reads
        /// when enemies stop appearing, and they are almost never the answer — a spawn can be refused by
        /// vanilla's density budget, by one of our four category ceilings, or by a full array, and until now
        /// none of those were visible from in-game or from a log. That is why "no enemies spawn" reports have
        /// been undiagnosable: the mod refuses spawns silently and by design.
        /// <para/>
        /// Note the deliberate mismatch on the first two lines. Vanilla's density budget sums npcSlots, and
        /// worm body segments declare npcSlots = 0f, so a whole worm counts as just its head there while
        /// occupying twenty array slots against our Enemy ceiling. Seeing both numbers side by side is the
        /// point: vanilla reading nearly empty while Enemy sits at its cap IS that disagreement, on screen.
        /// </summary>
        public static string BuildBlockedReport(Player p)
        {
            var sb = new StringBuilder();

            int lastMax = SpawnRateMultiplier.OutMax;
            bool densityClosed = SpawnRateMultiplier.CapturedTick >= 0 && p.nearbyActiveNPCs >= lastMax;
            sb.AppendLine($"[MMM] vanilla density {p.nearbyActiveNPCs:0.#}/{lastMax} {(densityClosed ? "SATURATED" : "open")}" +
                          $"  |  npcSlots-based, so worm segments count 0");

            int cap = EngineState.NpcCap;
            int used = 0;
            for (int i = 0; i < cap && i < Main.npc.Length; i++)
                if (Main.npc[i] != null && Main.npc[i].active) used++;
            sb.AppendLine($"array {used}/{cap} used, {cap - used} free{(used >= cap ? "  FULL" : "")}");

            var config = ModContent.GetInstance<ManyMoreMobsConfig>();
            if (config == null)
            {
                sb.AppendLine("ceilings: config unavailable.");
                return sb.ToString();
            }

            var caps = config.GetEffectiveCaps();
            var counts = CategoryCounts.Snapshot();
            bool bossEnforced = config.CapMode == NpcCapMode.Expanded;
            // One line for all four: chat has no scrollback, so a full-width row beats four short ones.
            string Cell(string name, int have, int limit, bool enforced)
                => $"{name} {have}/{limit}{(!enforced ? "(off)" : have >= limit ? " CAP!" : "")}";
            sb.AppendLine($"ceilings ({config.CapMode}): " + string.Join("  ", new[]
            {
                Cell("Town", counts.town, caps.town, true),
                Cell("Boss", counts.boss, caps.boss, bossEnforced),
                Cell("Critter", counts.critter, caps.critter, true),
                Cell("Enemy", counts.enemy, caps.enemy, true),
            }));

            sb.AppendLine($"refused {SpawnGateTelemetry.TotalRefusals} (T{SpawnGateTelemetry.RefusedTown} " +
                          $"B{SpawnGateTelemetry.RefusedBoss} C{SpawnGateTelemetry.RefusedCritter} " +
                          $"E{SpawnGateTelemetry.RefusedEnemy} type{SpawnGateTelemetry.RefusedPerTypeCap}) " +
                          $"last {SpawnGateTelemetry.Ago(SpawnGateTelemetry.LastRefusalTick)}");

            string evicted = SpawnGateTelemetry.LastEvictedType >= 0
                ? $", last type {SpawnGateTelemetry.LastEvictedType} {SpawnGateTelemetry.Ago(SpawnGateTelemetry.LastEvictionTick)}"
                : "";
            sb.AppendLine($"evicted {SpawnGateTelemetry.Evictions} ({SpawnGateTelemetry.EvictionsOfChainMembers} segments — " +
                          $"each unravels a whole body), {SpawnGateTelemetry.EvictionsFailed} found nothing{evicted}");

            // Verdict, most-specific first. Enemy is checked before the others because it is the ceiling that
            // ordinary spawns actually hit; a full array is reported last because it only bites guaranteed ones.
            string verdict;
            if (counts.enemy >= caps.enemy)
                verdict = $"Enemy is at its ceiling ({counts.enemy}/{caps.enemy}) — ordinary enemy spawns are being refused. " +
                          (p.nearbyActiveNPCs < lastMax * 0.5f
                              ? "Vanilla still reads this area as uncrowded, so something is holding slots without costing npcSlots (worm segments do exactly that)."
                              : "");
            else if (densityClosed)
                verdict = "vanilla's own density budget is full — raise MaxSpawnMultiplier, or this is simply a crowded area.";
            else if (counts.critter >= caps.critter)
                verdict = $"Critter is at its ceiling ({counts.critter}/{caps.critter}) — critters are refused, enemies are not.";
            else if (used >= cap)
                verdict = "the array is full — every further guaranteed spawn evicts something.";
            else
                verdict = "nothing is blocking spawns right now.";
            sb.AppendLine($"verdict: {verdict}");
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
            => BuildValidationReport(out anomalies, out _, out _);

        /// <summary>
        /// As <see cref="BuildValidationReport(out int)"/>, but splitting the total into the two groups the
        /// triage readout reports separately: NPC-slot arrays, and the hit-immunity arrays. They fail for
        /// different reasons and point at different fixes, so a single number hides which one is wrong.
        /// </summary>
        public static string BuildValidationReport(out int anomalies, out int arrayAnomalies, out int immunityAnomalies)
        {
            arrayAnomalies = 0;
            immunityAnomalies = 0;
            var sb = new StringBuilder();
            int cap = MaxNpcCapRaise.AppliedCap;
            int len = Main.npc.Length;

            sb.AppendLine($"AppliedCap={cap}  Main.npc.Length={len} (expected {cap + 1})");
            if (len < cap + 1)
            {
                sb.AppendLine("  !! Main.npc is shorter than AppliedCap+1");
                arrayAnomalies++;
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
                    arrayAnomalies++;
                    if (offenders.Count < 20)
                        offenders.Add($"slot {i}: type {n.type} '{SafeName(n)}' _globals.Length={g} (expected {expected})");
                }
            }
            sb.AppendLine($"active NPCs={active}  _globals lengths: {(dist.Count == 0 ? "(none)" : string.Join(", ", dist.OrderBy(k => k.Key).Select(kv => $"len{kv.Key}x{kv.Value}")))}  expected={expected}");
            foreach (string o in offenders)
                sb.AppendLine("  !! " + o);

            // Companion arrays indexed by NPC slot.
            CheckArray(sb, ref arrayAnomalies, "NPC.lazyNPCOwnedProjectileSearchArray", NPC.lazyNPCOwnedProjectileSearchArray?.Length ?? -1, cap);

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
                if (bad > 0) immunityAnomalies += bad;
            }

            int projBad = 0;
            for (int i = 0; i < Main.projectile.Length; i++)
            {
                int[] arr = Main.projectile[i]?.localNPCImmunity;
                if (arr != null && arr.Length < cap) projBad++;
            }
            sb.AppendLine($"projectiles localNPCImmunity < cap: {projBad}/{Main.projectile.Length}");
            if (projBad > 0) immunityAnomalies += projBad;

            int plBad = 0;
            for (int i = 0; i < Main.player.Length; i++)
            {
                int[] arr = Main.player[i]?.meleeNPCHitCooldown;
                if (arr != null && arr.Length < cap) plBad++;
            }
            sb.AppendLine($"players meleeNPCHitCooldown < cap: {plBad}");
            if (plBad > 0) immunityAnomalies += plBad;

            anomalies = arrayAnomalies + immunityAnomalies;
            sb.AppendLine($"TOTAL anomalies: {anomalies} (arrays {arrayAnomalies}, immunity {immunityAnomalies})");
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
        /// that visible in one line instead of one <c>/mmmdebug slot</c> call per segment.
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
