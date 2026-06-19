using System.ComponentModel;
using Terraria.ModLoader.Config;

namespace ManyMoreMobs
{
    /// <summary>
    /// Opt-in combat assists so weapons stay viable against the huge crowds this mod enables. Organized per
    /// damage class (Melee / Ranged / Magic) so each can be tuned or disabled independently; Summon is omitted
    /// (minions/whips already auto-target the bonus zone). Everything defaults to vanilla (0 / off).
    /// <para/>
    /// The projectile assists are applied in <see cref="HordeCombatProjectile"/>: per-class extra pierce (on
    /// spawn) and per-class anti-tunnel (a swept-collision <c>Colliding</c> override so fast projectiles register
    /// on every enemy along their path instead of skipping over them between updates). The Melee group also holds
    /// the true-melee (swung weapon) "hit all in swing" assist, applied via IL in <see cref="EngineILPatcher"/>.
    /// <para/>
    /// Everything defaults to vanilla (0 / off) EXCEPT <see cref="MeleeHitsAllInSwing"/>, which defaults ON since
    /// single-target melee is barely usable against the crowds this mod enables.
    /// </summary>
    public class HordeCombatConfig : ModConfig
    {
        public override ConfigScope Mode => ConfigScope.ServerSide;

        // Let a single true-melee swing (sword/whip) hit EVERY enemy in its arc instead of just one per frame
        // (each enemy still respects its own per-NPC hit cooldown). Affects swung weapons, not projectiles.
        // Defaults ON. Applied via IL in EngineILPatcher (Player.ItemCheck attackCD gate). on = mod default.
        [Header("Melee")]
        [DefaultValue(true)]
        public bool MeleeHitsAllInSwing { get; set; } = true;

        // Extra enemies a MELEE projectile (sword beams, swung-weapon projectiles) will pierce. 0 = vanilla.
        [Range(0, 20)]
        [DefaultValue(0)]
        public int MeleeExtraPierce { get; set; } = 0;

        // Make fast MELEE projectiles hit every enemy their path crosses (no tunnelling). off = vanilla.
        [DefaultValue(false)]
        public bool MeleeAntiTunnel { get; set; } = false;

        // Stop a piercing MELEE projectile's per-hit damage falloff from compounding across a crowd. off = vanilla.
        [DefaultValue(false)]
        public bool MeleeNoPierceFalloff { get; set; } = false;

        // Extra enemies a RANGED projectile (bullets/arrows) will pierce. 0 = vanilla.
        [Header("Ranged")]
        [Range(0, 20)]
        [DefaultValue(5)]
        public int RangedExtraPierce { get; set; } = 5;

        // Make fast RANGED projectiles (arrows/bullets) hit every enemy their path crosses. off = vanilla.
        [DefaultValue(false)]
        public bool RangedAntiTunnel { get; set; } = false;

        // Stop a piercing RANGED projectile's per-hit damage falloff from compounding across a crowd (e.g.
        // Jester's Arrow reaching the far end of a horde for full damage). off = vanilla.
        [DefaultValue(true)]
        public bool RangedNoPierceFalloff { get; set; } = true;

        // Extra enemies a MAGIC projectile will pierce. 0 = vanilla.
        [Header("Magic")]
        [Range(0, 20)]
        [DefaultValue(2)]
        public int MagicExtraPierce { get; set; } = 2;

        // Make fast MAGIC projectiles hit every enemy their path crosses. off = vanilla.
        [DefaultValue(false)]
        public bool MagicAntiTunnel { get; set; } = false;

        // Stop a piercing MAGIC projectile's per-hit damage falloff from compounding across a crowd. off = vanilla.
        [DefaultValue(false)]
        public bool MagicNoPierceFalloff { get; set; } = false;
    }
}
