# ManyMoreMobs
MMMmmmmmOD for Terraria to expand the NPC entity cap beyond the default hard 200-engine limit.


# Architecture Overview
The whole mod exists to do one thing: lift Terraria's hard **200-NPC engine limit** to a configurable
total (tested up to 1500) without breaking the rest of the game.


## "The wall"
Terraria pins the NPC count at 200 in a few stubborn ways: `Main.maxNPCs` is a `static readonly int = 200`,
`Main.npc` is a 201-long array, dozens of engine loops are hardcoded as `for (i = 0; i < 200; i++)`, and a
pile of companion arrays (per-projectile hit immunity, melee cooldowns, etc.) are sized to 200. Just making
the array bigger does nothing on its own, the loops still stop at 200, and the per-NPC arrays throw the
moment anything touches slot 200+.


## Breaking through (two .NET gotchas)
- **You can't set `Main.maxNPCs` with reflection** - it's an init-only `readonly`, which throws. 
    We set it by emitting a tiny `stsfld` via `DynamicMethod` instead (`Engine/MaxNpcCapRaise`).
- **You can't trust reads of it either** — the JIT bakes the `readonly` 200 straight into compiled code,
    so even after writing 750 the old reads still see 200. So the real source is our own *mutable* field `EngineState.NpcCap`, and the IL patches inject reads of *that*, never `Main.maxNPCs`.


## The moving parts
- **Raise + resize** (`Engine/MaxNpcCapRaise`, `Engine/EngineArrayResizer`), set the cap,
    then grow `Main.npc` and every NPC-indexed companion array to match, so slot 200+ is safe to touch.
- **IL patches** (`Engine/EngineILPatcher`, via tModLoader's official `MonoModHooks`), rewrite the engine's hardcoded `200` loop bounds to read `EngineState.NpcCap`: the per-tick update, draw, spawn and combat loops.
- **Slot zoning** (`Spawning/SlotAllocator`) — the key trick. Town NPCs and boss heads are steered into the
  native **0–199** slots, while enemies and critters fill the **200+** "expanded" slots. Because every vanilla
  system that only scans the first 200 slots (housing, world save, boss health bars, the map, event checks)
  still finds Town/Boss exactly where it expects them, all of that keeps working with **no patching at all**.
- **Save safety** — saving/loading is deliberately pinned to 200. The expanded zone is session-only and never
  written to the world file, so your save stays 100% vanilla-compatible and the mod can't corrupt it.
- **Categories** (`Categorization/*`, `Spawning/NewNpcGate`) — every spawn in the game funnels through
  `NPC.NewNPC`, so that single chokepoint is where NPCs get sorted into **Town / Boss / Critter / Enemy** and
  per-category caps are enforced. It's source-aware, so boss segments and summons inherit their parent's category.
- **Despawn-to-make-room** (`Spawning/EntityEvictor`) — in Default mode the caps act as ceilings: a
  low-priority mob is despawned so Town NPCs and bosses can always get a slot.


## Two modes, and a clean exit
Everything above is gated behind the **Mode** setting. **Default** does *zero* engine patching (just the
category/spawn tuning within vanilla's 200), while **Expanded** turns on the full treatment. Every hook is
registered through `MonoModHooks`, so they all cleanly auto-undo when the mod unloads or is disabled.


## For other modders
It's all managed IL (MonoMod + reflection) — no native code. If you're building a compatibility or "bridge"
mod, the spots that matter most are `Engine/EngineILPatcher` (what's patched), `Spawning/SlotAllocator`
(where NPCs land) and `Spawning/NewNpcGate` (the spawn chokepoint + categories). MIT-licensed, so go wild.



# Versioning History
## Version 0.7.6.2 - hotfix
- The Copper Town Slime can be obtained again. Throwing a Copper Shortsword at a slime made the dropped sword look for a slime to convert, but it only ever checked the first 200 slots, and slimes live in the expanded ones. It found nothing, so the pet was simply unobtainable. Not a multiplayer bug, this one was broken in single-player too.
- Enemies can pick up dropped coins again, which had the same cause.
- Checked the rest of the town pet and town slime unlocks while in there: the Elder Slime (right-click to free) and the bound slimes already work, and the licensed pets (cat, dog, bunny) were never affected since they arrive as town NPCs.
- Dev tooling: the loop audit now scans `Terraria.Item` as well. It previously only looked at Player, Projectile, Main and NPC, which is exactly why the Copper Slime loop sat unnoticed until someone reported it.

## Version 0.7.6.1 - experimental MP hotfix
Still **not supported**, still experimental, still untested by me in a real (non-local) session.

- The Old One's Army Dark Mage now appears in multiplayer. It was spawning all along, but the server only announces a newly spawned NPC to clients when it lands in the first 200 slots, and expanded-slot spawns were announced to nobody. The Dark Mage existed on the server and was invisible to every player, so the wave could never be cleared.
- Same silence fixed for adds summoned by enemy AI, on-hit spawns, enemies that split when killed, Queen Slime's minions and Faelings. Twenty places in total, all the same shape, all server-only, so single-player is untouched.
- Old One's Army minibosses (Dark Mage, Ogre, Betsy) now count as Bosses. Vanilla gives them no boss flag at all, a Dark Mage is just an enemy with 500 health, so they were treated as ordinary trash: capped alongside regular enemies and despawnable to make room. If the enemy cap happened to be full when the wave triggered, the miniboss simply never spawned and the wave couldn't be finished. They now get the same guaranteed slot as any other boss. This one applies in single-player too.

## Version 0.7.6 - experimental multiplayer testing
Multiplayer is still **not supported**. These are best-effort fixes for symptoms reported by players who turned out to be playing in multiplayer, and they are untested by me. Treat any multiplayer session as a test run, and please report what breaks.

- Critters can be caught in multiplayer. A client can't catch anything itself, it hides the critter locally and asks the server to do the real catch, and the server was throwing that request away for anything above slot 200. The critter then reappeared on the next sync, which is exactly the reported "goes into the net and right back out of it".
- Enemy invulnerability frames now sync for expanded-slot enemies. The packet carrying them was being applied to a throwaway NPC and lost, so clients never learned the real window. This is a plausible cause of the "enemy takes no damage" reports that only ever showed up in multiplayer.
- Debuffs can be cleared from expanded-slot enemies, coin value pings arrive, and a player joining now gets sent the whole NPC list rather than just the first 200.
- Neutralised an anti-cheat check that could kick a player from the server for referencing a high NPC slot.
- All of it lives in its own `MMMultiplayer/` folder and is gated behind 'Experimental Multiplayer Fixes' (Debugging & Experimental, ON by default, applies live). Switch it off for untouched vanilla netcode. Nothing in single-player is affected either way.
- `/debugnpc` now answers the player who typed it rather than the server console, so it's actually usable when reporting a multiplayer problem. `/debugnpc track` says outright that it's single-player only instead of quietly logging nothing.

## Version 0.7.5
- King Slime no longer ambushes you every minute near the world edges, and a Prismatic Lacewing no longer reappears seconds after the Empress dies. Neither is drawn from the normal spawn pool, they roll dice on every spawn attempt (King Slime 1-in-300, Lacewing 1-in-10), so raising the spawn rate multiplied how often they fired. Their odds now widen by the same factor the spawn rate was widened by, keeping them at vanilla frequency at any setting. New 'Normalize Rare Spawns' toggle, ON by default; summoning items are unaffected either way.
- The Lacewing is a deliberate exception, since hunting one down shouldn't be a chore. 'Lacewing Spawn Boost' keeps it a few times more common than vanilla (5x by default), except for two minutes after the Empress spawns or dies, where it drops to full vanilla rarity so you don't walk straight into re-summoning her.
- `/debugnpc spawninfo` now reports the rare-spawn odds actually in effect.

## Version 0.7.4
- "Weapons randomly stop working" fixed. It was never a missed hit, hits were landing fine, but vanilla shaves ~30% damage off a piercing projectile for every enemy it goes through. Over 5 enemies that's a fair tax, across a horde it compounds down to 1 damage. A traced Cool Whip went 33 -> 23 -> 16 -> 11 across four Mimics and landed for 1 on the last.
- The 'No Pierce Falloff' toggles (added in 0.6) now default ON for Melee / Ranged / Magic, since the whole point of the mod is fighting crowds. Turn them off for literal vanilla behaviour. Extra Pierce and Anti-Tunnel stay off by default.
- Whips got their own 'Summon No Pierce Falloff' toggle, also ON by default. Whips count as summon damage rather than melee, so the Melee toggle never covered them, and each whip type carries its own steep decay: a traced Cool Whip ran 33 -> 23 -> 16 -> 11 -> 7 -> 4 -> 2 -> 1 -> 0, dealing literal zero from the ninth enemy of a single swing onward.
- Broadswords no longer stop connecting a couple of enemies into a swing. In 1.4.4 the big swords swing as a projectile with a hard enemy cap baked in, Night's Edge stops after 2, Excalibur and Terra Blade after 3, True Excalibur after 6, and the remainder of the swing deals nothing at all. A traced Night's Edge landed on exactly 4 enemies per swing, at full damage, while standing in a crowd of hundreds. 'Melee Extra Pierce' now defaults to 5, matching Ranged.
- Debug tooling: the hit tracker now covers swung weapons too (they were previously invisible to it), reports the melee gates split by low vs high slot, and no longer burns its whole log budget on idle bystanders in a packed crowd.

## Version 0.7.3
- Rare critters (Prismatic Lacewing, Truffle Worm, gold/gem critters) no longer get squeezed out by a full Critter cap. This is why Empress of Light "wouldn't spawn", the Lacewing that summons her is a critter, and at high spawn rates the critter cap sits permanently full of bunnies so she could never appear. Same fix keeps Duke Fishron's Truffle Worm spawnable, and rare critters are now never despawned to make room.

## Version 0.7.2
- Bug nets can catch critters again. The catch scan only ever looked at slots 0-199, and critters live in the expanded slots, so nets caught literally nothing in Expanded mode.
- Invasion progress bar shows up again. The "is an invasion happening near me" check was slot-limited too, so with invaders in the expanded slots the bar never appeared and invasions looked endless.
- Lunatic Cultist ritual and statue spawn limits also patched for the expanded slots.
- New dev tool: `tools/audit-npc-loops.sh` audits the game's ~220 hardcoded 0-199 NPC loops against the mod's patch list, so remaining gaps are a checklist instead of guesswork. New `/debugnpc event` command reports invasion/moon/pillar state plus the low-vs-high slot split.

## Version 0.7.1
- In-game enemy counters patched, no longer restricted to single byte so can count past 255.
- Lifeform Analyzer patched, default scan only 0-199 slots, storing found NPC's slot in a byte, so rare creatures living past slot 200 were invisible or grabbed from wrong slot due to looping.
    - Lifeform Analyzer got minor QoL fix, prioritize showing rescuable NPCs over golden critters or rare enemies.
- Spawn worm-segment guard, in packed world slot-replace fallback no longer overwrite multi-segment boss's own segments.
- Rescue NPCs patched, correctly identifies as town NPCs and lives in low slots in Expanded mode.

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


# Credits & Acknowledgements
- **[tml-NPCUnlimiter](https://github.com/DarioDaF/tml-NPCUnlimiter)** by DarioDaF, original reference this mod's cap-raising groundwork was reverse-engineered and learned from, before being reimplemented here.
