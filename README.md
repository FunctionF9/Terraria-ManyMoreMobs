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
## Version 0.7.6.6 - segmented-enemies hotfix
- An Eater of Worlds was counting as one boss plus sixty-odd ordinary enemies, so a single fight could swallow most of the enemy budget while the boss budget sat idle. Other worms tag their segments to the head; the Eater doesn't, because it has to be able to split. It should now count as one boss, whole. **test confirmed by dev**
- Worms and Wyverns should no longer arrive chopped. Each segment is a separate spawn, and one turned away by a full enemy budget left a truncated worm, or a torso that promptly deleted itself. Segments now get first claim on a slot, despawning a low-priority mob if the world is completely full. **test confirmed by dev**
- Segments should no longer be despawned out from under a living worm either. Removing one link makes the game delete everything past it, so freeing a single slot could quietly cost twenty.
- Boss health bars should work above slot 200. The bar scanned only the first two hundred slots when picking a boss to follow and refused anything past that, so a boss in the expanded zone got no bar at all. The bars that total up a multi-part body (Eater, Twins, Golem, Moon Lord, Martian Saucer, Pirate Ship, Brain) stopped counting at the same line and could show a fraction of the real health.
- `/debugnpc slot <i>` now reports an NPC's AI style and whether it belongs to a multi-part body.
- New `/debugnpc dumpall` writes a full census to the log: every active NPC, its slot, its category, and which rule assigned it. **test confirmed by dev**

### Known, not fixed
- Several Eaters of Worlds at once share one health bar and it runs off the screen. That is the game's own bar, which adds up every Eater segment alive while sizing itself for a single worm. Other bosses use individual bars, so multiples just show the most recently damaged one. Takes a boss-multiplier mod to see at all, and it is harmless.

## Version 0.7.6.5
- Tier 1 of the Old One's Army should be completable again. Wave 5 sat frozen at 19% while the portals kept producing ordinary enemies and the Dark Mage never appeared, in single-player as much as online. **test confirmed by dev**
- The cause is the 'Old One's Army Wave Length' setting. It multiplies the kills a wave needs, which is right for waves 1 to 4 and wrong for the last one, since that wave ends on the miniboss dying rather than on a count. The game holds it open by parking progress at 139 of 140 and only releases the Dark Mage past half the requirement, so at the default 5x it wants 350 out of a possible 139. Hence 19%, which is 139 out of 700.
- The last wave of each tier now keeps its vanilla length; everything before it still scales as configured. This also covers tier 2 never wrapping up after the Ogre dies. **test confirmed by dev**
- No new world needed if you were stuck. Start the event again. **test confirmed by dev**
- `/debugnpc event` now reports the wave maths while an event is running: the wave, the kill requirement in use, the progress counter, and whether the Dark Mage's spawn condition passes.

## Version 0.7.6.4 - experimental MP hotfix
- Eight of the server broadcast guards added in 0.7.6.1 were never actually applied. The patch finds them by shape rather than by line number, and its search window was one instruction too narrow to see past padding in the compiled code, so a batch of them was skipped in silence while the rest succeeded. Should match now.
- The ordinary natural-spawn broadcast was missed entirely in 0.7.6.1, and it is the widest-reaching of the family. Every naturally spawned enemy goes through it.
- Correcting 0.7.6.1, which overstated this: a missed spawn broadcast delays rather than erases. The enemy still reaches clients the next time its AI syncs itself, so it appears late, behaves oddly for a moment, or seems to take no damage while a client thinks it is elsewhere. How long that lasts depends on the enemy, which is why it reads as random.
- The Old One's Army gates no longer send a redundant packet on every tick where a portal spawns nothing.
- New `/debugnpc version`. In multiplayer it reports the **server's** version, which is the one that counts: tModLoader makes the server's mod list authoritative and switches clients to match it, so a server that hasn't updated silently downgrades everyone on it. Include this when reporting a bug from a server.
- A cap-raise bug present since the beginning, unrelated to multiplayer: vanilla uses slot 200 as a "nothing here" placeholder in the spawn routine, because unmodded that slot is permanently empty. With the cap raised it is an ordinary occupied slot, so whoever stood in it could be quietly turned into a Pinky on a rare roll. Rare, harmless, and unattributable if you ever saw it.
- Dev tooling: the loop audit now scans `DD2Event`, and only counts a method as covered when a patch actually widens its loops. It previously counted any patch at all, so a method hooked for an unrelated reason reported all of its loops as safe. That was hiding fourteen of them.

## Version 0.7.6.3 - hotfix
- Town NPCs that *become* town NPCs should now move into the low slots instead of staying put. Transforming changes an NPC where it stands and never picks a new slot, so the Copper Town Slime from 0.7.6.2 was born in an expanded slot and stayed there, invisible to the housing overlay and the townsfolk count until a world reload. It should sort itself out within half a second now. **test confirmed by dev**
- Same repair covers any town NPC that landed high because the low slots were full at the time, and any modded NPC that transforms into one.

## Version 0.7.6.2 - hotfix
- The Copper Town Slime should be obtainable again. A thrown Copper Shortsword looks for a slime to convert but only checked the first 200 slots, and slimes live in the expanded ones, so the pet was simply unobtainable. Not a multiplayer bug, this one was broken in single-player too. **test confirmed by dev**
- Enemies picking up dropped coins had the same cause.
- Checked the other town pet and slime unlocks while in there: the Elder Slime and the bound slimes already work, and the licensed pets were never affected since they arrive as town NPCs.
- Dev tooling: the loop audit now scans `Terraria.Item`. It previously only looked at Player, Projectile, Main and NPC, which is exactly why the Copper Slime loop sat unnoticed until someone reported it.

## Version 0.7.6.1 - experimental MP hotfix
Still **not supported**, still experimental, soft-tested by dev on local host dedicated server.

- The Old One's Army Dark Mage should appear in multiplayer. It was spawning all along, but the server only announces a new NPC to clients when it lands in the first 200 slots, so expanded-slot spawns were announced to nobody. The Dark Mage existed server-side and was invisible to every player, so the wave could never be cleared.
    - False, fixed in v0.7.6.5 **test confirmed by dev**
- Same silence addressed for adds summoned by enemy AI, on-hit spawns, enemies that split when killed, Queen Slime's minions and Faelings. Twenty places, all the same shape, all server-only, so single-player is untouched.
- Old One's Army minibosses (Dark Mage, Ogre, Betsy) now count as Bosses, with the same guaranteed slot as any other boss. Vanilla gives them no boss flag at all, so they were capped alongside regular enemies and despawnable to make room, and a full enemy cap when the wave triggered meant the miniboss never spawned. Applies in single-player too. **test confirmed by dev**

## Version 0.7.6 - experimental multiplayer testing
Multiplayer is still **not supported**. Best-effort fixes for symptoms reported by players who turned out to be in multiplayer. Treat any multiplayer session as a bug-prone test run, and please report what breaks.

- Critters should be catchable in multiplayer. A client can't catch anything itself, it hides the critter locally and asks the server to do the real catch, and the server was throwing that request away above slot 200. The critter then reappeared on the next sync, which is exactly the reported "goes into the net and right back out of it". **test confirmed by dev**
- Enemy invulnerability frames should now sync for expanded-slot enemies. The packet carrying them was being applied to a throwaway NPC and lost, so clients never learned the real window. A plausible cause of the "enemy takes no damage" reports that only ever showed up in multiplayer.
- Debuffs can be cleared from expanded-slot enemies, coin value pings arrive, and a joining player is sent the whole NPC list rather than just the first 200.
- Neutralised an anti-cheat check that could kick a player for referencing a high NPC slot.
- All of it lives in `MMMultiplayer/` behind 'Experimental Multiplayer Fixes' (Debugging & Experimental, ON by default, applies live). Switch it off for untouched vanilla netcode. Single-player is unaffected either way.
- `/debugnpc` now answers the player who typed it rather than the server console, so it's usable when reporting a multiplayer problem. `/debugnpc track` says outright that it's single-player only instead of quietly logging nothing. **test confirmed by dev**

## Version 0.7.5
- King Slime should stop ambushing you near the world edges, and a Prismatic Lacewing should stop reappearing seconds after the Empress dies. Neither comes from the normal spawn pool; they roll dice on every spawn attempt, so a raised spawn rate multiplied how often they fired. Their odds now widen by the same factor, keeping them at vanilla frequency at any setting. New 'Normalize Rare Spawns' toggle, ON by default; summoning items are unaffected. **test confirmed by dev**
- The Lacewing is a deliberate exception, since hunting one down shouldn't be a chore. 'Lacewing Spawn Boost' keeps it a few times more common than vanilla (5x by default), dropping to full vanilla rarity for two minutes after the Empress spawns or dies so you don't walk straight into re-summoning her.
- `/debugnpc spawninfo` now reports the rare-spawn odds actually in effect. **test confirmed by dev**

## Version 0.7.4
- "Weapons randomly stop working" should be resolved. It was never a missed hit; vanilla shaves ~30% damage off a piercing projectile for every enemy it goes through, which is a fair tax over 5 enemies and compounds to 1 damage across a horde. A traced Cool Whip went 33 → 23 → 16 → 11 across four Mimics. **test confirmed by dev**
- The 'No Pierce Falloff' toggles from 0.6 now default ON for Melee / Ranged / Magic, since the whole point of the mod is fighting crowds. Turn them off for literal vanilla behaviour. Extra Pierce and Anti-Tunnel stay off. **test confirmed by dev**
- Whips got their own 'Summon No Pierce Falloff' toggle, also ON by default. Whips count as summon damage rather than melee, so the Melee toggle never covered them, and each carries its own steep decay: a traced Cool Whip hit literal zero from the ninth enemy of a single swing onward. **test confirmed by dev**
- Broadswords should stop connecting only a couple of enemies per swing. In 1.4.4 the big swords swing as a projectile with a hard target cap baked in: Night's Edge stops after 2, Excalibur and Terra Blade after 3, and the rest of the swing deals nothing. A traced Night's Edge landed on exactly 4 enemies per swing in a crowd of hundreds. 'Melee Extra Pierce' now defaults to 5, matching Ranged. **test confirmed by dev**
- Debug tooling: the hit tracker now covers swung weapons, splits the melee gates by low and high slot, and no longer burns its whole log budget on idle bystanders.

## Version 0.7.3
- Rare critters (Prismatic Lacewing, Truffle Worm, gold/gem critters) should no longer be squeezed out by a full Critter cap. This could be why Empress "wouldn't spawn": the Lacewing that summons her is a critter, and at high spawn rates the critter cap sits permanently full of bunnies. Same fix keeps Duke Fishron's Truffle Worm spawnable, and rare critters are never despawned to make room. **test confirmed by dev**

## Version 0.7.2
- Bug nets should catch critters again. The catch scan only looked at slots 0-199, and critters live in the expanded ones, so nets caught literally nothing in Expanded mode. **test confirmed by dev**
- The invasion progress bar should show up again. The "is an invasion happening near me" check was slot-limited too, so invasions looked endless. **test confirmed by dev**
- Lunatic Cultist ritual and statue spawn limits also patched for the expanded slots.
- New dev tool `tools/audit-npc-loops.sh` audits the game's hardcoded 0-199 NPC loops against the mod's patch list, so remaining gaps are a checklist instead of guesswork. New `/debugnpc event` reports invasion, moon and pillar state plus the low-vs-high slot split.

## Version 0.7.1
- In-game enemy counters can count past 255. They were stored in a single byte. **test confirmed by dev**
- Lifeform Analyzer works past slot 200. It scanned only 0-199 and stored the found NPC's slot in a byte, so rare creatures higher up were invisible or misidentified. It now also prioritises rescuable NPCs over golden critters and rare enemies. **test confirmed by dev**
- Rescue NPCs are recognised as town NPCs and live in the low slots in Expanded mode. **test confirmed by dev**
- Worm-segment guard: in a packed world the slot-replace fallback no longer overwrites a multi-segment boss's own segments.

## Version 0.7
- New Mode setting: Default (vanilla 200 limit, no engine patching) vs Expanded (raised cap). **test confirmed by dev**
- Despawn-to-make-room in Default mode: low-priority mobs are despawned to free slots for town NPCs and bosses, including multi-slot ones. Visible 'pop-out' of despawned enemies. **test confirmed by dev**
- First-pass config overhaul: pages reordered and grouped, tooltips rewritten to state what each setting does, its effect, which mode it applies to, and how settings interact.

## Version 0.6
- Multi-hit melee projectiles (Terra Blade beam, flails, Terraprisma, Night's Edge) should damage enemies in the expanded slots again. **test confirmed by dev**
- New Horde Combat config page: per-class Extra Pierce, Anti-Tunnel, and No Pierce Falloff (stops piercing shots cratering to near-zero damage across a crowd).

## !! Post v0.5 has been entirely vibe coded, pre had a fair amount of oversight & correction
## Version 0.5
- Old One's Army should work end-to-end at the raised cap (crystal renders, waves, loot, victory). Tested a couple of times in isolation, not during a full playthrough.
- New Old One's Army controls: spawn-rate ramp, wave length, and normal mob-spawn suppression.
- All sentries should auto-target enemies in the expanded slots (Frost Hydra, Rainbow Crystal, Houndius Shootius, Queen Spider, Lunar Portal).

## Version 0.4
- Moon-event minibosses should stop over-spawning. The per-type "max N" limits now count the full NPC range.
- Frost Moon wave-20 boss counts limited to avoid slowdown. (Lag was absolutely horrible with 500+ bosses.)
- New Event Length page: invasion size and moon wave-length multipliers. Invasions, eclipses, blood moons and Lunar Pillars checked at the 750 cap.

## Version 0.3
- Minions, homing projectiles and whip targeting should reach and damage enemies in the expanded slots.
- Projectile hit-cooldowns clear for expanded slots again. They never did, which made mobs up there un-hittable.

## Version 0.2
- Foundation: four-category NPC reservation and the experimental engine cap raise (default 750), save/reload-safe, with the first General / Biome / Spawn Item config pages and debug tooling.

## Version 0.1
- At this stage it was a simple spawn-rate modifier mod, inspired by **youtuber @wildlmao's** 30X spawn-rate challenge run.


# Credits & Acknowledgements
- **[tml-NPCUnlimiter](https://github.com/DarioDaF/tml-NPCUnlimiter)** by DarioDaF, original reference this mod's cap-raising groundwork was reverse-engineered and learned from, before being reimplemented here.
