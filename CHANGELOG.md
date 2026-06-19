# Many More Mobs — Changelog

Version history for Many More Mobs. (Full feature list, config explanations and warnings live on the
Steam Workshop page; this is the running version log moved off the Workshop page to fit Steam's
8000-byte description limit.)

## Version 0.7
- New Mode setting: Default (vanilla 200 limit, no engine patching) vs Expanded (raised cap).
- Despawn-to-make-room in Default mode: low-priority mobs are despawned to free up slots for town NPCs and bosses, including multi-slot bosses. Visible 'pop-out' of despawned enemies.
- First-pass config overhaul: pages reordered and grouped, tooltips rewritten for clarity (what each setting does, its effect, which mode it applies to, and how settings interact).

## Version 0.6
- Fixed multi-hit melee projectiles (Terra Blade beam, flails, Terraprisma, Night's Edge) going unable to damage enemies in the expanded slots.
- New Horde Combat config page: per-class Extra Pierce, Anti-Tunnel, and No Pierce Falloff (stops piercing shots from cratering to near-zero damage across a crowd).

## Version 0.5
- Old One's Army should work end-to-end at the raised cap (crystal renders, waves, loot, victory). Tested a couple of times in isolation, not during a full playthrough.
- New Old One's Army controls: spawn-rate ramp, wave length, and normal mob-spawn suppression.
- All sentries now correctly auto-target enemies in the expanded slots (Frost Hydra, Rainbow Crystal, Houndius Shootius, Queen Spider, Lunar Portal).

## Version 0.4
- Fixed moon-event minibosses over-spawning (per-type "max N" limits now count the full NPC range).
- Limited Frost Moon wave-20 boss counts to avoid slowdown. (Lag was absolutely horrible with 500+ bosses.)
- New Event Length page: invasion size and moon wave-length multipliers; verified invasions, eclipses, blood moons and Lunar Pillars at the 750 cap.

## Version 0.3
- Minions, homing projectiles and whip targeting now reach and damage enemies in the expanded slots.
- Fixed projectile hit-cooldowns never clearing for expanded slots (mobs becoming un-hittable).

## Version 0.2
- Foundation: four-category NPC reservation and the experimental engine cap raise (default 750), save/reload-safe, with the first General / Biome / Spawn Item config pages and debug tooling.

## Version 0.1
- At this stage it was a simple spawn-rate modifier mod, inspired by youtuber @wildlmao's 30X spawn-rate challenge run.
