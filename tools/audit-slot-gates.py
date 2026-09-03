#!/usr/bin/env python3
"""Find NON-LOOP slot gates in the engine — the shape audit-npc-loops.sh structurally cannot see.

    python tools/audit-slot-gates.py                     # dumps IL if needed, then scans
    python tools/audit-slot-gates.py path/to/engine.il   # scan an existing dump

WHY THIS EXISTS
---------------
audit-npc-loops.sh reads decompiled C# and inventories LOOPS. A bare guard is not a loop:

    int slot = (int)ai[1];
    if (slot < 0 || slot >= 200)        // <- invisible to the loop audit
        Kill();

so it is reported as nothing at all — not even as a GAP. That blind spot hid all three of
Projectile.VanillaAI's stuck-projectile gates and the Coin Loss Revenge sentinel for an entire
release line. The loop audit said VanillaAI was PATCHED the whole time, and it was — its loops
were.

Read the output as a triage list, not a patch list, and check REACHABILITY before acting on an
entry. This scan also flags NPC.AI_124_DeerclopsLeg, whose gate is textbook — and whose method has
zero call sites anywhere in the assembly, so patching it fixed nothing. A gate in a method nobody
calls is decoration. Confirm something calls it before you write the patch, let alone a patch note.

This reads IL instead of C#, so it sees the comparison itself rather than the syntax around it.

THE TWO SHAPES
--------------
    INT    <ldloc L> ; ldc.i4 200 ; <forward cond branch>      where L is a PROVEN slot local
    FLOAT  ldelem.r4 ; ldc.r4 200 ; <forward cond branch>      + Main.npc loaded within 12

"PROVEN slot local" means the method contains `ldsfld Main::npc ; ldloc L ; ldelem.ref`
somewhere — L is literally used to index Main.npc. That test, rather than a fixed look-ahead,
is what makes the third VanillaAI gate visible: it guards a dust emitter whose body never
touches Main.npc, so no look-ahead of any size could classify it, but it guards the same
variable the other two gates guard.

The float side needs the Main.npc proximity test for the opposite reason — to REJECT things.
`ldelem.r4 ; ldc.r4 200 ; branch` is not unique to a slot gate: NPC.AI_005_EaterOfSouls
matches it exactly with `if (ai[0] > 200f) ai[0] = -200f;`, which is the oscillation counter
that makes Eaters of Souls wobble. Widening that would change their movement, not fix a bug.

NOTE ON THE DUMP: `ilspycmd -il` ignores `-t` and emits the WHOLE assembly (~125 MB, ~3 min),
so one dump covers every type. That is why this tool has no equivalent of the loop audit's
TYPES list, and cannot inherit its blind spot.

Output is a triage list, not a patch list. Cross-check each hit against both registries
(Engine/EngineILPatcher.cs and MMMultiplayer/MultiplayerNetPatcher.cs) before acting: a gate
preceded by a `Main.netMode` read and followed by a NetMessage.SendData call is a multiplayer
broadcast guard owned by the MP patcher, and must NOT be widened here as well.
"""
import os
import re
import subprocess
import sys

INSTR = re.compile(r'^\s*IL_([0-9a-f]+):\s+(\S+)(?:\s+(.*))?$')
ENDM = re.compile(r'^\s*\}\s*//\s*end of method\s+(\S+)')
LDLOC = re.compile(r'^ldloc(\.s|\.0|\.1|\.2|\.3)?$')
COND = {
    'beq', 'beq.s', 'bne.un', 'bne.un.s', 'bge', 'bge.s', 'bge.un', 'bge.un.s',
    'bgt', 'bgt.s', 'bgt.un', 'bgt.un.s', 'ble', 'ble.s', 'ble.un', 'ble.un.s',
    'blt', 'blt.s', 'blt.un', 'blt.un.s', 'brtrue', 'brtrue.s', 'brfalse', 'brfalse.s',
}

def find_dll():
    """TML_DLL if set, else probe the usual Steam roots. Mirrors audit-npc-loops.sh so the two
    tools agree about which engine they are describing; no path here is machine-specific."""
    env = os.environ.get("TML_DLL")
    if env:
        return env
    tail = "steamapps/common/tModLoader/tModLoader.dll"
    home = os.path.expanduser("~")
    roots = [os.path.join(home, ".steam", "steam"),
             os.path.join(home, ".local", "share", "Steam"),
             os.path.join(home, "Library", "Application Support", "Steam")]
    for drive in "cdefgh":
        for base in ("Steam", "SteamLibrary",
                     "Program Files (x86)/Steam", "Program Files/Steam"):
            roots.append("/%s/%s" % (drive, base))
            roots.append("%s:/%s" % (drive.upper(), base))
    for root in roots:
        cand = os.path.join(root, tail).replace("\\", "/")
        if os.path.isfile(cand):
            return cand
    return "tModLoader.dll"


DEFAULT_DLL = find_dll()
CACHE = os.path.join(os.environ.get("TMPDIR", "/tmp"), "mmm-audit", "engine.il")


def ensure_dump(path):
    if os.path.isfile(path) and os.path.getsize(path) > 1_000_000:
        if not os.path.isfile(DEFAULT_DLL) or \
           os.path.getmtime(path) >= os.path.getmtime(DEFAULT_DLL):
            return path
    os.makedirs(os.path.dirname(path), exist_ok=True)
    sys.stderr.write("dumping IL (this takes a few minutes)...\n")
    tmp = path + ".tmp"
    with open(tmp, "w", encoding="utf-8") as fh:
        rc = subprocess.call(["ilspycmd", DEFAULT_DLL, "-il"], stdout=fh)
    if rc != 0 or os.path.getsize(tmp) < 1_000_000:
        sys.stderr.write("error: ilspycmd produced no usable output\n")
        sys.exit(1)
    os.replace(tmp, path)
    return path


def parse(path):
    """Yield (method_name, [(offset, opcode, operand, srcline), ...])."""
    cur, buf = None, []
    with open(path, encoding='utf-8', errors='replace') as fh:
        for lineno, line in enumerate(fh, 1):
            if ENDM.match(line):
                if cur and buf:
                    yield cur, buf
                cur, buf = None, []
                continue
            if 'cil managed' in line:
                m = re.search(r'(\w+)\s*\([^)]*\)\s*cil managed', line)
                if m:
                    if cur and buf:
                        yield cur, buf
                    cur, buf = m.group(1), []
                continue
            im = INSTR.match(line)
            if im and cur:
                buf.append((int(im.group(1), 16), im.group(2),
                            (im.group(3) or '').strip(), lineno))
    if cur and buf:
        yield cur, buf


def local_id(op, arg):
    if not op or not LDLOC.match(op):
        return None
    if op in ('ldloc.0', 'ldloc.1', 'ldloc.2', 'ldloc.3'):
        return op[-1]
    return arg.split()[0] if arg else None


def prev_real(ins, i):
    """Skip nop. This build pads EVERY long-form ldloc/stloc/ldloca with two nops, so raw
    neighbour access silently fails in exactly the methods that matter most.

    The long form starts at local index 255 -- measured, not assumed: the highest short-form index
    anywhere in the assembly is `ldloc.s 254`, the lowest long-form is `ldloc 255`, and `ldloc.s 255`
    does not occur at all. So the padding is confined to methods with 255+ locals."""
    k = i - 1
    while k >= 0 and ins[k][1] == 'nop':
        k -= 1
    return ins[k] if k >= 0 else (0, None, None, 0)


def next_real(ins, i):
    k = i + 1
    while k < len(ins) and ins[k][1] == 'nop':
        k += 1
    return ins[k] if k < len(ins) else (0, None, None, 0)


def loads_npc_within(ins, i, window):
    seen, k = 0, i + 1
    while k < len(ins) and seen < window:
        if ins[k][1] == 'nop':
            k += 1
            continue
        seen += 1
        if ins[k][1] == 'ldsfld' and 'Terraria.Main::npc' in (ins[k][2] or ''):
            return True
        k += 1
    return False


def mp_owned(ins, i):
    """A server broadcast guard: Main.netMode read within 6 real instructions before, and a
    NetMessage.SendData call within 20 real after. Owned by MMMultiplayer, not by us."""
    seen, k, before = 0, i - 1, False
    while k >= 0 and seen < 6:
        if ins[k][1] != 'nop':
            seen += 1
            if ins[k][1] == 'ldsfld' and 'Terraria.Main::netMode' in (ins[k][2] or ''):
                before = True
                break
        k -= 1
    if not before:
        return False
    seen, k = 0, i + 1
    while k < len(ins) and seen < 20:
        if ins[k][1] != 'nop':
            seen += 1
            if 'NetMessage::SendData' in (ins[k][2] or ''):
                return True
        k += 1
    return False


def scan(path):
    ints, floats = [], []
    for meth, ins in parse(path):
        proven = set()
        for i, (_off, op, arg, _ln) in enumerate(ins):
            L = local_id(op, arg)
            if L is None:
                continue
            p = prev_real(ins, i)
            if p[1] == 'ldsfld' and 'Terraria.Main::npc' in (p[2] or '') \
               and next_real(ins, i)[1] == 'ldelem.ref':
                proven.add(L)

        for i, (off, op, arg, ln) in enumerate(ins):
            is_int = op == 'ldc.i4' and arg.strip() == '200'
            is_flt = op == 'ldc.r4' and arg.strip() == '200'
            if not (is_int or is_flt):
                continue
            nxt = next_real(ins, i)
            if nxt[1] not in COND:
                continue
            m = re.search(r'IL_([0-9a-f]+)', nxt[2] or '')
            if m and int(m.group(1), 16) < off:
                continue                      # backward branch => loop bound, not a gate
            p = prev_real(ins, i)
            if is_int:
                if local_id(p[1], p[2]) in proven:
                    ints.append((meth, ln, mp_owned(ins, i)))
            elif p[1] == 'ldelem.r4' and loads_npc_within(ins, i, 12):
                floats.append((meth, ln, mp_owned(ins, i)))

    def emit(title, rows):
        print("## %s (%d)" % (title, len(rows)))
        for meth, ln, mp in sorted(set(rows)):
            tag = "  [MP broadcast guard - owned by MMMultiplayer]" if mp else ""
            print("   %-46s il-line %-9d%s" % (meth, ln, tag))
        print()

    print("# Non-loop slot gates\n")
    emit("INT gates", ints)
    emit("FLOAT gates", floats)


if __name__ == '__main__':
    scan(ensure_dump(sys.argv[1]) if len(sys.argv) > 1 else ensure_dump(CACHE))
