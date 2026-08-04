# Summon

Whips and minions are both summon class, but they fail in completely different ways — whips through damage
decay, minions through targeting. Keep them separate when testing.

## Whips

| Weapon | Proj | Status | Notes |
|---|---|---|---|
| Cool Whip | 912 | fixed (0.7.4) | Traced `33 → 23 → 16 → 11 → 7 → 4 → 2 → 1 → 0`: literal zero from the ninth enemy of a swing. Vanilla decay `×0.7` per hit. |
| Snapthorn / others | 913-915 | untested | All four whips (912-915) carry a per-type decay; 913 is `×0.66`. Should be covered by the same toggle — worth confirming. |
| Leather Whip | — | untested | User reports it feels fine post-0.7.4. |
| Kaleidoscope, Morning Star, etc. | — | untested | |

**Key fact:** `Projectile.DefaultToWhip()` sets `DamageType = DamageClass.SummonMeleeSpeed`, which does
**not** satisfy `CountsAsClass(DamageClass.Melee)`. Whips need the Summon No Pierce Falloff toggle; the
Melee one never applied to them. Never assume a whip is melee.

## Minions & sentries

| Weapon | Proj | Status | Notes |
|---|---|---|---|
| Terraprisma | 946 | clean (0.7.4) | 1141 hits, **all** on slots 200+, constant `projDmg=97`, 268 distinct enemies, 13 clean target acquisitions each held 39-79 frames. No thrashing, no decay. |
| Stardust Dragon | 626-628 | untested | Shares a head's `localNPCImmunity`; `AI_121_StardustDragon` patched. |
| Stardust Guardian | — | clean (0.5) | `AI_120_StardustGuardian` + `_FindTarget` patched. |
| Abigail | 62 | untested | `AI_062` patched. |
| Sentries (all) | — | clean (0.5) | Frost Hydra, Rainbow Crystal, Houndius Shootius, Queen Spider, Lunar Portal — verified targeting high slots. |
| Other minions | — | untested | |

Minions are excluded from the Extra Pierce / Anti-Tunnel assists by design (they already reach and
auto-target the expanded zone). They are **included** in `/debugnpc track`.

## Reference: vanilla per-type damage decay

Projectile types with a hardcoded per-hit `damage = damage * k` in `Projectile.Damage`. These are the types
that can decay to zero across a crowd:

```
4 5 85 114 132 242 265 294 309 323 355 841 847 848 849 866 912 913 914 915 931 950 952 964 985
```

912-915 are the whips; 985 is a Terra Blade projectile. Terraprisma (946) is **not** on this list, which is
why it needs no falloff protection.
