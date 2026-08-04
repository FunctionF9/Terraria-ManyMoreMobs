# Ranged

| Weapon | Proj | Status | Notes |
|---|---|---|---|
| Jester's Arrow | — | fixed (0.6) | The original pierce-falloff case: infinite pierce, but damage decayed to ~0 by the far side of a horde. Ranged No Pierce Falloff has defaulted ON since 0.7.4. |
| Guns (single-target) | — | untested | Ranged Extra Pierce defaults to 5 so bullets aren't single-target in a crowd. |
| Bows | — | untested | |
| Launchers | — | untested | Explosive on-death effects run through `Projectile.Kill`, which still has unpatched NPC loops. |
| Chlorophyte Shotbow / Tsunami | — | untested | Multi-shot; good stress test for Anti-Tunnel. |
| Vortex Beater / SDMG | — | untested | High fire rate — watch for per-frame cost at 750 NPCs. |
| Star Cannon / Sniper Rifle | — | untested | |

## Things to watch

- **Anti-Tunnel is OFF by default** and costs per-frame work. Fast projectiles (arrows, bullets) can skip
  enemies they visually pass through because the engine's single-frame box test misses them between updates.
  If a fast weapon under-performs in a dense crowd, toggle it on and re-trace before assuming a mod bug.
- **`Projectile.Kill` has 2 known unpatched NPC loops** (on-death homing, on-death debuff aura). Explosives
  and on-death-effect weapons are the most likely place for a remaining slot bug in this class.
- Extra Pierce only applies where `penetrate > 0` — it can't help a projectile that dies on first contact by
  design (`penetrate == 1` is deliberately excluded, since changing it would rewrite the weapon's identity).
