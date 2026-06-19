using Terraria;
using Terraria.DataStructures;
using Terraria.ModLoader;

namespace ManyMoreMobs
{
    /// <summary>
    /// Optional horde assist: stops a piercing projectile's per-hit damage falloff from <b>compounding</b>.
    /// <para/>
    /// Many piercing weapons permanently shrink their own <see cref="Projectile.damage"/> a little on every hit
    /// (e.g. Jester's Arrow loses 10% per pierce, in <c>Projectile.Damage</c>). Across the huge crowds this mod
    /// enables that compounds to ~0, so a "infinite pierce" projectile still hits the far enemies but for no
    /// damage. When the per-class toggle in <see cref="HordeCombatConfig"/> is on, we restore the projectile's
    /// damage to its spawn value after each hit, so every pierced enemy takes the weapon's normal per-hit damage
    /// instead of an ever-shrinking fraction. This matches vanilla's FIRST-hit damage and only removes the
    /// runaway compounding; it does not increase a weapon's base hit.
    /// </summary>
    public class PierceDamageProjectile : GlobalProjectile
    {
        public override bool InstancePerEntity => true;

        // Damage the projectile spawned with (-1 = not a tracked player weapon projectile).
        private int _spawnDamage = -1;

        public override void OnSpawn(Projectile projectile, IEntitySource source)
        {
            // Only piercing player weapon projectiles can compound a falloff (penetrate == 1 dies on first hit).
            if (HordeCombatProjectile.IsPlayerWeaponProjectile(projectile) && projectile.penetrate != 1)
                _spawnDamage = projectile.damage;
        }

        public override void OnHitNPC(Projectile projectile, NPC target, NPC.HitInfo hit, int damageDone)
        {
            if (_spawnDamage <= 0 || projectile.damage >= _spawnDamage)
                return;

            if (!NoFalloffEnabledFor(projectile))
                return;

            // Undo whatever the vanilla per-hit falloff just shaved off, so the NEXT pierced enemy starts fresh.
            projectile.damage = _spawnDamage;
        }

        private static bool NoFalloffEnabledFor(Projectile projectile)
        {
            var config = ModContent.GetInstance<HordeCombatConfig>();
            if (config == null)
                return false;

            DamageClass dc = projectile.DamageType;
            if (dc.CountsAsClass(DamageClass.Melee)) return config.MeleeNoPierceFalloff;
            if (dc.CountsAsClass(DamageClass.Ranged)) return config.RangedNoPierceFalloff;
            if (dc.CountsAsClass(DamageClass.Magic)) return config.MagicNoPierceFalloff;
            return false;
        }
    }
}
