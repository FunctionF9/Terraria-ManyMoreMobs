using System.Collections.Generic;
using System.Text;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace ManyMoreMobs
{
    /// <summary>
    /// Debug-only frame-by-frame tracker for diagnosing why a weapon isn't connecting. Toggled with
    /// <c>/mmmdebug track</c>. When on, it logs — for every projectile the local player owns, INCLUDING minions
    /// and sentries — each frame's collision picture against nearby enemies (does the box overlap? would a swept
    /// line cross it? what is the per-player i-frame and the projectile's local immunity for that slot?), plus
    /// every actual hit; <see cref="HitTrackerMeleePlayer"/> covers swung weapons.
    /// <para/>
    /// Comparing "geometry says hit" vs "hit actually landed" isolates whether a miss is a frame/hitbox issue,
    /// an immunity issue, or the collision result being ignored.
    /// <para/>
    /// Scope is deliberately wide. Three separate investigations in a row produced an EMPTY log because this
    /// tracker's filters excluded the exact weapon under test — whips, then <c>noMelee</c> swords, then minions.
    /// Empty output reads as "nothing wrong" when it actually means "not measured", which is the worst failure
    /// mode a diagnostic can have. Prefer noise over silence here.
    /// </summary>
    public static class HitTracker
    {
        public static bool Enabled { get; private set; }

        // TWO budgets, deliberately. The per-frame geometry dump outnumbers actual hits by ~50:1, so a single
        // shared budget means one busy weapon spends the whole allowance on scenery before the weapon you
        // actually came to diagnose ever swings. Hits are the evidence; they get their own reserve and the
        // verbose dump can run dry without silencing them.
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

        public static string Toggle()
        {
            Enabled = !Enabled;
            _hitBudget = 3000;
            _dumpBudget = 1500;
            _claimedSlots.Clear();
            string msg = Enabled
                ? "[HITTRACK] tracking ON — swing/fire into a crowd, then /mmmdebug track to stop."
                : "[HITTRACK] tracking OFF.";
            MmmLog.Info($"=== {msg} ===");
            return msg;
        }

        /// <summary>Log a hit or a gate report. Exhausting this budget stops tracking entirely.</summary>
        public static void Log(string line)
        {
            if (!Enabled)
                return;
            MmmLog.Info(line);
            if (--_hitBudget <= 0)
            {
                Enabled = false;
                MmmLog.Info("[HITTRACK] auto-stopped (hit budget reached).");
                // Say so in chat, not just in a file nobody is reading mid-fight. A silent auto-stop means the
                // second half of a test session records nothing and the weapon under test looks broken when in
                // fact it was never watched — which is exactly what happened on 2026-08-21.
                if (Main.netMode != NetmodeID.Server)
                    Main.NewText("[MMM] /mmmdebug track auto-stopped (hit budget reached) — run it again to continue.");
            }
        }

        /// <summary>Log verbose per-frame geometry. Runs dry independently; hits keep recording after it does.</summary>
        public static void LogDump(string line)
        {
            if (!Enabled || _dumpBudget <= 0)
                return;
            MmmLog.Info(line);
            if (--_dumpBudget == 0)
                MmmLog.Info("[HITTRACK] per-frame geometry dump budget spent — still recording hits.");
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

        public override void PostAI(Projectile projectile)
        {
            if (!HitTracker.Enabled || !IsLocalPlayerProjectile(projectile))
                return;

            Vector2 vel = projectile.velocity;
            if (vel.LengthSquared() < 0.01f)
                return;

            Vector2 currCenter = projectile.Center;
            Vector2 prevCenter = currCenter - vel;
            Vector2 size = projectile.Size;
            var projHitbox = new Rectangle((int)projectile.position.X, (int)projectile.position.Y, projectile.width, projectile.height);
            int owner = projectile.owner;

            var sb = new StringBuilder();
            sb.Append($"[HITTRACK] f{Main.GameUpdateCount} proj#{projectile.whoAmI} t{projectile.type} {Kind(projectile)} ")
              .Append($"pen={projectile.penetrate} local={projectile.usesLocalNPCImmunity} id={projectile.usesIDStaticNPCImmunity} ")
              .Append($"cd={projectile.localNPCHitCooldown} target={(int)projectile.ai[1]} ")
              .Append($"projDmg={projectile.damage} |v|={vel.Length():F1} c=({(int)currCenter.X},{(int)currCenter.Y})");

            // Only spell out the NPCs that actually tell us something (geometry says hit, or a cooldown is
            // holding them off). In a 750-enemy crowd the old "every enemy within 160px" dump was ~45 lines a
            // frame and burned the whole log budget in two seconds — the quiet ones just get counted.
            int near = 0, interesting = 0;
            for (int i = 0; i < Main.npc.Length; i++)
            {
                NPC n = Main.npc[i];
                if (n == null || !n.active || n.friendly || n.townNPC)
                    continue;
                float d = n.Distance(currCenter);
                if (d > 160f)
                    continue;
                near++;

                Rectangle nr = n.getRect();
                bool intersects = projHitbox.Intersects(nr);
                Vector2 aabbPos = new Vector2(nr.X, nr.Y) - size / 2f;
                Vector2 aabbDim = new Vector2(nr.Width, nr.Height) + size;
                bool swept = Collision.CheckAABBvLineCollision(aabbPos, aabbDim, prevCenter, currCenter);
                int imm = (n.immune != null && owner < n.immune.Length) ? n.immune[owner] : -999;
                int loc = (projectile.localNPCImmunity != null && i < projectile.localNPCImmunity.Length) ? projectile.localNPCImmunity[i] : -999;

                if (!intersects && !swept && imm == 0 && loc == 0)
                    continue;
                interesting++;
                sb.Append($"\n    npc{i} d={(int)d} overlap={intersects} swept={swept} immune[own]={imm} local[slot]={loc} life={n.life}");
            }

            sb.Append($"\n    ({near} enemies within 160px, {near - interesting} idle/no-contact)");

            if (near > 0)
                HitTracker.LogDump(sb.ToString());
        }

        public override void OnHitNPC(Projectile projectile, NPC target, NPC.HitInfo hit, int damageDone)
        {
            if (!HitTracker.Enabled || !IsLocalPlayerProjectile(projectile))
                return;
            HitTracker.ClaimHit(target.whoAmI);
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
        public override void PostUpdate()
        {
            if (!HitTracker.Enabled || Player.whoAmI != Main.myPlayer)
                return;

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
