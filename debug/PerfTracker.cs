using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using Terraria;
using Terraria.ModLoader;

namespace ManyMoreMobs
{
    /// <summary>
    /// Per-tick timing and population sampling, toggled with <c>/mmmdebug perf</c>. Built to answer one
    /// question the mod currently cannot answer at all: <b>when a horde tanks the frame rate, WHERE is the
    /// time going, and is it a smooth slowdown or a stall?</b>
    /// <para/>
    /// Nothing in this mod measured runtime cost before this. Every performance claim to date has come from
    /// counting loop iterations in a decompile, which sizes a cost but cannot rank it against anything else
    /// actually running. This exists so an optimisation can be justified by a number instead of an argument.
    /// <para/>
    /// <b>What it can and cannot see.</b> The engine's per-tick work is bracketed by ModSystem hooks, so the
    /// NPC pass and the projectile pass are measured whole. Inside those, <c>GlobalNPC.PreAI/PostAI</c> and
    /// <c>GlobalProjectile.PreAI/PostAI</c> bracket only the entity's <c>AI()</c> call — NOT
    /// <c>Projectile.Damage()</c>, which the engine calls separately from <c>Projectile.Update()</c>. That gap
    /// is the point: the report prints the projectile pass total AND the summed AI time, and the difference
    /// between them is the non-AI remainder — movement, tile collision, and the <c>Damage()</c> hit scan that
    /// widens from 200 to the cap. If the remainder dominates, that is evidence for the hit scan; if AI
    /// dominates, it is not. Neither conclusion is available without this split.
    /// <para/>
    /// <b>Cost.</b> <c>Stopwatch.GetTimestamp()</c> is a few nanoseconds, and the per-entity timers add two
    /// calls per entity per tick — on the order of tens of microseconds against a 16,700-microsecond frame
    /// budget. Histogram sweeps over the projectile array are the only O(n) work and they run on an interval
    /// (<see cref="SampleInterval"/>), not every tick. When <see cref="Enabled"/> is false every hook is a
    /// single bool test.
    /// </summary>
    public static class PerfTracker
    {
        /// <summary>Slots 0-199 are the native zone (Town/Boss live here); the expanded zone starts at 200.</summary>
        private const int LowZoneSlots = 200;

        /// <summary>Ticks between the O(n) population histogram sweeps. 30 = twice a second.</summary>
        private const int SampleInterval = 30;

        /// <summary>
        /// How many of the slowest ticks to keep, with their full context.
        /// <para/>
        /// This used to be a fixed "slower than 50ms" threshold, which produced an EMPTY section on a healthy
        /// run — and an empty section reads as "measured, nothing there" when it actually means "nothing
        /// cleared an arbitrary bar". Keeping the top N instead always yields the worst ticks of whatever run
        /// you did, so the section is informative on a good run and a bad one alike.
        /// </summary>
        private const int MaxWorstTicks = 25;

        /// <summary>Width of the projectile-count bins used to test whether cost scales with projectile count.</summary>
        private const int ProjBinWidth = 100;

        /// <summary>Auto-stop after this many ticks (10 minutes at 60fps) so a forgotten session stays bounded.</summary>
        private const int TickBudget = 36000;

        public static bool Enabled { get; private set; }

        // ── Timing accumulators. Raw Stopwatch ticks; converted to ms only when the report is built. ──
        private static long _ticksSampled;
        private static long _sumTotal, _sumNpcPass, _sumProjPass, _sumNpcAi, _sumProjAi;
        private static long _maxTotal, _maxNpcPass, _maxProjPass;
        private static long _minTotal = long.MaxValue;

        // Frame-time distribution. This is the actual freeze-vs-slowdown discriminator: a smooth slowdown
        // piles up in one adjacent pair of buckets, a stall leaves a tail in the last two.
        private static readonly double[] BucketEdgesMs = { 5, 16.7, 33, 100, 500 };
        private static readonly long[] Buckets = new long[6];

        // ── Population accumulators. ──
        private static long _sumNpcLow, _sumNpcHigh, _sumProjActive, _sumProjHostile;
        private static int _maxNpcLow, _maxNpcHigh, _maxProjActive;
        // Most RECENT sample, not the max — the eviction estimate has to ask "is the array full right now",
        // and using the running max would keep reporting saturation forever after one busy moment.
        private static int _lastProjActive;
        private static long _popSamples;

        // Projectile-array pressure. The vanilla array is 1000 slots and this mod does not resize it, so
        // saturation is reached by raising the number of SHOOTERS rather than the cap.
        private static long _saturatedTicks, _nearSaturatedTicks;
        private static long _spawnsTotal, _spawnsWhileSaturated;

        // Per-aiStyle AI time. A flat array rather than a dictionary because this is written once per NPC per
        // tick and vanilla aiStyles are small ints; modded styles above the bound are folded into the last cell.
        private static readonly long[] AiStyleTicks = new long[256];
        private static readonly long[] AiStyleCalls = new long[256];

        // Live projectile-type census, refreshed on the sample interval.
        private static Dictionary<int, int> _projTypeCounts = new Dictionary<int, int>();
        private static int _maxUsesLocalImmunity;

        // The game's OWN frame rate, for cross-reference. Everything else here measures the entity update
        // only; the gap between that and the real frame time is rendering and whatever else the tick does.
        // Without this it is impossible to say whether a 10ms entity update is most of a bad frame or a
        // small part of one.
        private static long _sumFrameRate;
        private static int _minFrameRate = int.MaxValue;
        private static long _frameRateSamples;

        // Projectile pass time binned by how many projectiles were alive. This is the hypothesis under test:
        // if Damage()'s cap-wide NPC scan is the cost, the projectile pass should rise roughly linearly with
        // projectile count. Binning is how that gets shown rather than asserted, and it needs no new engine
        // hook — which matters, because the direct instrument (overriding CanHitNPC) would force that hook
        // into the dispatch chain for every player, debug mode or not.
        private static readonly long[] ProjBinTicks = new long[12];
        private static readonly long[] ProjBinTime = new long[12];

        // Slowest ticks, kept as a small ordered list rather than gated on a threshold.
        private static readonly List<(double ms, string detail)> WorstTicks = new List<(double, string)>();

        // ── Per-projectile-type phase breakdown ────────────────────────────────────────────────────────
        //
        // The whole-pass numbers proved the cost is inside Projectile.Update but OUTSIDE AI(), and that it
        // does not scale with NPC count — which ruled out Damage()'s hit scan (it sits behind a `friendly`
        // guard and every projectile in the measured scenario was hostile). That left ~85us per projectile
        // per tick unexplained, which is far too much for something that just moves and checks tiles.
        //
        // Update has no per-projectile bracket, so the phases are reconstructed from three landmarks the
        // engine already calls in a fixed order:
        //
        //     [Update head] PreAI -> AI() -> PostAI -> [movement, wet/lava, tile collision] ->
        //     Damage() -> CanDamage -> [rest of Damage, Update tail] -> next projectile's PreAI
        //
        //   AI    = PreAI      -> PostAI
        //   MID   = PostAI     -> CanDamage     (movement + tile collision — the prime suspect)
        //   TAIL  = CanDamage  -> next PreAI    (rest of Damage, Update tail, next projectile's head)
        //
        // TAIL deliberately absorbs the next projectile's Update head, because there is no hook before AI().
        // That head is a 255-entry playerImmune decrement plus the localNPCImmunity sweep, so the error is
        // small and constant per projectile — and it is attributed consistently, so comparisons between
        // types stay valid. Stated here so nobody later reads TAIL as pure Damage() cost.
        //
        // Cost of the landmarks: CanDamage is one extra hook dispatch per projectile per tick (~1000), which
        // is why it is acceptable where CanHitNPC was not — that one fires per projectile PER NPC.
        private const int PhaseNone = 0, PhaseAi = 1, PhaseMid = 2, PhaseTail = 3;
        private static int _phase;
        private static long _phaseStamp;
        private static int _phaseType = -1;

        /// <summary>Per projectile type: [0] AI, [1] movement+collision, [2] damage+tail, [3] call count.</summary>
        private static readonly Dictionary<int, long[]> TypeCost = new Dictionary<int, long[]>();

        // Scratch for the in-flight tick. Entity AI runs sequentially on the main thread, so a single static
        // start-stamp is safe and avoids a per-entity field.
        private static long _tickStart, _npcPassStart, _projPassStart, _npcAiStart, _projAiStart;
        private static long _tickNpcPass, _tickProjPass, _tickNpcAi, _tickProjAi;
        private static int _budgetLeft;

        // Arming flags, one per bracket. Toggling sampling on happens from a chat command, i.e. in the middle
        // of a tick — so the closing half of a bracket can fire without its opening half ever having run. The
        // start stamp would then be zero or stale and the tick would record as hours long, poisoning the max,
        // the distribution tail and the slow-tick list on the very first sample. A close only counts if its
        // own open armed it.
        private static bool _tickArmed, _npcPassArmed, _projPassArmed, _npcAiArmed, _projAiArmed;

        private static double Ms(long stopwatchTicks) => stopwatchTicks * 1000.0 / Stopwatch.Frequency;

        /// <summary>Start or stop sampling. Returns the message to show the player.</summary>
        public static string Toggle()
        {
            Enabled = !Enabled;
            if (Enabled)
            {
                Reset();
                _budgetLeft = TickBudget;
                return "perf sampling ON — play the scenario you want measured, then run /mmmdebug perf again to stop and write the report.";
            }

            string summary = WriteReport("/mmmdebug perf");
            return summary;
        }

        private static void Reset()
        {
            _ticksSampled = _sumTotal = _sumNpcPass = _sumProjPass = _sumNpcAi = _sumProjAi = 0;
            _maxTotal = _maxNpcPass = _maxProjPass = 0;
            _minTotal = long.MaxValue;
            Array.Clear(Buckets, 0, Buckets.Length);
            _sumNpcLow = _sumNpcHigh = _sumProjActive = _sumProjHostile = 0;
            _maxNpcLow = _maxNpcHigh = _maxProjActive = _lastProjActive = 0;
            _popSamples = 0;
            _saturatedTicks = _nearSaturatedTicks = 0;
            _spawnsTotal = _spawnsWhileSaturated = 0;
            Array.Clear(AiStyleTicks, 0, AiStyleTicks.Length);
            Array.Clear(AiStyleCalls, 0, AiStyleCalls.Length);
            _projTypeCounts = new Dictionary<int, int>();
            _maxUsesLocalImmunity = 0;
            _sumFrameRate = 0;
            _minFrameRate = int.MaxValue;
            _frameRateSamples = 0;
            Array.Clear(ProjBinTicks, 0, ProjBinTicks.Length);
            Array.Clear(ProjBinTime, 0, ProjBinTime.Length);
            TypeCost.Clear();
            _phase = PhaseNone;
            _phaseType = -1;
            _phaseStamp = 0;
            WorstTicks.Clear();
            _tickArmed = _npcPassArmed = _projPassArmed = _npcAiArmed = _projAiArmed = false;
        }

        // ── Tick brackets, driven by PerfSystem ────────────────────────────────────────────────────────

        internal static void BeginTick()
        {
            _tickStart = Stopwatch.GetTimestamp();
            _tickNpcPass = _tickProjPass = _tickNpcAi = _tickProjAi = 0;
            _npcPassArmed = _projPassArmed = _npcAiArmed = _projAiArmed = false;
            _tickArmed = true;
        }

        internal static void BeginNpcPass()
        {
            _npcPassStart = Stopwatch.GetTimestamp();
            _npcPassArmed = true;
        }

        internal static void EndNpcPass()
        {
            if (!_npcPassArmed) return;
            _npcPassArmed = false;
            _tickNpcPass = Stopwatch.GetTimestamp() - _npcPassStart;
        }

        internal static void BeginProjPass()
        {
            _projPassStart = Stopwatch.GetTimestamp();
            _projPassArmed = true;
        }

        internal static void EndProjPass()
        {
            if (!_projPassArmed) return;
            _projPassArmed = false;
            _tickProjPass = Stopwatch.GetTimestamp() - _projPassStart;
        }

        internal static void BeginNpcAi()
        {
            _npcAiStart = Stopwatch.GetTimestamp();
            _npcAiArmed = true;
        }

        internal static void EndNpcAi(int aiStyle)
        {
            if (!_npcAiArmed) return;
            _npcAiArmed = false;
            long d = Stopwatch.GetTimestamp() - _npcAiStart;
            _tickNpcAi += d;
            int slot = aiStyle >= 0 && aiStyle < AiStyleTicks.Length - 1 ? aiStyle : AiStyleTicks.Length - 1;
            AiStyleTicks[slot] += d;
            AiStyleCalls[slot]++;
        }

        internal static void BeginProjAi()
        {
            _projAiStart = Stopwatch.GetTimestamp();
            _projAiArmed = true;
        }

        internal static void EndProjAi()
        {
            if (!_projAiArmed) return;
            _projAiArmed = false;
            _tickProjAi += Stopwatch.GetTimestamp() - _projAiStart;
        }

        /// <summary>Close the phase currently in flight and bank it against the projectile type that owned it.</summary>
        private static void ClosePhase(long now)
        {
            if (_phase == PhaseNone || _phaseType < 0)
                return;

            if (!TypeCost.TryGetValue(_phaseType, out long[] slot))
            {
                slot = new long[4];
                TypeCost[_phaseType] = slot;
            }

            long d = now - _phaseStamp;
            switch (_phase)
            {
                case PhaseAi: slot[0] += d; break;
                case PhaseMid: slot[1] += d; break;
                case PhaseTail: slot[2] += d; break;
            }
            _phase = PhaseNone;
        }

        /// <summary>A projectile is entering AI(). Closes the previous projectile's trailing phase first.</summary>
        internal static void PhaseEnterAi(int type)
        {
            long now = Stopwatch.GetTimestamp();
            ClosePhase(now);

            if (!TypeCost.TryGetValue(type, out long[] slot))
            {
                slot = new long[4];
                TypeCost[type] = slot;
            }
            slot[3]++;

            _phaseType = type;
            _phaseStamp = now;
            _phase = PhaseAi;
        }

        /// <summary>AI() finished; movement and tile collision follow.</summary>
        internal static void PhaseEnterMid()
        {
            long now = Stopwatch.GetTimestamp();
            ClosePhase(now);
            _phaseStamp = now;
            _phase = PhaseMid;
        }

        /// <summary>Damage() has started. Everything after this until the next projectile is the tail.</summary>
        internal static void PhaseEnterTail()
        {
            long now = Stopwatch.GetTimestamp();
            ClosePhase(now);
            _phaseStamp = now;
            _phase = PhaseTail;
        }

        /// <summary>End of the projectile pass — bank whatever was still open so the last projectile counts.</summary>
        internal static void PhaseFlush()
        {
            ClosePhase(Stopwatch.GetTimestamp());
            _phaseType = -1;
        }

        /// <summary>A projectile was created this tick. Used to estimate evictions against a full array.</summary>
        internal static void NoteProjectileSpawn()
        {
            _spawnsTotal++;
            // Saturation is judged from the last population sample rather than a fresh scan: counting 1000
            // slots on every spawn would cost more than the thing being measured. So this is an ESTIMATE,
            // accurate to within one sample interval, and the report labels it as such.
            if (_lastProjActive >= Main.projectile.Length - 1)
                _spawnsWhileSaturated++;
        }

        internal static void EndTick()
        {
            // Sampling was switched on partway through this tick, so there is no valid start stamp. Drop the
            // partial tick rather than record a fabricated one; the next tick is measured normally.
            if (!_tickArmed) return;
            _tickArmed = false;

            long total = Stopwatch.GetTimestamp() - _tickStart;

            _ticksSampled++;
            _sumTotal += total;
            _sumNpcPass += _tickNpcPass;
            _sumProjPass += _tickProjPass;
            _sumNpcAi += _tickNpcAi;
            _sumProjAi += _tickProjAi;
            if (total > _maxTotal) _maxTotal = total;
            if (total < _minTotal) _minTotal = total;
            if (_tickNpcPass > _maxNpcPass) _maxNpcPass = _tickNpcPass;
            if (_tickProjPass > _maxProjPass) _maxProjPass = _tickProjPass;

            double totalMs = Ms(total);
            int b = 0;
            while (b < BucketEdgesMs.Length && totalMs >= BucketEdgesMs[b])
                b++;
            Buckets[b]++;

            // Light sampling runs EVERY tick — it is ~1750 array reads, which is a rounding error next to the
            // hundreds of thousands of iterations being measured, and per-tick resolution is what makes the
            // projectile-count binning below meaningful. It happens after `total` is taken, so it never
            // inflates the number it is helping to explain.
            int npcTotal = CountActiveNpcs(out int high);
            int low = npcTotal - high;
            int projActive = CountActiveProjectiles(out int hostile, out int usesLocalImmunity);

            _popSamples++;
            _sumNpcLow += low;
            _sumNpcHigh += high;
            _sumProjActive += projActive;
            _sumProjHostile += hostile;
            if (low > _maxNpcLow) _maxNpcLow = low;
            if (high > _maxNpcHigh) _maxNpcHigh = high;
            if (projActive > _maxProjActive) _maxProjActive = projActive;
            if (usesLocalImmunity > _maxUsesLocalImmunity) _maxUsesLocalImmunity = usesLocalImmunity;
            _lastProjActive = projActive;

            int slots = Main.projectile.Length - 1;
            if (projActive >= slots) _saturatedTicks++;
            else if (projActive >= slots * 9 / 10) _nearSaturatedTicks++;

            int bin = Math.Min(projActive / ProjBinWidth, ProjBinTicks.Length - 1);
            ProjBinTicks[bin]++;
            ProjBinTime[bin] += _tickProjPass;

            if (Main.frameRate > 0)
            {
                _sumFrameRate += Main.frameRate;
                _frameRateSamples++;
                if (Main.frameRate < _minFrameRate) _minFrameRate = Main.frameRate;
            }

            if (_ticksSampled % SampleInterval == 0)
                SampleProjectileTypes();

            RecordIfSlow(totalMs, npcTotal, high, projActive);

            if (--_budgetLeft <= 0)
            {
                Enabled = false;
                string summary = WriteReport("/mmmdebug perf (auto-stopped, tick budget reached)");
                // Say it in chat, not only in a file. A silent auto-stop reads as "measured and fine", which
                // is the same failure mode the hit tracker was built to avoid.
                if (Main.netMode != Terraria.ID.NetmodeID.Server)
                    Main.NewText("[MMM] /mmmdebug perf auto-stopped (10 min budget) — report written. " + summary);
            }
        }

        /// <summary>Keep the slowest ticks by time, so the section is never empty on a healthy run.</summary>
        private static void RecordIfSlow(double totalMs, int npcTotal, int high, int projActive)
        {
            if (WorstTicks.Count >= MaxWorstTicks && totalMs <= WorstTicks[WorstTicks.Count - 1].ms)
                return;

            string detail =
                $"tick {Main.GameUpdateCount}\t{totalMs:0.00}ms\tnpcPass={Ms(_tickNpcPass):0.00}\t" +
                $"projPass={Ms(_tickProjPass):0.00}\tnpcAI={Ms(_tickNpcAi):0.00}\tprojAI={Ms(_tickProjAi):0.00}\t" +
                $"npc={npcTotal}(high {high})\tproj={projActive}\tfps={Main.frameRate}";

            WorstTicks.Add((totalMs, detail));
            WorstTicks.Sort((a, b) => b.ms.CompareTo(a.ms));
            if (WorstTicks.Count > MaxWorstTicks)
                WorstTicks.RemoveAt(WorstTicks.Count - 1);
        }

        /// <summary>The O(n) half of sampling: which projectile types are actually occupying the array.</summary>
        private static void SampleProjectileTypes()
        {
            _projTypeCounts.Clear();
            for (int i = 0; i < Main.projectile.Length - 1; i++)
            {
                Projectile p = Main.projectile[i];
                if (p == null || !p.active) continue;
                _projTypeCounts.TryGetValue(p.type, out int c);
                _projTypeCounts[p.type] = c + 1;
            }
        }

        private static int CountActiveNpcs(out int high)
        {
            high = 0;
            int total = 0;
            int cap = Math.Min(EngineState.NpcCap, Main.npc.Length);
            for (int i = 0; i < cap; i++)
            {
                NPC n = Main.npc[i];
                if (n == null || !n.active) continue;
                total++;
                if (i >= LowZoneSlots) high++;
            }
            return total;
        }

        private static int CountActiveProjectiles(out int hostile, out int usesLocalImmunity)
        {
            hostile = 0;
            usesLocalImmunity = 0;
            int total = 0;
            for (int i = 0; i < Main.projectile.Length - 1; i++)
            {
                Projectile p = Main.projectile[i];
                if (p == null || !p.active) continue;
                total++;
                if (p.hostile) hostile++;
                if (p.usesLocalNPCImmunity) usesLocalImmunity++;
            }
            return total;
        }

        // ── Reporting ──────────────────────────────────────────────────────────────────────────────────

        /// <summary>Build and write the report. Returns a one-line summary for chat.</summary>
        public static string WriteReport(string title)
        {
            if (_ticksSampled == 0)
                return "perf sampling stopped — no ticks were sampled, nothing written.";

            string body = Build();
            MmmLog.Dump(title, body);

            double avg = Ms(_sumTotal) / _ticksSampled;
            // Word it from the live state, so a mid-run snapshot doesn't claim sampling has ended.
            string state = Enabled ? "perf snapshot (still running)" : "perf OFF";
            return $"{state} — {_ticksSampled} ticks, avg {avg:0.0}ms ({(avg > 0 ? 1000.0 / avg : 0):0} fps), " +
                   $"worst {Ms(_maxTotal):0.0}ms. Report in ManyMoreMobs-state.log.";
        }

        private static string Build()
        {
            var sb = new StringBuilder();
            long n = Math.Max(1, _ticksSampled);
            long ps = Math.Max(1, _popSamples);

            double avgTotal = Ms(_sumTotal) / n;
            double avgNpc = Ms(_sumNpcPass) / n;
            double avgProj = Ms(_sumProjPass) / n;
            double avgNpcAi = Ms(_sumNpcAi) / n;
            double avgProjAi = Ms(_sumProjAi) / n;

            sb.AppendLine($"ticks sampled\t{_ticksSampled}\t(budget {TickBudget}, {_budgetLeft} left)");
            sb.AppendLine($"cap={EngineState.NpcCap}\tnpcLength={Main.npc.Length}\tprojSlots={Main.projectile.Length - 1}\tnetMode={Main.netMode}");

            // Stated on every report because this is the one setting that moves the projectile numbers by an
            // order of magnitude. Comparing a before/after run without knowing which side of it you were on
            // would make the whole measurement meaningless, and nothing else in the report would reveal it.
            var perfCfg = ModContent.GetInstance<ManyMoreMobsConfig>();
            string scanState = perfCfg == null ? "?"
                             : !perfCfg.FastHostileProjectileScan ? "OFF (config)"
                             : HostileHitScan.ModHooksPresent
                                 ? (perfCfg.FastScanEvenWithOtherMods ? "ON (forced past other mods)" : "OFF (held back by other mods)")
                                 : "ON";
            sb.AppendLine($"fast enemy-projectile scan\t{scanState}");
            sb.AppendLine();

            sb.AppendLine("--- frame time (whole tick, entity update only) ---");
            sb.AppendLine($"avg\t{avgTotal:0.00} ms\t({(avgTotal > 0 ? 1000.0 / avgTotal : 0):0} fps equivalent)");
            sb.AppendLine($"min\t{Ms(_minTotal):0.00} ms");
            sb.AppendLine($"max\t{Ms(_maxTotal):0.00} ms");
            // The gap between the line above and the line below is everything this tracker does NOT measure:
            // rendering, UI, audio, tile updates. If entity update is 10ms and the real frame is 33ms, then
            // entity work is a third of the problem and optimising it alone cannot fix the frame rate.
            if (_frameRateSamples > 0)
            {
                double avgFps = (double)_sumFrameRate / _frameRateSamples;
                sb.AppendLine($"game's own fps\tavg {avgFps:0.0}\tmin {_minFrameRate}\t" +
                              $"(= {(avgFps > 0 ? 1000.0 / avgFps : 0):0.00} ms real frame vs {avgTotal:0.00} ms entity update)");
            }
            sb.AppendLine();

            // The shape of this distribution is the answer to "freeze or slowdown". Concentrated = slowdown.
            // A tail in the last two rows with a low average = stalls, which is a different bug entirely.
            sb.AppendLine("--- frame time distribution ---");
            string[] labels = { "     < 5ms", "  5 - 16.7ms", "16.7 - 33ms", "  33 - 100ms", " 100 - 500ms", "    > 500ms" };
            for (int i = 0; i < Buckets.Length; i++)
                sb.AppendLine($"{labels[i]}\t{Buckets[i]}\t{100.0 * Buckets[i] / n:0.0}%");
            sb.AppendLine();

            sb.AppendLine("--- where the time goes (avg per tick) ---");
            sb.AppendLine($"NPC pass\t{avgNpc:0.00} ms\t{Pct(avgNpc, avgTotal)}\tmax {Ms(_maxNpcPass):0.00} ms");
            sb.AppendLine($"  of which NPC AI()\t{avgNpcAi:0.00} ms\t{Pct(avgNpcAi, avgTotal)}");
            sb.AppendLine($"  NPC non-AI remainder\t{Math.Max(0, avgNpc - avgNpcAi):0.00} ms\t{Pct(Math.Max(0, avgNpc - avgNpcAi), avgTotal)}");
            sb.AppendLine($"Projectile pass\t{avgProj:0.00} ms\t{Pct(avgProj, avgTotal)}\tmax {Ms(_maxProjPass):0.00} ms");
            sb.AppendLine($"  of which projectile AI()\t{avgProjAi:0.00} ms\t{Pct(avgProjAi, avgTotal)}");
            // This remainder is the interesting one: Damage()'s NPC hit scan lives here, and that is the loop
            // the cap raise widens from 200 to the cap for every projectile, every tick.
            sb.AppendLine($"  projectile non-AI remainder\t{Math.Max(0, avgProj - avgProjAi):0.00} ms\t{Pct(Math.Max(0, avgProj - avgProjAi), avgTotal)}\t<- Damage() hit scan + movement + tile collision");
            sb.AppendLine($"everything else\t{Math.Max(0, avgTotal - avgNpc - avgProj):0.00} ms\t{Pct(Math.Max(0, avgTotal - avgNpc - avgProj), avgTotal)}");
            sb.AppendLine();

            sb.AppendLine("--- population (counted every tick; type census every " + SampleInterval + ") ---");
            sb.AppendLine($"NPCs low zone (0-{LowZoneSlots - 1})\tavg {(double)_sumNpcLow / ps:0.0}\tmax {_maxNpcLow}");
            sb.AppendLine($"NPCs high zone ({LowZoneSlots}+)\tavg {(double)_sumNpcHigh / ps:0.0}\tmax {_maxNpcHigh}");
            sb.AppendLine($"projectiles active\tavg {(double)_sumProjActive / ps:0.0}\tmax {_maxProjActive}\tof {Main.projectile.Length - 1} slots");
            sb.AppendLine($"  hostile\tavg {(double)_sumProjHostile / ps:0.0}");
            sb.AppendLine($"  usesLocalNPCImmunity\tmax {_maxUsesLocalImmunity}\t(each runs a cap-wide decrement sweep every tick)");
            sb.AppendLine();

            // Does projectile-pass cost actually scale with projectile count? If Damage()'s cap-wide NPC scan
            // is the driver, ms/projectile should stay roughly FLAT across these rows while total ms climbs.
            // A flat per-projectile figure with a rising total is the signature of "cost = projectiles x cap".
            // If ms/projectile instead climbs with the count, something super-linear is going on and the
            // simple model is wrong.
            sb.AppendLine("--- projectile pass vs projectile count ---");
            sb.AppendLine("projectiles\tticks\tavg projPass\tus per projectile");
            for (int i = 0; i < ProjBinTicks.Length; i++)
            {
                if (ProjBinTicks[i] == 0) continue;
                double binAvg = Ms(ProjBinTime[i]) / ProjBinTicks[i];
                int mid = i * ProjBinWidth + ProjBinWidth / 2;
                string label = i >= ProjBinTicks.Length - 1 ? $"{i * ProjBinWidth}+" : $"{i * ProjBinWidth}-{(i + 1) * ProjBinWidth - 1}";
                sb.AppendLine($"{label}\t{ProjBinTicks[i]}\t{binAvg:0.00} ms\t{binAvg * 1000.0 / Math.Max(1, mid):0.00} us");
            }
            sb.AppendLine();

            sb.AppendLine("--- projectile array pressure ---");
            sb.AppendLine($"samples at FULL ({Main.projectile.Length - 1})\t{_saturatedTicks}\t{100.0 * _saturatedTicks / ps:0.0}%");
            sb.AppendLine($"samples at 90%+\t{_nearSaturatedTicks}\t{100.0 * _nearSaturatedTicks / ps:0.0}%");
            sb.AppendLine($"projectiles spawned\t{_spawnsTotal}");
            // Vanilla never fails a spawn: a full array makes NewProjectile evict whatever has the least
            // timeLeft. So a nonzero figure here means projectiles were being silently deleted to make room.
            sb.AppendLine($"spawned while full (evictions)\t{_spawnsWhileSaturated}");
            sb.AppendLine();

            sb.AppendLine("--- live projectiles by type (last sample) ---");
            foreach (var kv in _projTypeCounts.OrderByDescending(k => k.Value).Take(15))
                sb.AppendLine($"{kv.Value}\ttype={kv.Key}\t{SafeProjName(kv.Key)}");
            if (_projTypeCounts.Count == 0)
                sb.AppendLine("(none active at the last sample)");
            sb.AppendLine();

            // The phase breakdown. Whichever column dominates names the culprit:
            //   AI     -> the projectile's own behaviour code
            //   MID    -> movement, wet/lava checks, tile collision
            //   TAIL   -> Damage() body and the Update tail (plus the next projectile's small head)
            sb.AppendLine("--- projectile Update cost by type and phase (total over run) ---");
            // `calls` counts AI() ENTRIES, not projectiles. A projectile with extraUpdates runs its whole
            // Update body several times per tick, so calls >> (active projectiles x ticks) is itself the
            // finding: the cost would be per-update rather than per-projectile.
            sb.AppendLine($"(for reference: {_ticksSampled} ticks x ~{(double)_sumProjActive / ps:0} active projectiles " +
                          $"= ~{_ticksSampled * (_sumProjActive / Math.Max(1, ps)):n0} expected calls if no extraUpdates)");
            sb.AppendLine("calls\ttype\tname\tAI ms\tMID ms\tTAIL ms\ttotal ms\tus/call\tdominant");
            var byCost = TypeCost
                .Where(kv => kv.Value[3] > 0)
                .OrderByDescending(kv => kv.Value[0] + kv.Value[1] + kv.Value[2])
                .Take(15);
            bool anyType = false;
            foreach (var kv in byCost)
            {
                anyType = true;
                long[] v = kv.Value;
                double ai = Ms(v[0]), mid = Ms(v[1]), tail = Ms(v[2]);
                double tot = ai + mid + tail;
                string dominant = mid >= ai && mid >= tail ? "MID (move/collide)"
                                : tail >= ai ? "TAIL (Damage+tail)"
                                : "AI";
                sb.AppendLine($"{v[3]}\t{kv.Key}\t{SafeProjName(kv.Key)}\t{ai:0.0}\t{mid:0.0}\t{tail:0.0}\t" +
                              $"{tot:0.0}\t{tot * 1000.0 / v[3]:0.00}\t{dominant}");
            }
            if (!anyType)
                sb.AppendLine("(no projectile updates recorded)");
            sb.AppendLine();

            sb.AppendLine("--- NPC AI time by aiStyle (total over run) ---");
            var styles = new List<(int style, long ticks, long calls)>();
            for (int i = 0; i < AiStyleTicks.Length; i++)
                if (AiStyleCalls[i] > 0)
                    styles.Add((i, AiStyleTicks[i], AiStyleCalls[i]));
            foreach (var s in styles.OrderByDescending(s => s.ticks).Take(15))
            {
                string name = s.style == AiStyleTicks.Length - 1 ? "(styles >= 255, folded)" : "";
                sb.AppendLine($"aiStyle {s.style}\t{Ms(s.ticks):0.0} ms total\t{s.calls} calls\t" +
                              $"{Ms(s.ticks) * 1000.0 / s.calls:0.00} us/call\t{name}");
            }
            if (styles.Count == 0)
                sb.AppendLine("(no NPC AI ran during sampling)");
            sb.AppendLine();

            sb.AppendLine($"--- slowest {MaxWorstTicks} ticks of this run ---");
            if (WorstTicks.Count == 0)
                sb.AppendLine("(no ticks recorded)");
            else
                foreach (var w in WorstTicks)
                    sb.AppendLine(w.detail);

            return sb.ToString();
        }

        private static string Pct(double part, double whole)
            => whole <= 0 ? "-" : $"{100.0 * part / whole:0.0}%";

        private static string SafeProjName(int type)
        {
            try { return Lang.GetProjectileName(type)?.Value ?? ""; }
            catch { return ""; }
        }
    }

    /// <summary>
    /// Drives <see cref="PerfTracker"/>'s tick brackets. These ModSystem hooks wrap the engine's own entity
    /// passes, so the NPC and projectile totals include everything the engine does in them — not just the
    /// parts this mod hooks.
    /// </summary>
    public class PerfSystem : ModSystem
    {
        public override void PreUpdateEntities()
        {
            if (PerfTracker.Enabled) PerfTracker.BeginTick();
        }

        public override void PreUpdateNPCs()
        {
            if (PerfTracker.Enabled) PerfTracker.BeginNpcPass();
        }

        public override void PostUpdateNPCs()
        {
            if (PerfTracker.Enabled) PerfTracker.EndNpcPass();
        }

        public override void PreUpdateProjectiles()
        {
            if (PerfTracker.Enabled) PerfTracker.BeginProjPass();
        }

        public override void PostUpdateProjectiles()
        {
            if (!PerfTracker.Enabled) return;
            // Flush before closing the pass: the last projectile's tail is still open, and without this it
            // would either be dropped or spill into the next tick's first projectile.
            PerfTracker.PhaseFlush();
            PerfTracker.EndProjPass();
        }

        public override void PostUpdateEverything()
        {
            if (PerfTracker.Enabled) PerfTracker.EndTick();
        }
    }

    /// <summary>Times each NPC's own AI() call so cost can be attributed to an aiStyle.</summary>
    public class PerfNpcTimer : GlobalNPC
    {
        public override bool PreAI(NPC npc)
        {
            if (PerfTracker.Enabled) PerfTracker.BeginNpcAi();
            return true;
        }

        public override void PostAI(NPC npc)
        {
            if (PerfTracker.Enabled) PerfTracker.EndNpcAi(npc.aiStyle);
        }
    }

    /// <summary>
    /// Times each projectile's AI() call, and counts spawns. The AI time is subtracted from the whole
    /// projectile pass in the report to expose the non-AI remainder, which is where <c>Damage()</c>'s
    /// cap-wide NPC hit scan lives.
    /// </summary>
    public class PerfProjTimer : GlobalProjectile
    {
        public override bool PreAI(Projectile projectile)
        {
            if (PerfTracker.Enabled)
            {
                PerfTracker.BeginProjAi();
                PerfTracker.PhaseEnterAi(projectile.type);
            }
            return true;
        }

        public override void PostAI(Projectile projectile)
        {
            if (PerfTracker.Enabled)
            {
                PerfTracker.EndProjAi();
                PerfTracker.PhaseEnterMid();
            }
        }

        /// <summary>
        /// Timing landmark only — marks the start of <c>Damage()</c>, which has no hook of its own.
        /// <para/>
        /// <b>Must return null.</b> A non-null result here OVERRIDES vanilla's own decision about whether the
        /// projectile can deal damage, for every projectile in the game. This override exists purely to read
        /// the clock; returning anything else would silently change combat.
        /// </summary>
        public override bool? CanDamage(Projectile projectile)
        {
            if (PerfTracker.Enabled) PerfTracker.PhaseEnterTail();
            return null;
        }

        public override void OnSpawn(Projectile projectile, Terraria.DataStructures.IEntitySource source)
        {
            if (PerfTracker.Enabled) PerfTracker.NoteProjectileSpawn();
        }
    }
}
