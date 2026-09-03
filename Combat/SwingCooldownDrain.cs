using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace ManyMoreMobs
{
    /// <summary>
    /// Extends the <see cref="HordeCombatConfig.MeleeHitsAllInSwing"/> assist to the swing-PROJECTILE sword
    /// family, by making their per-enemy hit cooldown actually tick down.
    /// <para/>
    /// The engine splits melee into two families that are gated completely differently, and the assist only
    /// ever reached one of them:
    /// <list type="bullet">
    /// <item><b>Item-hitbox melee</b> (Breaker Blade — no projectile). Limited by the player-global
    /// <c>attackCD</c>, which is what the assist suppresses.</item>
    /// <item><b>Swing-projectile swords</b> (Night's Edge, Excalibur, True Excalibur, True Night's Edge, Terra
    /// Blade, Horseman's Blade — all <c>noMelee</c>, all spawning a projectile with
    /// <c>usesOwnerMeleeHitCD</c>). Limited per-enemy by <c>Player.meleeNPCHitCooldown</c>.
    /// <c>Projectile.Damage</c> never reads <c>attackCD</c> at all, so the assist did nothing for them.</item>
    /// </list>
    /// The gap this closes is not the arc — a swing projectile already hits every enemy it overlaps, one each.
    /// It is that <c>Player.UpdateMeleeHitCooldowns()</c>, the per-frame decrement, has exactly one call site,
    /// and the block it sits in returns early at <c>Player.cs:43327</c> for any <c>noMelee</c> item. So for
    /// this entire family the cooldown never counts down; it is only zeroed wholesale by
    /// <c>ResetMeleeHitCooldowns()</c> at the start of the next swing.
    /// <para/>
    /// That single-point-of-failure is the reason this exists. <c>meleeNPCHitCooldown</c> is WRITTEN unbounded
    /// (from <c>ProcessHitAgainstNPC</c> and from <c>Projectile.Damage</c>) but cleared only by those two
    /// 0-199 loops. Both are patched, so a stuck entry should not happen — but if that reset ever stops
    /// matching (another mod's IL edit landing first is the realistic way, and there is precedent), the first
    /// hit on an enemy above slot 199 would leave its entry pinned and that enemy would be immune to every
    /// sword in the list above for the rest of the session, while Breaker Blade kept working. That is a
    /// standing bug report, term for term. With the decrement running, the same fault drains in
    /// <c>itemAnimation</c> frames of swinging instead of persisting.
    /// <para/>
    /// So this is defence in depth, not a damage buff: the cooldown is set to the swing's remaining animation
    /// frames and decremented once per frame, so it expires roughly as the swing ends either way. It should
    /// not be described as equalising the two families' damage — it removes an asymmetry in which family's
    /// cooldown is allowed to tick, nothing more.
    /// </summary>
    public class SwingCooldownDrain : ModPlayer
    {
        public override void PostUpdate()
        {
            var config = ModContent.GetInstance<HordeCombatConfig>();
            if (config == null || !config.MeleeHitsAllInSwing)
                return;

            // Vanilla's own decrement is client-side only; match it, or a server would drift its own copy.
            if (Main.myPlayer != Player.whoAmI)
                return;

            if (Player.itemAnimation <= 0)
                return;

            Item held = Player.HeldItem;
            if (held == null || held.IsAir || held.damage <= 0 || !held.noMelee)
                return;

            // `noMelee` ALONE IS FAR TOO BROAD, and reading vanilla's guard is what misleads: its early
            // return keys on `noMelee`, so it is tempting to treat that as meaning "swing-projectile sword".
            // It does not. Counted in the decompiled Item.SetDefaults: 66 items set `noMelee = true` next to
            // `ranged = true` and 54 next to `magic = true` — an ordinary bow is `noUseGraphic = true;
            // noMelee = true; ... ranged = true`. Without this test we would walk the whole 750-entry melee
            // cooldown table every frame the player holds a bow or a staff: a behaviour divergence (a sword's
            // cooldowns would keep draining while a ranged weapon is out) bought for no benefit at all, since
            // a ranged weapon sets no melee cooldowns to begin with.
            //
            // Vanilla gets away with keying on `noMelee` because its check sits INSIDE the melee hitbox path,
            // which a ranged weapon never reaches. Ours runs in PostUpdate, so it has to say so itself.
            if (!held.CountsAsClass(DamageClass.Melee))
                return;

            // The exact carve-outs vanilla's early return lets through to its OWN decrement, so we must not
            // also decrement for them. In unmodified vanilla this return is unreachable and that is fine:
            // BubbleWand and the three catching tools set no damage at all (the damage <= 0 test above already
            // dropped them), and NebulaBlaze and SpiritFlame are magic = true (CountsAsClass above dropped
            // them). It earns its place only if a content mod reclassifies one of these four as melee, which
            // would otherwise double-decrement and halve its hit rate. Kept as a guard against that, NOT
            // because it fires in vanilla play.
            if (held.type == ItemID.BubbleWand || held.type == ItemID.NebulaBlaze
                || held.type == ItemID.SpiritFlame || ItemID.Sets.CatchingTool[held.type])
                return;

            Player.UpdateMeleeHitCooldowns();
        }
    }
}
