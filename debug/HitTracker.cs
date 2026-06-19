using System.Text;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ModLoader;

namespace ManyMoreMobs
{
    /// <summary>
    /// Debug-only frame-by-frame tracker for diagnosing why a fast projectile (Jester's Arrow) skips enemies it
    /// visually passes through. Toggled with <c>/debugnpc track</c>. When on, it logs — for the local player's
    /// weapon projectiles — each frame's collision picture against every nearby enemy (does the box overlap?
    /// would a swept line cross it? what is the per-player i-frame and the projectile's local immunity for that
    /// slot?), plus every actual hit. Comparing "geometry says hit" vs "hit actually landed" isolates whether
    /// the miss is a frame/hitbox issue, an immunity issue, or the collision result being ignored. Self-limits
    /// to a line budget so it can't spam the log if left on.
    /// </summary>
    public static class HitTracker
    {
        public static bool Enabled { get; private set; }
        private static int _budget;

        public static string Toggle()
        {
            Enabled = !Enabled;
            _budget = 500;
            string msg = Enabled
                ? "[HITTRACK] tracking ON — fire ONE arrow into a line of enemies, then /debugnpc track to stop."
                : "[HITTRACK] tracking OFF.";
            MmmLog.Info($"=== {msg} ===");
            return msg;
        }

        public static void Log(string line)
        {
            if (!Enabled)
                return;
            MmmLog.Info(line);
            if (--_budget <= 0)
            {
                Enabled = false;
                MmmLog.Info("[HITTRACK] auto-stopped (line budget reached).");
            }
        }
    }

    public class HitTrackerGlobalProjectile : GlobalProjectile
    {
        private static bool IsLocalPlayerWeaponProjectile(Projectile p) =>
            p.owner == Main.myPlayer && p.friendly && !p.hostile && !p.npcProj && !p.trap && !p.minion && !p.sentry;

        public override void PostAI(Projectile projectile)
        {
            if (!HitTracker.Enabled || !IsLocalPlayerWeaponProjectile(projectile))
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
            sb.Append($"[HITTRACK] f{Main.GameUpdateCount} proj#{projectile.whoAmI} t{projectile.type} ")
              .Append($"pen={projectile.penetrate} local={projectile.usesLocalNPCImmunity} id={projectile.usesIDStaticNPCImmunity} ")
              .Append($"projDmg={projectile.damage} |v|={vel.Length():F1} c=({(int)currCenter.X},{(int)currCenter.Y})");

            int near = 0;
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
                sb.Append($"\n    npc{i} d={(int)d} overlap={intersects} swept={swept} immune[own]={imm} local[slot]={loc} life={n.life}");
            }

            if (near > 0)
                HitTracker.Log(sb.ToString());
        }

        public override void OnHitNPC(Projectile projectile, NPC target, NPC.HitInfo hit, int damageDone)
        {
            if (!HitTracker.Enabled || !IsLocalPlayerWeaponProjectile(projectile))
                return;
            HitTracker.Log($"[HITTRACK] f{Main.GameUpdateCount} *** HIT proj#{projectile.whoAmI} t{projectile.type} projDmg={projectile.damage} -> npc{target.whoAmI} type{target.type} def={target.defense} dmgDone={damageDone} life={target.life}/{target.lifeMax}");
        }
    }
}
