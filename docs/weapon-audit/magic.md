# Magic

| Weapon | Proj | Status | Notes |
|---|---|---|---|
| Magic Missile family | — | untested | `AI_009_MagicMissiles` patched. |
| Magnet Sphere | — | clean (0.5) | `AI_047_MagnetSphere_TryAttacking` patched. |
| Medusa Head | — | clean (0.5) | `AI_100_Medusa` patched. |
| Life Drain | — | untested | `AI_185_LifeDrain` is a **known unpatched gap** — likely only finds targets in slots 0-199. |
| Dryad's Ward / Nettle Burst | — | untested | `AI_111_DryadsWard` is a known unpatched gap. |
| Last Prism / Lunar Flare | — | untested | |
| Razorblade Typhoon / Bat Scepter | — | untested | |
| Nebula Blaze | — | untested | Homing; homing paths were fixed in 0.3. |
| Crystal Serpent / Shadowbeam | — | untested | Piercing — good falloff test. |

## Things to watch

- **Two known unpatched AI methods in this class**: `AI_185_LifeDrain` and `AI_111_DryadsWard`. Both appear
  in the audit report as GAPs. If a channelled or aura-style magic weapon ignores the horde entirely, start
  there rather than guessing.
- Magic Extra Pierce defaults to 2 (lower than Ranged's 5) since many magic weapons already pierce heavily.
- Magic No Pierce Falloff defaults ON as of 0.7.4.

Run `tools/audit-npc-loops.sh` before investigating anything here — the gaps above are already known and
listed, so a "weapon doesn't work" report in this class should be checked against the report first.
