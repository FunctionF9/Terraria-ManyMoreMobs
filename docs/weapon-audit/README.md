# Weapon audit

A slow, ongoing pass over every weapon class, checking that weapons actually *work* at a raised NPC cap
before worrying about whether they feel good. One file per damage class.

Not a sprint. Fill in rows as weapons get tested; an empty row means "not looked at yet", never "fine".

## Why this exists

Three separate weapon bugs reached players in 0.7.1, and all three looked identical from the outside
("my weapon randomly stopped working") while having completely different causes: a summon-class gap in a
config check, a vanilla per-swing pierce cap, and a per-type damage decay. Guessing was expensive. Checking
weapons one at a time, with the tracker on, is slower but it actually converges.

## Method

1. `/mmmdebug track`, then use the weapon in a crowd. **One weapon per trace** where you can — the
   per-frame gate lines share one budget. The exception is the shared-timer check below, which needs a
   second weapon on purpose.
2. `/mmmdebug immune` while still tracking (toggling track off first loses the context).
3. Toggle tracking off. That writes a **per-weapon summary** to `ManyMoreMobs-debug.log` — read it first,
   then the trace, against the failure shapes below.
4. Record the result, even when it's clean. "Verified clean" is a result.

The tracker tests each projectile with the game's own `Projectile.Colliding` against the hitbox
`Projectile.Damage` uses, at the moment `Damage` runs. For every enemy the weapon touched but could not
hit, it names the first gate in the engine's own order that stopped it. Before 0.7.8.2 it tested a plain
box overlap instead — the wrong shape for whips (they collide along the lash) and the projectile-swung
swords (a cone), so for exactly those weapons it could not see a miss at all.

## What to look for

| Shape | Symptom in the log | Meaning |
|---|---|---|
| Damage decay | `projDmg=` shrinking hit to hit | Per-type falloff — needs a No Pierce Falloff toggle for that class |
| Pierce cap | Hits stop after N enemies, `projDmg` unchanged | Vanilla `penetrate` limit — Extra Pierce covers it |
| Slot blindness | Hits only ever land on slots < 200 | An unpatched `0-199` loop — run `tools/audit-npc-loops.sh` |
| Decayed to nothing | `projDmg=0` and then **no hits at all** | Not a separate bug — the end state of decay. See below. |
| Shared timer | `shared-timer` on a whip or sword, with `set by: tN` | Another of your weapons had just hit that enemy. Every weapon a player owns shares one per-enemy "just hit" timer, and whips and projectile-swung swords must wait for it. Piercing shots set it for 10 frames, and Extra Pierce makes single-hit shots pierce, so with it on a gun or spell can hold a whip off an enemy it keeps hitting. |
| Stale table | `STALE-local` or `STALE-meleeCD` | A per-enemy table refused a hit that this projectile or swing never landed — a reset did not run. A real bug. Since 0.7.8.2 the mod also clears slots 200+ itself at spawn and swing start, and the summary and `/mmm info` count each time it had to. A STALE line with that in place means the clear missed it too — report it with the log. |
| Owner check | `no-line-of-sight` or `too-far` | Whips and swing swords only hit what you can see. Vanilla behaviour; enemies behind terrain are out of reach. |
| Deleted mid-update | `/mmm info` shows **Projectile errors** | The projectile's update threw and the game swapped it for a blank one. Nothing appears in the tracker, because nothing was hit. `client.log`'s "Silently Caught Exception" names the mod. |

`already-hit` and `meleeCD-this-swing` are the normal one-hit-per-swing rule — a whip keeps touching an enemy
it has already hit for the rest of the swing, and is correctly refused. They're counted in the summary but
don't get per-frame lines.

**The trap:** `Projectile.Damage()` wraps its whole NPC loop in `if (damage > 0)`. Once a projectile's damage
decays to zero it stops *attempting* hits rather than landing weak ones — no hit registers, no damage number,
nothing in the tracker. That is indistinguishable from an invulnerable enemy, a targeting failure or an
unpatched loop, and it is why "enemies are immune" reports kept arriving after the hit-immunity bug was
already fixed. Before chasing immunity or slot coverage, check whether `projDmg` reached 0 first.

A trace with **no lines at all** means the tracker didn't measure the weapon, not that the weapon is fine.
Check the filters before concluding anything.

## Balance baseline

Balance is genuinely ill-defined when players can set anywhere from 1 to 1500 enemies, so don't chase it.
Assume **default config** (750 total, 525 enemies, default spawn rate) as the only baseline, and treat any
tuning as "does this weapon still function as its class intends", not "is this number optimal".

## Status legend

- `untested` — not looked at
- `clean` — traced, no defect
- `fixed` — defect found and addressed (link the version)
- `vanilla-limit` — behaves per vanilla; only a config knob can change it
- `open` — defect found, not yet fixed
