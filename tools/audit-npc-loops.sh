#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# audit-npc-loops.sh — NPC-loop coverage audit for Many More Mobs.
#
# The engine is littered with hardcoded `for (i = 0; i < 200; i++)` loops over
# Main.npc. Every one of them silently ignores the expanded slots (200+), which
# is the root cause behind most "X doesn't work with this mod" reports. We patch
# a curated subset in Engine/EngineILPatcher.cs — this script diffs the engine's
# full inventory against that registry so the remaining gaps are a checklist
# instead of guesswork.
#
# Usage:   tools/audit-npc-loops.sh [output.md]
#          TML_DLL=/path/to/tModLoader.dll tools/audit-npc-loops.sh
#
# Re-run it after every tModLoader update: line numbers and even method names
# move between builds, and a patch that silently stops matching is invisible
# until someone reports a bug.
#
# Deliberate exclusions live next to the code, not here. Mark them with a
#   // AUDIT-SKIP: Type.Method — reason
# comment anywhere in EngineILPatcher.cs and they're reported as SKIP.
# ---------------------------------------------------------------------------
set -uo pipefail

# Point TML_DLL at your own tModLoader.dll, or let the usual Steam locations be probed. Drive letters are
# tried explicitly rather than globbed: under Git Bash the /c, /d, ... mounts are virtual and don't expand
# from a glob on /.
if [ -z "${TML_DLL:-}" ]; then
    probe() { [ -f "$1/steamapps/common/tModLoader/tModLoader.dll" ] && TML_DLL="$1/steamapps/common/tModLoader/tModLoader.dll"; }
    for root in "$HOME/.steam/steam" "$HOME/.local/share/Steam" \
                "$HOME/Library/Application Support/Steam"; do
        probe "$root" && break
    done
    if [ -z "${TML_DLL:-}" ]; then
        for drive in c d e f g h; do
            for base in "/$drive/Steam" "/$drive/SteamLibrary" \
                        "/$drive/Program Files (x86)/Steam" "/$drive/Program Files/Steam"; do
                probe "$base" && break 2
            done
        done
    fi
fi
TML_DLL="${TML_DLL:-tModLoader.dll}"
REPO_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PATCHER="$REPO_DIR/Engine/EngineILPatcher.cs"
OUT="${1:-$REPO_DIR/tools/audit-report.md}"
WORK="${TMPDIR:-/tmp}/mmm-audit"

# The four types that hold essentially every NPC-indexed loop in the game.
# Item joined the list in 0.7.6.2: a player reported the Copper Town Slime being unobtainable, and the cause
# was a plain 0-199 NPC loop in Item.GetPickedUpByMonsters_Special that this audit had never looked at.
# If a report points at a system none of these types own, widen this list before assuming the code is fine.
TYPES="Player Projectile Main NPC Item"

command -v ilspycmd >/dev/null 2>&1 || {
    echo "error: ilspycmd not found on PATH (dotnet tool install --global ilspycmd --version 8.0.0.7345)" >&2
    exit 1
}
[ -f "$TML_DLL" ] || { echo "error: tModLoader.dll not found at $TML_DLL (set TML_DLL=...)" >&2; exit 1; }
[ -f "$PATCHER" ] || { echo "error: patcher not found at $PATCHER" >&2; exit 1; }

mkdir -p "$WORK"

# ── 1. Decompile (cached; refresh whenever the dll is newer than our copy) ──
for t in $TYPES; do
    src="$WORK/$t.cs"
    if [ ! -s "$src" ] || [ "$TML_DLL" -nt "$src" ]; then
        echo "decompiling Terraria.$t ..." >&2
        ilspycmd "$TML_DLL" -t "Terraria.$t" > "$src" 2>/dev/null
    fi
done

# ── 2. Inventory every NPC-indexed loop, tagged with its enclosing method ──
# Catches both the ascending `< 200` form and the descending `= 199; >= 0` form
# (the health-bar draw loop was a 199 that the blanket 200-patch never saw).
inventory() {
    awk -v TYPE="$1" '
        { line[NR] = $0 }
        END {
            method = "(file scope)"
            for (i = 1; i <= NR; i++) {
                if (line[i] ~ /^\t(public|private|protected|internal)[^=]*\(/) {
                    m = line[i]
                    sub(/\(.*/, "", m)          # drop parameter list
                    sub(/.*[ \t]/, "", m)       # drop return type / modifiers
                    if (m != "") method = m
                }
                asc  = (line[i] ~ /for \(int [A-Za-z0-9_]+ = 0; [A-Za-z0-9_]+ < 200[;&) ]/)
                desc = (line[i] ~ /for \(int [A-Za-z0-9_]+ = 199; [A-Za-z0-9_]+ >= 0/)
                if (!asc && !desc) continue
                body = ""
                for (j = i + 1; j <= i + 6 && j <= NR; j++) body = body " " line[j]
                if (body !~ /npc\[/) continue   # not an NPC loop (some other 200)
                code = line[i]; gsub(/^[ \t]+|[ \t]+$/, "", code)
                printf "%s\t%s\t%d\t%s\n", TYPE, method, i, code
            }
        }
    ' "$WORK/$1.cs"
}

: > "$WORK/loops.tsv"
for t in $TYPES; do inventory "$t" >> "$WORK/loops.tsv"; done

# ── 3. Parse the patch registry out of EngineILPatcher.cs ──
# Explicit call shapes only — a loose "any quoted string" grep would mark gaps
# as covered, and a false PATCHED is far worse than a false GAP here.
{
    # PatchMethod / PatchNpcLoops / PatchChaseLoops (mod, typeof(Type), "Name")
    grep -oE 'Patch(Method|NpcLoops|ChaseLoops)\(mod, typeof\([A-Za-z]+\), "[A-Za-z0-9_]+"' "$PATCHER" \
        | sed -E 's/.*typeof\(([A-Za-z]+)\), "([A-Za-z0-9_]+)"/\1::\2/'
    # typeof(Type).GetMethod("Name" ...) and GetMethod(nameof(Type.Name) ...)
    grep -oE 'typeof\([A-Za-z]+\)\.GetMethod\("[A-Za-z0-9_]+"' "$PATCHER" \
        | sed -E 's/typeof\(([A-Za-z]+)\)\.GetMethod\("([A-Za-z0-9_]+)"/\1::\2/'
    grep -oE 'typeof\([A-Za-z]+\)\.GetMethod\(nameof\([A-Za-z]+\.[A-Za-z0-9_]+\)' "$PATCHER" \
        | sed -E 's/typeof\(([A-Za-z]+)\)\.GetMethod\(nameof\([A-Za-z]+\.([A-Za-z0-9_]+)\)/\1::\2/'
    # ChaseLoopAIMethods[] — every entry is a Projectile AI method.
    sed -n '/ChaseLoopAIMethods *=/,/};/p' "$PATCHER" \
        | grep -oE '"[A-Za-z0-9_]+"' | tr -d '"' | sed 's/^/Projectile::/'
} | sort -u > "$WORK/patched.txt"

# AUDIT-SKIP: Type.Method — reason
grep -oE 'AUDIT-SKIP: *[A-Za-z]+\.[A-Za-z0-9_]+' "$PATCHER" 2>/dev/null \
    | sed -E 's/AUDIT-SKIP: *([A-Za-z]+)\.([A-Za-z0-9_]+)/\1::\2/' | sort -u > "$WORK/skipped.txt" || : > "$WORK/skipped.txt"

# ── 4. Report ──
{
    echo "# NPC-loop coverage audit"
    echo
    # Report the dll's identity, never its path — the report is committed, and a local install
    # path is both noise to other readers and a needless detail to publish.
    echo "Generated by \`tools/audit-npc-loops.sh\` against \`$(basename "$TML_DLL")\`."
    echo "Engine loops over \`Main.npc\` bounded by a hardcoded 200/199, grouped by enclosing method."
    echo
    echo "- **GAP** — the expanded slots (200+) are invisible to this code. Triage it."
    echo "- **PATCHED** — a patch targets this method. (Presence, not proof: verify the patch still matches.)"
    echo "- **SKIP** — deliberately excluded via an \`AUDIT-SKIP\` comment in the patcher."
    echo

    total=$(wc -l < "$WORK/loops.tsv" | tr -d ' ')
    gaps=0
    while IFS=$'\t' read -r type method line code; do
        key="$type::$method"
        grep -qxF "$key" "$WORK/patched.txt" && continue
        grep -qxF "$key" "$WORK/skipped.txt" && continue
        gaps=$((gaps + 1))
    done < "$WORK/loops.tsv"

    echo "**$total NPC loops found · $gaps in un-patched methods.**"
    echo

    for t in $TYPES; do
        echo "## Terraria.$t"
        echo
        printf '| Status | Method | Line | Loop |\n|---|---|---|---|\n'
        awk -F'\t' -v T="$t" '$1 == T' "$WORK/loops.tsv" | sort -t$'\t' -k2,2 -k3,3n |
        while IFS=$'\t' read -r type method line code; do
            key="$type::$method"
            if grep -qxF "$key" "$WORK/skipped.txt"; then status="SKIP"
            elif grep -qxF "$key" "$WORK/patched.txt"; then status="PATCHED"
            else status="**GAP**"; fi
            printf '| %s | `%s` | %s | `%s` |\n' "$status" "$method" "$line" "${code//|/\\|}"
        done
        echo
    done
} > "$OUT"

echo "wrote $OUT" >&2
grep -c 'GAP' "$OUT" | sed 's/^/gap rows: /' >&2
