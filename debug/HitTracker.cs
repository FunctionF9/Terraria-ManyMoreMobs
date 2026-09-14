using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using Terraria.ModLoader;

namespace ManyMoreMobs
{
    /// <summary>
    /// Debug-only tracker for diagnosing why a weapon isn't connecting. Toggled with <c>/mmmdebug track</c>.
    /// While on, for every projectile the local player owns — INCLUDING minions and sentries — it asks, at the top
    /// of each <c>Projectile.Damage</c>, which enemies the projectile's real collision says it is touching, and for
    /// any it could not hit, which gate in the engine's own order stopped it. Every actual hit is logged too, and
    /// <see cref="HitTrackerMeleePlayer"/> covers swung weapons. Turning it off writes a per-weapon summary to the
    /// log, which is the part to read first.
    /// <para/>
    /// <b>Geometry.</b> It uses the game's own <c>Projectile.Colliding</c> (which includes our anti-tunnel test
    /// when that is on) against the hitbox <c>Damage</c> itself uses. An earlier version tested
    /// <c>projHitbox.Intersects(npc)</c> — the wrong shape for exactly the weapons that kept being reported: a whip
    /// collides along its lash (<c>WhipPointsForCollision</c>) and the projectile-swung swords as a cone
    /// (aiStyle 190). For those it saw almost nothing overlapping, and since a miss produces no hit event,
    /// "tracked it, looked fine" was the only answer it could ever give.
    /// <para/>
    /// <b>Normal versus faulty refusals.</b> A whip that has already hit an enemy keeps touching it for the rest of
    /// the swing and is correctly refused: that is one hit per swing, not a bug. So every projectile's hits are
    /// remembered for its own lifetime, and a refusal by its own table on an enemy it NEVER hit is reported as
    /// STALE — a real defect. The per-swing melee cooldown gets the same split. Only the unexpected verdicts earn
    /// a per-frame line; the normal ones are counted in the summary.
    /// <para/>
    /// Scope is deliberately wide. Three separate investigations in a row produced an EMPTY log because this
    /// tracker's filters excluded the exact weapon under test — whips, then <c>noMelee</c> swords, then minions.
    /// Empty output reads as "nothing wrong" when it actually means "not measured", which is the worst failure
    /// mode a diagnostic can have. Prefer noise over silence here.
    /// </summary>
    public static class HitTracker
    {
        public static bool Enabled { get; private set; }

        // TWO budgets, deliberately. The per-frame gate lines outnumber actual hits, so a single shared budget
        // means one busy weapon spends the whole allowance before the weapon you actually came to diagnose ever
        // swings. Hits are the evidence; they get their own reserve and the verbose lines can run dry without
        // silencing them.
        private static int _hitBudget;
        private static int _dumpBudget;

        // Which NPC slots a projectile or swing has already reported a hit on THIS frame.
        // tModLoader routes every player-caused hit through ModPlayer.OnHitNPC, projectiles and swings
        // included, so without this the contact tracker re-reports hits the specific trackers already logged.
        // Verified from a live log that the projectile hook fires first, so claiming there and checking here
        // is the right way round.
        private static ulong _claimFrame = ulong.MaxValue;
        private static readonly HashSet<int> _claimedSlots = new();

        /// <summary>A specific tracker has reported this hit; the catch-all contact hook should stay quiet.</summary>
        public static void ClaimHit(int npcSlot)
        {
            SyncClaimFrame();
            _claimedSlots.Add(npcSlot);
        }

        /// <summary>Whether a more specific tracker already reported a hit on this slot this frame.</summary>
        public static bool AlreadyClaimed(int npcSlot)
        {
            SyncClaimFrame();
            return _claimedSlots.Contains(npcSlot);
        }

        private static void SyncClaimFrame()
        {
            if (_claimFrame == Main.GameUpdateCount)
                return;
            _claimFrame = Main.GameUpdateCount;
            _claimedSlots.Clear();
        }

        // ── Gate verdicts ────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Why a touching enemy could or could not be hit, in <c>Projectile.Damage</c>'s own order.
        /// <see cref="GateLabel"/> holds what the log prints; keep the two in the same order.
        /// </summary>
        internal enum Gate
        {
            Open,
            DamageZero,
            LocalAlreadyHit,
            LocalStale,
            IdStatic,
            MeleeCooldownThisSwing,
            MeleeCooldownStale,
            DontTakeDamage,
            HookRefused,
            NotATarget,
            SharedTimer,
            SpecialCase,
            OwnerTooFar,
            NoLineOfSight,
        }

        internal static readonly string[] GateLabel =
        {
            "open", "damage-0", "already-hit", "STALE-local", "id-static", "meleeCD-this-swing", "STALE-meleeCD",
            "dontTakeDamage", "mod-hook-refused", "not-a-target", "shared-timer", "vanilla-special-case",
            "too-far", "no-line-of-sight",
        };

        /// <summary>The one-hit-per-swing rule doing its job — counted, but not worth a per-frame line.</summary>
        internal static bool IsNormal(Gate gate) => gate == Gate.LocalAlreadyHit || gate == Gate.MeleeCooldownThisSwing;

        internal sealed class TypeTally
        {
            public int Scans, Touching, Open, Hits;
            public readonly int[] Blocked = new int[GateLabel.Length];
            public readonly Dictionary<int, int> TimerSetBy = new();
        }

        private static readonly Dictionary<int, TypeTally> Tallies = new();

        internal static TypeTally TallyFor(int type)
        {
            if (!Tallies.TryGetValue(type, out TypeTally tally))
                Tallies[type] = tally = new TypeTally();
            return tally;
        }

        // Which of the player's projectile types last stamped the shared per-player timer on each NPC slot, and
        // when. This is what lets a refused whip name the weapon that locked it out.
        private static readonly Dictionary<int, (int type, uint frame)> Stamps = new();

        internal static void NoteStamp(int npcSlot, int projType) => Stamps[npcSlot] = (projType, Main.GameUpdateCount);

        internal static bool TryGetStamp(int npcSlot, out int projType, out uint age)
        {
            if (Stamps.TryGetValue(npcSlot, out (int type, uint frame) stamp))
            {
                projType = stamp.type;
                age = Main.GameUpdateCount - stamp.frame;
                return true;
            }
            projType = 0;
            age = 0;
            return false;
        }

        // Which NPC slots each projectile LIFE has hit — keyed by projectile slot, reset when a new projectile is
        // spawned into it. Only lives that began while tracking are judged; for older ones we cannot know.
        private static HashSet<int>[] _hitsThisLife = Array.Empty<HashSet<int>>();
        private static bool[] _lifeObserved = Array.Empty<bool>();

        private static void EnsureLifeArrays()
        {
            int n = Main.projectile.Length;
            if (_lifeObserved.Length == n)
                return;
            _hitsThisLife = new HashSet<int>[n];
            _lifeObserved = new bool[n];
        }

        internal static void NewLife(int projSlot)
        {
            EnsureLifeArrays();
            if (projSlot < 0 || projSlot >= _lifeObserved.Length)
                return;
            (_hitsThisLife[projSlot] ??= new HashSet<int>()).Clear();
            _lifeObserved[projSlot] = true;
        }

        internal static void NoteProjectileHit(int projSlot, int npcSlot)
        {
            EnsureLifeArrays();
            if (projSlot >= 0 && projSlot < _lifeObserved.Length && _lifeObserved[projSlot])
                _hitsThisLife[projSlot].Add(npcSlot);
        }

        /// <summary>Whether this projectile's current life has hit that NPC, or null if the life began before tracking.</summary>
        internal static bool? HitThisLife(int projSlot, int npcSlot)
        {
            EnsureLifeArrays();
            if (projSlot < 0 || projSlot >= _lifeObserved.Length || !_lifeObserved[projSlot])
                return null;
            return _hitsThisLife[projSlot].Contains(npcSlot);
        }

        // Melee half of the same split: when each NPC slot was last struck by a swing (the item itself, or a
        // projectile that uses the owner's melee cooldown), and when the current swing began.
        private static readonly Dictionary<int, uint> MeleeHitFrame = new();

        internal static uint SwingStartFrame { get; set; }

        internal static void NoteMeleeHit(int npcSlot) => MeleeHitFrame[npcSlot] = Main.GameUpdateCount;

        internal static bool MeleeHitSinceSwingStart(int npcSlot) =>
            MeleeHitFrame.TryGetValue(npcSlot, out uint frame) && frame >= SwingStartFrame;

        public static string Toggle()
        {
            // Going off: report what was measured before anything is reset.
            if (Enabled)
                WriteSummary();

            Enabled = !Enabled;
            _hitBudget = 3000;
            _dumpBudget = 1500;
            _claimedSlots.Clear();
            if (Enabled)
                ResetMeasurements();

            string msg = Enabled
                ? "[HITTRACK] tracking ON — swing/fire into a crowd, then /mmmdebug track to stop (the summary is written on stop)."
                : "[HITTRACK] tracking OFF — per-weapon summary written to ManyMoreMobs-debug.log.";
            MmmLog.Info($"=== {msg} ===");
            return msg;
        }

        // Backstop counts when tracking started, so the summary reports the resets caught during THIS trace.
        private static int _backstopProjectileBase, _backstopSwingBase;

        private static void ResetMeasurements()
        {
            Tallies.Clear();
            Stamps.Clear();
            MeleeHitFrame.Clear();
            SwingStartFrame = 0;
            EnsureLifeArrays();
            Array.Clear(_lifeObserved);
            _backstopProjectileBase = HitTableResetBackstop.ProjectileMisses;
            _backstopSwingBase = HitTableResetBackstop.SwingMisses;
        }

        /// <summary>Log a hit or a gate report. Exhausting this budget stops tracking entirely.</summary>
        public static void Log(string line)
        {
            if (!Enabled)
                return;
            MmmLog.Info(line);
            if (--_hitBudget <= 0)
            {
                WriteSummary();
                Enabled = false;
                MmmLog.Info("[HITTRACK] auto-stopped (hit budget reached).");
                // Say so in chat, not just in a file nobody is reading mid-fight. A silent auto-stop means the
                // second half of a test session records nothing and the weapon under test looks broken when in
                // fact it was never watched — which is exactly what happened on 2026-08-21.
                if (Main.netMode != NetmodeID.Server)
                    Main.NewText("[MMM] /mmmdebug track auto-stopped (hit budget reached) — run it again to continue.");
            }
        }

        /// <summary>Log verbose per-frame detail. Runs dry independently; hits keep recording after it does.</summary>
        public static void LogDump(string line)
        {
            if (!Enabled || _dumpBudget <= 0)
                return;
            MmmLog.Info(line);
            if (--_dumpBudget == 0)
                MmmLog.Info("[HITTRACK] per-frame detail budget spent — still recording hits and the summary.");
        }

        private static void WriteSummary()
        {
            var sb = new StringBuilder("[HITTRACK] === summary: what each weapon touched, and why each touch did or didn't hit ===");
            if (Tallies.Count == 0)
                sb.Append("\n  No player projectile reached a hit scan while tracking. Swung weapons report in the HITTRACK-MELEE lines.");

            foreach (KeyValuePair<int, TypeTally> entry in Tallies.OrderByDescending(e => e.Value.Touching))
            {
                TypeTally t = entry.Value;
                sb.Append($"\n  t{entry.Key} {ProjectileName(entry.Key)}: scans={t.Scans} touching={t.Touching} open={t.Open} hits={t.Hits}");
                for (int g = 1; g < GateLabel.Length; g++)
                {
                    if (t.Blocked[g] > 0)
                        sb.Append($" | {GateLabel[g]}={t.Blocked[g]}");
                }

                if (t.TimerSetBy.Count > 0)
                {
                    sb.Append("\n      shared timer set by: ")
                      .Append(string.Join(", ", t.TimerSetBy
                          .OrderByDescending(s => s.Value)
                          .Take(4)
                          .Select(s => $"t{s.Key} {ProjectileName(s.Key)} x{s.Value}")));
                }
            }

            // The backstop clears a missed reset before the weapon can be refused by it, so with it in place STALE
            // should stay at 0 even when the engine's reset misses. This line is where such a miss still shows.
            sb.Append($"\n  Engine resets that left slots 200+ behind during this trace, cleared by the mod: " +
                      $"{HitTableResetBackstop.ProjectileMisses - _backstopProjectileBase} projectile spawn(s), " +
                      $"{HitTableResetBackstop.SwingMisses - _backstopSwingBase} swing start(s).");

            sb.Append("\n  How to read it: every count is a (projectile, enemy, update) contact, so a lasting swing touches the same enemy many times - compare ratios, not totals.")
              .Append("\n  already-hit and meleeCD-this-swing are the normal one-hit-per-swing rule. STALE-anything means a reset never ran, and is a real bug.")
              .Append("\n  shared-timer means another of your weapons had just hit that enemy; the 'set by' line names it.");
            MmmLog.Info(sb.ToString());
        }

        internal static string ProjectileName(int type)
        {
            try { return Lang.GetProjectileName(type)?.Value ?? ""; }
            catch { return ""; }
        }
    }

    public class HitTrackerGlobalProjectile : GlobalProjectile
    {
        // Minions and sentries are INCLUDED. They were excluded here for the same reason they're excluded from
        // HordeCombatProjectile (they aren't "weapon projectiles"), but that reasoning belongs to the gameplay
        // assists, not to a diagnostic — it meant Terraprisma, every minion and every sentry logged literally
        // nothing while under investigation. A tracker must never silently skip the thing being tracked.
        private static bool IsLocalPlayerProjectile(Projectile p) =>
            p.owner == Main.myPlayer && p.friendly && !p.hostile && !p.npcProj && !p.trap;

        private static string Kind(Projectile p) =>
            p.minion ? "minion" : p.sentry ? "sentry" : "weapon";

        /// <summary>
        /// Types vanilla deliberately seeds with a -1 at spawn so they skip the enemy that created them
        /// (Projectile.cs:12522 and :12543 for the two whip-tag bursts, <c>CopyLocalNPCImmunityTimes</c> for
        /// Betsy's arrow shards). An entry there that the projectile never earned is by design, not stale.
        /// </summary>
        private static readonly HashSet<int> SeededAtSpawn = new()
        {
            ProjectileID.ScytheWhipProj, ProjectileID.FireWhipProj, ProjectileID.DD2BetsyArrow,
        };

        // Projectile.Damage tests against Damage_GetHitbox(), which inflates a few types and runs tModLoader's
        // ModifyDamageHitbox hook. It is private, so it is reflected once; if that ever fails we fall back to the
        // plain box and say so once, rather than quietly measuring the wrong thing.
        private static readonly Func<Projectile, Rectangle> DamageHitbox = BuildDamageHitbox();
        private static bool _saidHitboxFallback;

        private static Func<Projectile, Rectangle> BuildDamageHitbox()
        {
            try
            {
                MethodInfo m = typeof(Projectile).GetMethod("Damage_GetHitbox", BindingFlags.NonPublic | BindingFlags.Instance);
                if (m != null && m.ReturnType == typeof(Rectangle) && m.GetParameters().Length == 0)
                    return (Func<Projectile, Rectangle>)Delegate.CreateDelegate(typeof(Func<Projectile, Rectangle>), m);
            }
            catch
            {
                // Falls through to the plain-hitbox fallback, which announces itself.
            }
            return null;
        }

        private static Rectangle HitboxOf(Projectile p)
        {
            if (DamageHitbox != null)
                return DamageHitbox(p);

            if (!_saidHitboxFallback)
            {
                _saidHitboxFallback = true;
                MmmLog.Info("[HITTRACK] Projectile.Damage_GetHitbox could not be reflected; using the plain hitbox. " +
                            "The few types Damage inflates (some bombs and blasts) may read as not touching.");
            }
            return p.Hitbox;
        }

        public override void OnSpawn(Projectile projectile, IEntitySource source)
        {
            if (HitTracker.Enabled && IsLocalPlayerProjectile(projectile))
                HitTracker.NewLife(projectile.whoAmI);
        }

        /// <summary>
        /// Gate-by-gate verdict for every enemy this projectile is touching, taken at the top of
        /// <c>Projectile.Damage</c> — the same instant, position and state its NPC scan uses.
        /// <para/>
        /// <b>Must return null.</b> A value here would override the game's own decision about whether the
        /// projectile deals damage at all.
        /// </summary>
        public override bool? CanDamage(Projectile projectile)
        {
            if (HitTracker.Enabled && IsLocalPlayerProjectile(projectile))
            {
                // This runs INSIDE Projectile.Damage. An exception escaping from here would be caught by vanilla's
                // update catch, which deletes the projectile — the diagnostic would manufacture the very symptom
                // it exists to find. Nothing is allowed out.
                try { Analyse(projectile); }
                catch (Exception e) { MmmLog.Report(e, "HitTracker gate analysis"); }
            }
            return null;
        }

        private static void Analyse(Projectile p)
        {
            HitTracker.TypeTally tally = HitTracker.TallyFor(p.type);
            tally.Scans++;

            Rectangle hitbox = HitboxOf(p);
            Player owner = p.owner >= 0 && p.owner < Main.player.Length ? Main.player[p.owner] : null;

            // Walk exactly the range the real scan walks.
            int limit = Math.Min(HostileHitScan.ScanBound(p), Main.npc.Length);

            int touching = 0, open = 0, flagged = 0, shown = 0;
            var flaggedText = new StringBuilder();

            for (int i = 0; i < limit; i++)
            {
                NPC n = Main.npc[i];
                if (n == null || !n.active || n.friendly)
                    continue;

                // The scan's one per-NPC hitbox special case (Projectile.cs, just before its Colliding call).
                Rectangle target = n.getRect();
                if (n.type == NPCID.SolarCrawltipedeTail)
                    target.Inflate(8, 8);
                if (!p.Colliding(hitbox, target))
                    continue;

                touching++;
                HitTracker.Gate gate = Evaluate(p, owner, n, i, out string detail);
                if (gate == HitTracker.Gate.Open)
                {
                    open++;
                    continue;
                }

                tally.Blocked[(int)gate]++;
                if (gate == HitTracker.Gate.SharedTimer && HitTracker.TryGetStamp(i, out int stamper, out _))
                    tally.TimerSetBy[stamper] = tally.TimerSetBy.GetValueOrDefault(stamper) + 1;

                if (HitTracker.IsNormal(gate))
                    continue;

                flagged++;
                if (shown < 8)
                {
                    flaggedText.Append($" · npc{i} {HitTracker.GateLabel[(int)gate]}{(detail.Length > 0 ? " " + detail : "")}");
                    shown++;
                }
            }

            tally.Touching += touching;
            tally.Open += open;

            // Only the surprising cases get a line: something refused for a reason other than one-hit-per-swing,
            // or more hittable enemies than hits left (the scan goes by slot, so pierce decides WHICH are hit).
            bool pierceShort = p.penetrate > 0 && open > p.penetrate;
            if (flagged == 0 && !pierceShort)
                return;

            var sb = new StringBuilder();
            sb.Append($"[HITTRACK-GATE] f{Main.GameUpdateCount} proj#{p.whoAmI} t{p.type} {Kind(p)} dmg={p.damage} pen={p.penetrate} | touching={touching} open={open}");
            if (pierceShort)
                sb.Append($" (only {p.penetrate} hits left; the scan goes by slot, so the rest wait)");
            if (flagged > 0)
            {
                sb.Append(" | refused:").Append(flaggedText);
                if (flagged > shown)
                    sb.Append($" · +{flagged - shown} more");
            }
            HitTracker.LogDump(sb.ToString());
        }

        /// <summary>
        /// The first gate, in <c>Projectile.Damage</c>'s own order, that stops this projectile hitting this NPC —
        /// or <see cref="HitTracker.Gate.Open"/>. Mirrors the scan body at Projectile.cs:12089-12160; keep it in
        /// step if that changes. Array reads are bounds-checked where vanilla would throw, because an array too
        /// short for a slot is itself a finding and should be reported rather than crash the report.
        /// <para/>
        /// Not modelled: Stardust Dragon segments (626-628) share the head's immunity table in vanilla, so their
        /// local verdict here reads the segment's own table.
        /// </summary>
        private static HitTracker.Gate Evaluate(Projectile p, Player owner, NPC n, int i, out string detail)
        {
            detail = "";

            if (p.damage <= 0)
                return HitTracker.Gate.DamageZero;

            // vanilla flag5: a projectile with a table is allowed through if EITHER of its tables allows it.
            if (p.usesLocalNPCImmunity || p.usesIDStaticNPCImmunity)
            {
                string localDetail = "", idDetail = "";
                HitTracker.Gate local = p.usesLocalNPCImmunity ? LocalVerdict(p, i, out localDetail) : HitTracker.Gate.Open;
                HitTracker.Gate idStatic = p.usesIDStaticNPCImmunity ? IdStaticVerdict(p, i, out idDetail) : HitTracker.Gate.Open;
                bool allowed = (p.usesLocalNPCImmunity && local == HitTracker.Gate.Open)
                               || (p.usesIDStaticNPCImmunity && idStatic == HitTracker.Gate.Open);
                if (!allowed)
                {
                    if (p.usesLocalNPCImmunity)
                    {
                        detail = localDetail;
                        return local;
                    }
                    detail = idDetail;
                    return idStatic;
                }
            }

            if (p.usesOwnerMeleeHitCD && !p.npcProj && !p.trap && p.owner < 255 && owner != null)
            {
                int[] cd = owner.meleeNPCHitCooldown;
                if (cd == null || i >= cd.Length)
                {
                    detail = $"(cooldown array length {cd?.Length ?? 0}, slot {i})";
                    return HitTracker.Gate.MeleeCooldownStale;
                }
                if (cd[i] > 0)
                {
                    detail = $"meleeCD={cd[i]}";
                    return HitTracker.MeleeHitSinceSwingStart(i)
                        ? HitTracker.Gate.MeleeCooldownThisSwing
                        : HitTracker.Gate.MeleeCooldownStale;
                }
            }

            if ((n.dontTakeDamage && !NPCID.Sets.ZappingJellyfish[n.type]) || (n.aiStyle == NPCAIStyleID.Fairy && n.ai[2] > 1f))
                return HitTracker.Gate.DontTakeDamage;

            // Vanilla calls this for every active NPC on every scan, so asking it once more adds no new side effect.
            bool? hook = CombinedHooks.CanHitNPCWithProj(p, n);
            if (hook == false)
                return HitTracker.Gate.HookRefused;
            bool forced = hook == true; // vanilla's flag6

            // Friendly NPCs are skipped by the caller, so of vanilla's flag8 only the player-side veto can apply.
            bool playerAllows = p.owner >= 255 || owner == null || owner.CanNPCBeHitByPlayerOrPlayerProjectile(n, p);
            if (!forced && !(p.friendly && (playerAllows || NPCID.Sets.ZappingJellyfish[n.type])))
                return HitTracker.Gate.NotATarget;

            // vanilla flag10: a single-hit shot with no table of its own ignores the shared timer entirely.
            bool ignoresTimer = forced || (p.maxPenetrate == 1 && !p.usesLocalNPCImmunity && !p.usesIDStaticNPCImmunity);
            if (p.owner >= 0 && !ignoresTimer)
            {
                int[] imm = n.immune;
                int timer = imm != null && p.owner < imm.Length ? imm[p.owner] : 0;
                if (timer != 0)
                {
                    detail = HitTracker.TryGetStamp(i, out int by, out uint age)
                        ? $"immune[own]={timer} (set by t{by} {HitTracker.ProjectileName(by)}, {age}f ago)"
                        : $"immune[own]={timer} (set before tracking, or by something other than your projectiles)";
                    return HitTracker.Gate.SharedTimer;
                }
            }

            bool special = (p.type == ProjectileID.VilePowder && (n.type == NPCID.CorruptBunny || n.type == NPCID.CorruptGoldfish))
                           || (p.type == ProjectileID.SandBallFalling && n.type == NPCID.Antlion)
                           || (n.trapImmune && p.trap)
                           || (n.immortal && p.npcProj);
            if (special && !forced)
                return HitTracker.Gate.SpecialCase;

            if (!n.noTileCollide && p.ownerHitCheck)
            {
                float distance = p.Distance(n.Center);
                if (distance > p.ownerHitCheckDistance)
                {
                    detail = $"({(int)distance} > {(int)p.ownerHitCheckDistance}px)";
                    return HitTracker.Gate.OwnerTooFar;
                }
                if (!p.CanHitWithMeleeWeapon(n))
                    return HitTracker.Gate.NoLineOfSight;
            }

            return HitTracker.Gate.Open;
        }

        private static HitTracker.Gate LocalVerdict(Projectile p, int i, out string detail)
        {
            int[] table = p.localNPCImmunity;
            if (table == null || i >= table.Length)
            {
                detail = $"(table length {table?.Length ?? 0}, slot {i})";
                return HitTracker.Gate.LocalStale;
            }

            detail = "";
            if (table[i] == 0)
                return HitTracker.Gate.Open;

            detail = $"local[slot]={table[i]}";
            bool? earned = HitTracker.HitThisLife(p.whoAmI, i);
            if (earned == false && !SeededAtSpawn.Contains(p.type))
                return HitTracker.Gate.LocalStale;
            if (earned == null)
                detail += " (projectile spawned before tracking)";
            return HitTracker.Gate.LocalAlreadyHit;
        }

        private static HitTracker.Gate IdStaticVerdict(Projectile p, int i, out string detail)
        {
            uint[][] all = Projectile.perIDStaticNPCImmunity;
            int type = p.type;
            uint[] row = all != null && (uint)type < (uint)all.Length ? all[type] : null; // unsigned compare covers negatives too
            if (row == null || i >= row.Length)
            {
                detail = $"(table length {row?.Length ?? 0}, slot {i})";
                return HitTracker.Gate.IdStatic;
            }

            detail = "";
            // vanilla: hittable once the stored tick is <= GameUpdateCount (IsNPCIndexImmuneToProjectileType).
            if (row[i] <= Main.GameUpdateCount)
                return HitTracker.Gate.Open;

            detail = $"ready in {row[i] - Main.GameUpdateCount}f";
            return HitTracker.Gate.IdStatic;
        }

        public override void OnHitNPC(Projectile projectile, NPC target, NPC.HitInfo hit, int damageDone)
        {
            if (!HitTracker.Enabled || !IsLocalPlayerProjectile(projectile))
                return;

            HitTracker.ClaimHit(target.whoAmI);
            HitTracker.TallyFor(projectile.type).Hits++;
            HitTracker.NoteProjectileHit(projectile.whoAmI, target.whoAmI);
            if (projectile.usesOwnerMeleeHitCD)
                HitTracker.NoteMeleeHit(target.whoAmI);

            // The engine assigns this hit's immunity BEFORE calling OnHit (the assignment block runs ahead of
            // CombinedHooks.OnHitNPCWithProj), so the shared timer already shows this hit's effect. A projectile
            // that had to find that timer at zero to hit at all — anything but a single-hit shot with no table —
            // can only have left it non-zero itself. That is what lets a refused whip name who locked it out.
            bool ignoresTimer = projectile.maxPenetrate == 1 && !projectile.usesLocalNPCImmunity && !projectile.usesIDStaticNPCImmunity;
            int own = projectile.owner;
            if (!ignoresTimer && target.immune != null && own >= 0 && own < target.immune.Length && target.immune[own] > 0)
                HitTracker.NoteStamp(target.whoAmI, projectile.type);

            HitTracker.Log($"[HITTRACK] f{Main.GameUpdateCount} *** HIT proj#{projectile.whoAmI} t{projectile.type} {Kind(projectile)} projDmg={projectile.damage} -> npc{target.whoAmI} type{target.type} def={target.defense} dmgDone={damageDone} life={target.life}/{target.lifeMax}");
        }
    }

    /// <summary>
    /// Swung-weapon half of <see cref="HitTracker"/>. The projectile tracker above only sees things that fly,
    /// so a swing that lands nothing leaves an empty log and looks like "no data" rather than "no hits". This
    /// probe covers the swing itself: while a damaging item is mid-animation it reports, once per frame, the
    /// gates that decide whether a swing connects (<c>attackCD</c>, per-player <c>npc.immune[]</c>, per-slot
    /// <c>meleeNPCHitCooldown[]</c>), split LOW (0-199) vs HIGH (200+). A gate that only ever blocks HIGH slots
    /// is the fingerprint of an unpatched loop.
    /// <para/>
    /// It deliberately does NOT skip <c>noMelee</c> items. In 1.4.4 the big swords swing as a projectile
    /// (Night's Edge is ProjectileID 972) with <c>noMelee</c> set, and an earlier version of this probe
    /// filtered exactly those out — the one weapon under investigation produced zero lines. Those projectiles
    /// still consult <c>meleeNPCHitCooldown</c> via <c>usesOwnerMeleeHitCD</c>, so the gate counts stay
    /// meaningful; the logged <c>noMelee</c>/<c>shoot</c> fields say where the actual hits will show up.
    /// </summary>
    public class HitTrackerMeleePlayer : ModPlayer
    {
        private int _lastAnimation;

        public override void PostUpdate()
        {
            if (!HitTracker.Enabled || Player.whoAmI != Main.myPlayer)
                return;

            // A new use begins when the animation counter jumps back up. Vanilla zeroes meleeNPCHitCooldown at
            // exactly that moment (ResetMeleeHitCooldowns, from ItemCheck_StartActualUse), so anything still on
            // cooldown afterwards that no swing since then has hit is a reset that did not happen.
            if (Player.itemAnimation > _lastAnimation)
                HitTracker.SwingStartFrame = Main.GameUpdateCount;
            _lastAnimation = Player.itemAnimation;

            Item item = Player.HeldItem;
            if (item == null || item.IsAir || Player.itemAnimation <= 0 || item.damage <= 0)
                return;

            int cap = EngineState.NpcCap;
            int[] cd = Player.meleeNPCHitCooldown;
            int nearLow = 0, nearHigh = 0, freeLow = 0, freeHigh = 0, cdBlockLow = 0, cdBlockHigh = 0, immBlock = 0;

            for (int i = 0; i < Main.npc.Length; i++)
            {
                NPC n = Main.npc[i];
                if (n == null || !n.active || n.friendly || n.townNPC || n.Distance(Player.Center) > 200f)
                    continue;

                bool high = i >= 200;
                if (high) nearHigh++; else nearLow++;

                if (n.immune[Player.whoAmI] != 0)
                {
                    immBlock++;
                    continue;
                }
                // Mirrors Player.CanHitNPCWithMeleeHit(i). An out-of-range slot here would mean the array never
                // got grown, which is a crash in vanilla code — worth seeing rather than silently skipping.
                bool onCooldown = cd == null || i >= cd.Length || cd[i] > 0;
                if (onCooldown)
                {
                    if (high) cdBlockHigh++; else cdBlockLow++;
                }
                else
                {
                    if (high) freeHigh++; else freeLow++;
                }
            }

            if (nearLow + nearHigh == 0)
                return;

            HitTracker.LogDump(
                $"[HITTRACK-MELEE] f{Main.GameUpdateCount} item{item.type} noMelee={item.noMelee} shoot={item.shoot} " +
                $"anim={Player.itemAnimation} attackCD={Player.attackCD} " +
                $"gate={EngineState.MeleeAttackGate(Player)} cdArr={(cd == null ? -1 : cd.Length)}/cap{cap} | " +
                $"LOW near={nearLow} hittable={freeLow} onCD={cdBlockLow} | HIGH near={nearHigh} hittable={freeHigh} onCD={cdBlockHigh} | iframes={immBlock}");
        }

        public override void OnHitNPCWithItem(Item item, NPC target, NPC.HitInfo hit, int damageDone)
        {
            if (!HitTracker.Enabled || Player.whoAmI != Main.myPlayer)
                return;
            HitTracker.ClaimHit(target.whoAmI);
            HitTracker.NoteMeleeHit(target.whoAmI);
            HitTracker.Log($"[HITTRACK-MELEE] f{Main.GameUpdateCount} *** SWING HIT item{item.type} -> npc{target.whoAmI} type{target.type} def={target.defense} dmgDone={damageDone} life={target.life}/{target.lifeMax}");
        }

        /// <summary>
        /// Catch-all for player-caused hits, here to cover the one thing the other two trackers cannot see:
        /// damage dealt by TOUCHING an NPC — dashes (Shield of Cthulhu, Solar Flare) and the collision attacks
        /// sharing their code path, which funnel through <c>Player.ApplyDamageToNPC</c> /
        /// <c>Player.CollideWithNPCs</c> and appear in neither <c>OnHitNPCWithItem</c> nor the projectile hook.
        /// That gap mattered: <c>Player.DashMovement</c> is precisely the patch that was failing silently
        /// against Calamity, and a test session full of dashing read identically to one where every dash missed.
        /// <para/>
        /// <b>This hook is NOT contact-only.</b> tModLoader routes every player-caused hit through it, including
        /// projectile and swing hits — measured, not assumed: one session logged 2317 "contact" hits that were
        /// the same (frame, NPC) set as its 2317 projectile hits, exactly doubling the log and burning the hit
        /// budget twice as fast, which silently cut that test short. So anything a more specific tracker has
        /// already claimed this frame is skipped here.
        /// </summary>
        public override void OnHitNPC(NPC target, NPC.HitInfo hit, int damageDone)
        {
            if (!HitTracker.Enabled || Player.whoAmI != Main.myPlayer)
                return;

            // Already reported as a projectile or swing hit — logging it again would say nothing new.
            if (HitTracker.AlreadyClaimed(target.whoAmI))
                return;

            bool dashing = Player.eocDash > 0 || Player.dashDelay < 0;
            HitTracker.Log($"[HITTRACK-CONTACT] f{Main.GameUpdateCount} *** {(dashing ? "DASH" : "TOUCH")} HIT " +
                           $"eocDash={Player.eocDash} dashDelay={Player.dashDelay} dashType={Player.dashType} " +
                           $"-> npc{target.whoAmI} type{target.type} def={target.defense} dmgDone={damageDone} " +
                           $"life={target.life}/{target.lifeMax}");
        }
    }
}
