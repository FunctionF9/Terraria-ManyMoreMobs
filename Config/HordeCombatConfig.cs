using System.ComponentModel;
using Terraria.ModLoader.Config;

namespace ManyMoreMobs
{
    /// <summary>
    /// Opt-in combat assists so weapons stay viable against the huge crowds this mod enables. Organized per
    /// damage class so each can be tuned or disabled independently. Melee / Ranged / Magic get the full set;
    /// Summon only gets the no-falloff toggle, because minions and whips already reach and auto-target the
    /// expanded zone — the one thing they DON'T escape is the per-hit damage decay.
    /// <para/>
    /// The projectile assists are applied in <see cref="HordeCombatProjectile"/>: per-class extra pierce (on
    /// spawn) and per-class anti-tunnel (a swept-collision <c>Colliding</c> override so fast projectiles register
    /// on every enemy along their path instead of skipping over them between updates). The Melee group also holds
    /// the true-melee (swung weapon) "hit all in swing" assist, applied via IL in <see cref="EngineILPatcher"/>.
    /// <para/>
    /// Most settings default to vanilla (0 / off). The exceptions are <see cref="MeleeHitsAllInSwing"/> and the
    /// three <c>NoPierceFalloff</c> toggles, which default ON because vanilla's behaviour is actively broken at
    /// this mod's scale rather than merely conservative:
    /// <list type="bullet">
    /// <item>true-melee hits ONE enemy per frame, which is unusable against a 500-enemy crowd;</item>
    /// <item>piercing projectiles lose ~30% damage per enemy pierced. Tuned for ~5 enemies that's a sensible
    /// tax; across a horde it compounds to nothing. A traced Cool Whip went 33 → 23 → 16 → 11 projectile
    /// damage over four Mimics, landing for <b>1</b> damage on the last — the hits register fine, so to a
    /// player it reads as "my weapon randomly stopped working" (exactly how it was reported). A later trace
    /// followed the same whip all the way down: 33 → 23 → 16 → 11 → 7 → 4 → 2 → 1 → <b>0</b>, i.e. from the
    /// ninth enemy of a single swing onward it dealt literal zero.</item>
    /// </list>
    /// Turning these off restores literal vanilla behaviour; they are opt-OUT rather than opt-in because the
    /// horde is the mod's premise. Anti-tunnel stays off — it costs per-frame work. Extra pierce defaults
    /// non-zero for Melee/Ranged/Magic because vanilla's per-shot enemy caps are all balanced around a handful
    /// of targets; see <see cref="MeleeExtraPierce"/> for the worst case (2-6 enemies per SWORD SWING).
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
        // Defaults to 5, matching Ranged. In 1.4.4 the big swords swing as a PROJECTILE with a hard penetrate
        // cap and stopsDealingDamageAfterPenetrateHits: Night's Edge (proj 972) 2; True Night's Edge (973),
        // Excalibur (982) and Terra Blade (984) 3; True Excalibur (983) 6. So a broadsword lands on 2-6
        // enemies per swing no matter how many are in the arc,
        // which in a 750-enemy crowd reads as the sword whiffing. Traced: two Night's Edge swing projectiles,
        // 2 hits each, 4 enemies per swing, full 47 damage on every one.
        [Range(0, 20)]
        [DefaultValue(5)]
        public int MeleeExtraPierce { get; set; } = 5;

        // Make fast MELEE projectiles hit every enemy their path crosses (no tunnelling). off = vanilla.
        [DefaultValue(false)]
        public bool MeleeAntiTunnel { get; set; } = false;

        // Stop a piercing MELEE projectile's per-hit damage falloff from compounding across a crowd.
        // Defaults ON — see the class doc for why this is a fix rather than a buff.
        [DefaultValue(true)]
        public bool MeleeNoPierceFalloff { get; set; } = true;

        // Extra enemies a RANGED projectile (bullets/arrows) will pierce. 0 = vanilla.
        [Header("Ranged")]
        [Range(0, 20)]
        [DefaultValue(5)]
        public int RangedExtraPierce { get; set; } = 5;

        // Make fast RANGED projectiles (arrows/bullets) hit every enemy their path crosses. off = vanilla.
        [DefaultValue(false)]
        public bool RangedAntiTunnel { get; set; } = false;

        // Stop a piercing RANGED projectile's per-hit damage falloff from compounding across a crowd (e.g.
        // Jester's Arrow reaching the far end of a horde for full damage). Defaults ON.
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

        // Stop a piercing MAGIC projectile's per-hit damage falloff from compounding across a crowd.
        // Defaults ON.
        [DefaultValue(true)]
        public bool MagicNoPierceFalloff { get; set; } = true;

        // Whips are DamageClass.SummonMeleeSpeed, so the Melee toggle above does NOT cover them — they need
        // their own. Vanilla gives each whip type its own hardcoded per-hit decay in Projectile.Damage (Cool
        // Whip 0.7, Snapthorn 0.66, ...), which reaches literal 0 within one swing across a crowd. Defaults ON.
        // No extra-pierce/anti-tunnel counterparts: whips already have infinite pierce and don't travel.
        [Header("Summon")]
        [DefaultValue(true)]
        public bool SummonNoPierceFalloff { get; set; } = true;
    }
}
