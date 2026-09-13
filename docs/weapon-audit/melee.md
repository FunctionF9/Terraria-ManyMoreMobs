# Melee

| Weapon | Proj | Status | Notes |
|---|---|---|---|
| Night's Edge | 972 | vanilla-limit (0.7.4) | Traced: 2 swing projectiles × 2 pierce = 4 enemies/swing, constant 47 dmg, hits landing correctly on slots 216-373. Covered by Melee Extra Pierce default 5. |
| Terra Blade | 984/985 | untested | 984 swing pierce 3; **985 has a vanilla per-hit damage decay** — check `projDmg` across a crowd. |
| True Excalibur | 983 | untested | Highest vanilla swing pierce at 6. |
| Excalibur | 982 | untested | |
| True Night's Edge | 973 | untested | aiStyle 191, no `usesOwnerMeleeHitCD` unlike its siblings. |
| Flails | — | clean (0.6) | `AI_015_Flails` patched. (`AI_015_Flails_Old` is an unreachable leftover in the engine — nothing calls it.) |
| Zenith | — | untested | `Player.GetZenithTarget` is patched; behaviour at 750 unverified. |
| Solar Eruption | — | untested | |
| Yoyos | — | untested | |
| Spears | — | untested | |
| Boomerangs | — | untested | |

## Reference: 1.4.4 projectile-swing swords

The big swords are **not true melee** — they are `noMelee` items that `shoot` a swing projectile with a hard
pierce cap and `stopsDealingDamageAfterPenetrateHits`, so once the cap is reached the rest of the swing deals
literal zero.

| Proj | Weapon | penetrate | `usesOwnerMeleeHitCD` |
|---|---|---|---|
| 972 | Night's Edge | 2 | yes |
| 973 | True Night's Edge | 3 | no |
| 982 | Excalibur | 3 | yes |
| 983 | True Excalibur | 6 | yes |
| 984 | Terra Blade | 3 | yes |
| 985 | Terra Blade (secondary) | -1 | — (has per-type damage decay) |
| 997 | — | 3 | yes |

`usesOwnerMeleeHitCD` means the projectile gates on `Player.meleeNPCHitCooldown[]`, **not** `attackCD` — so
"Melee Hits All In Swing" does nothing for these five. That setting still governs every genuinely swung
weapon (no `noMelee`), which is most of the class. The two settings are complementary, not redundant.

## Reference: true melee path

`Player.ItemCheck_MeleeHitNPCs` gates on three things — `attackCD` (routed through
`EngineState.MeleeAttackGate`), `npc.immune[player]`, and `meleeNPCHitCooldown[slot]`. All three of the
supporting methods (`ItemCheck_MeleeHitNPCs`, `UpdateMeleeHitCooldowns`, `ResetMeleeHitCooldowns`) are
patched; `meleeNPCHitCooldown` is grown to the cap both in the `Player` constructor and for existing players.

`/mmmdebug track` reports these split LOW (0-199) vs HIGH (200+). A gate that blocks only HIGH slots is the
fingerprint of an unpatched loop.
