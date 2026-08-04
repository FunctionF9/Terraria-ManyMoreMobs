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

1. `/debugnpc track`, then use the weapon in a crowd. **One weapon per trace** — the per-frame geometry
   dump is what fills the log budget, so testing two weapons back to back can leave the second with no data.
2. `/debugnpc immune` while still tracking (toggling track off first loses the context).
3. Read the trace for the three failure shapes below.
4. Record the result, even when it's clean. "Verified clean" is a result.

## What to look for

| Shape | Symptom in the log | Meaning |
|---|---|---|
| Damage decay | `projDmg=` shrinking hit to hit | Per-type falloff — needs a No Pierce Falloff toggle for that class |
| Pierce cap | Hits stop after N enemies, `projDmg` unchanged | Vanilla `penetrate` limit — Extra Pierce covers it |
| Slot blindness | Hits only ever land on slots < 200 | An unpatched `0-199` loop — run `tools/audit-npc-loops.sh` |

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
