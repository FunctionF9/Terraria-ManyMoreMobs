# ManyMoreMobs
MMMmmmmmOD for Terraria to expand the NPC entity cap beyond the default hard 200-engine limit.


# Architecture Overview
The whole mod exists to do one thing: lift Terraria's hard **200-NPC engine limit** to a configurable
total (up to 1500) without breaking the rest of the game. Here's the gist of how that was made possible.


## "The wall"
Terraria pins the NPC count at 200 in a few stubborn ways: `Main.maxNPCs` is a `static readonly int = 200`,
`Main.npc` is a 201-long array, dozens of engine loops are hardcoded as `for (i = 0; i < 200; i++)`, and a
pile of companion arrays (per-projectile hit immunity, melee cooldowns, etc.) are sized to 200. Just making
the array bigger does nothing on its own — the loops still stop at 200, and the per-NPC arrays throw the
moment anything touches slot 200+.


## Breaking through (two .NET gotchas)
- **You can't set `Main.maxNPCs` with reflection** — it's an init-only `readonly`, which throws. We set it by
  emitting a tiny `stsfld` via `DynamicMethod` instead (`Engine/MaxNpcCapRaise`).
- **You can't trust reads of it either** — the JIT bakes the `readonly` 200 straight into compiled code, so
  even after writing 750 the old reads still see 200. So the real source of truth is our own *mutable* field
  `EngineState.NpcCap`, and the IL patches inject reads of *that*, never `Main.maxNPCs`.


## The moving parts
- **Raise + resize** (`Engine/MaxNpcCapRaise`, `Engine/EngineArrayResizer`) — set the cap, then grow
  `Main.npc` and every NPC-indexed companion array to match, so slot 200+ is safe to touch.
- **IL patches** (`Engine/EngineILPatcher`, via tModLoader's official `MonoModHooks`) — rewrite the engine's
  hardcoded `200` loop bounds to read `EngineState.NpcCap`: the per-tick update, draw, spawn and combat loops.
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

# Credits & Acknowledgements

- **[tml-NPCUnlimiter](https://github.com/DarioDaF/tml-NPCUnlimiter)** by DarioDaF — the original reference
  this mod's cap-raising groundwork was reverse-engineered and learned from, before being reimplemented here.
  Huge thanks for charting the first steps past the 200-NPC wall.
- **@wildlmao** — whose 30X spawn-rate challenge run sparked the whole idea in the first place.
