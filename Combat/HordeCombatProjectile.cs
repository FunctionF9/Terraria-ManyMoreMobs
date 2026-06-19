using Microsoft.Xna.Framework;
using Terraria;
using Terraria.DataStructures;
using Terraria.ModLoader;

namespace ManyMoreMobs
{
    /// <summary>
    /// Applies the per-class horde-combat assists from <see cref="HordeCombatConfig"/> to player weapon
    /// projectiles:
    /// <list type="bullet">
    /// <item><b>Extra pierce</b> (on spawn) — single-target guns/spells cut through a crowd instead of
    /// stopping at the first enemy. Re-hits of the same enemy are still blocked by the engine's per-NPC
    /// immunity, so the bonus spreads across DIFFERENT enemies.</item>
    /// <item><b>Anti-tunnel</b> (<see cref="Colliding"/>) — a fast projectile moves many pixels per update, so
    /// the engine's single-frame box test can miss enemies it visually passes through (Jester's Arrow only
    /// hitting the first couple in a line). We additionally register a hit when the projectile's swept path
    /// this update crosses the target. It only ever ADDS hits, so it can't change normal behaviour.</item>
    /// </list>
    /// Both gate to friendly player weapon projectiles only (no minions/sentries/traps/enemy shots).
    /// </summary>
    public class HordeCombatProjectile : GlobalProjectile
    {
        // Friendly player WEAPON projectiles only — skip enemy/trap projectiles, minions, sentries, their shots.
        internal static bool IsPlayerWeaponProjectile(Projectile projectile) =>
            projectile.friendly && !projectile.hostile && !projectile.npcProj && !projectile.trap
            && !projectile.minion && !projectile.sentry;

        public override void OnSpawn(Projectile projectile, IEntitySource source)
        {
            if (!IsPlayerWeaponProjectile(projectile))
                return;

            // Already-infinite (penetrate == -1) or invalid: nothing to add.
            if (projectile.penetrate <= 0)
                return;

            var config = ModContent.GetInstance<HordeCombatConfig>();
            if (config == null)
                return;

            DamageClass dc = projectile.DamageType;
            int bonus =
                dc.CountsAsClass(DamageClass.Melee) ? config.MeleeExtraPierce :
                dc.CountsAsClass(DamageClass.Ranged) ? config.RangedExtraPierce :
                dc.CountsAsClass(DamageClass.Magic) ? config.MagicExtraPierce : 0;

            if (bonus <= 0)
                return;

            projectile.penetrate += bonus;
            if (projectile.maxPenetrate > 0)
                projectile.maxPenetrate += bonus;
        }

        public override bool? Colliding(Projectile projectile, Rectangle projHitbox, Rectangle targetHitbox)
        {
            if (!IsPlayerWeaponProjectile(projectile))
                return null;

            var config = ModContent.GetInstance<HordeCombatConfig>();
            if (config == null || !AntiTunnelEnabledFor(projectile, config))
                return null;

            // Nothing to sweep if essentially stationary; and if the boxes already overlap, let the engine's
            // own check (and any other global) decide as normal.
            Vector2 vel = projectile.velocity;
            if (vel.LengthSquared() < 0.01f || projHitbox.Intersects(targetHitbox))
                return null;

            // Swept test: did this update's travel (last center -> current center) cross the target? Inflate the
            // target by the projectile's half-size so the projectile's own body is accounted for, then test the
            // centre segment against it.
            Vector2 currCenter = projectile.Center;
            Vector2 prevCenter = currCenter - vel;
            Vector2 size = projectile.Size;
            Vector2 aabbPos = new Vector2(targetHitbox.X, targetHitbox.Y) - size / 2f;
            Vector2 aabbDim = new Vector2(targetHitbox.Width, targetHitbox.Height) + size;

            if (Collision.CheckAABBvLineCollision(aabbPos, aabbDim, prevCenter, currCenter))
                return true;

            return null;
        }

        private static bool AntiTunnelEnabledFor(Projectile projectile, HordeCombatConfig config)
        {
            DamageClass dc = projectile.DamageType;
            if (dc.CountsAsClass(DamageClass.Melee)) return config.MeleeAntiTunnel;
            if (dc.CountsAsClass(DamageClass.Ranged)) return config.RangedAntiTunnel;
            if (dc.CountsAsClass(DamageClass.Magic)) return config.MagicAntiTunnel;
            return false;
        }
    }
}
