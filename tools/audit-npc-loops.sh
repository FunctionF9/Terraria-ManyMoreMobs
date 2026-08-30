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

# Types holding NPC-indexed loops. Fully-qualified; the leaf name is used for filenames and report headings.
#
# This list has been WRONG TWICE, both times found by a player report rather than by the audit:
#   0.7.6.2  Terraria.Item was missing      -> Copper Town Slime unobtainable (GetPickedUpByMonsters_Special)
#   0.7.6.4  DD2Event was missing            -> Old One's Army triage had to be done by hand
#   0.7.6.6  the whole BigProgressBar namespace was missing -> bosses above slot 200 drew no health bar
# Widen this list before concluding that code is fine. An absent type reads exactly like a clean audit.
# The lesson from the third one: UI namespaces count. A loop that only decides what to draw still goes
# wrong the same way, and "the boss has no health bar" is a louder bug report than most gameplay ones.
TYPES="Terraria.Player Terraria.Projectile Terraria.Main Terraria.NPC Terraria.Item
       Terraria.GameContent.Events.DD2Event
       Terraria.GameContent.UI.BigProgressBar.BigProgressBarSystem
       Terraria.GameContent.UI.BigProgressBar.CommonBossBigProgressBar
       Terraria.GameContent.UI.BigProgressBar.EaterOfWorldsProgressBar
       Terraria.GameContent.UI.BigProgressBar.TwinsBigProgressBar
       Terraria.GameContent.UI.BigProgressBar.MoonLordProgressBar
       Terraria.GameContent.UI.BigProgressBar.GolemHeadProgressBar
       Terraria.GameContent.UI.BigProgressBar.MartianSaucerBigProgressBar
       Terraria.GameContent.UI.BigProgressBar.PirateShipBigProgressBar
       Terraria.GameContent.UI.BigProgressBar.BrainOfCthuluBigProgressBar
       Terraria.GameContent.UI.BigProgressBar.DeerclopsBigProgressBar"

command -v ilspycmd >/dev/null 2>&1 || {
    echo "error: ilspycmd not found on PATH (dotnet tool install --global ilspycmd --version 8.0.0.7345)" >&2
    exit 1
}
[ -f "$TML_DLL" ] || { echo "error: tModLoader.dll not found at $TML_DLL (set TML_DLL=...)" >&2; exit 1; }
[ -f "$PATCHER" ] || { echo "error: patcher not found at $PATCHER" >&2; exit 1; }

mkdir -p "$WORK"

# Leaf name of a fully-qualified type — used for the cache filename and the report heading.
leaf() { echo "${1##*.}"; }

# ── 1. Decompile (cached; refresh whenever the dll is newer than our copy) ──
for t in $TYPES; do
    src="$WORK/$(leaf "$t").cs"
    if [ ! -s "$src" ] || [ "$TML_DLL" -nt "$src" ]; then
        echo "decompiling $t ..." >&2
        ilspycmd "$TML_DLL" -t "$t" > "$src" 2>/dev/null
    fi
done

# ── 2. Inventory every NPC-indexed loop, tagged with its enclosing method ──
# Catches the ascending `for (... < 200)` form, the descending `for (... = 199; >= 0)`
# form (the health-bar draw loop was a 199 that the blanket 200-patch never saw),
# and `while`/`do ... while` loops bounded by 200/199.
#
# The while form was added in 0.7.7.1. Chain WALKS are while loops, not for loops --
# `while (num > 0 && num < 200)` following ai[0] down a worm -- and matching only
# `for` hid four of them, including CheckActive_WormSegments, which runs on EVERY
# worm despawn. Above slot 199 it cleans up nothing, the segments are orphaned, and
# they self-delete one at a time through HitEffect(): visible gore, no loot. Do not
# narrow this back to `for`.
#
# The ascending form's INITIALISER was also widened from `= 0` to anything, same
# version and for the same reason: Main.DrawInterface_Healthbar_Worm starts its scan
# at `head.whoAmI + 1`, so demanding `= 0;` reported it as nothing at all.
#
# The lesson both times: this regex defines what the audit can SEE. A loop shape it
# does not match is not a GAP, it is invisible -- exactly as bad as a missing type.
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
                asc  = (line[i] ~ /for \(int [A-Za-z0-9_]+ = [^;]+; [A-Za-z0-9_]+ < 200[;&) ]/)
                desc = (line[i] ~ /for \(int [A-Za-z0-9_]+ = 199; [A-Za-z0-9_]+ >= 0/)
                # Chain walks: `while (num > 0 && num < 200 && ...)`. The bound is often
                # ANDed with other terms and the index is usually re-read from ai[0], so
                # there is no initialiser to anchor on -- match the bound alone.
                wh   = (line[i] ~ /while \(/ && line[i] ~ /(< *200|<= *199)([^0-9]|$)/)
                if (!asc && !desc && !wh) continue
                # A while header carries its own npc[] test (`&& Main.npc[num].active`),
                # so scan it too; a for header never needed it.
                body = wh ? line[i] : ""
                for (j = i + 1; j <= i + 6 && j <= NR; j++) body = body " " line[j]
                if (body !~ /npc\[/) continue   # not an NPC loop (some other 200)
                code = line[i]; gsub(/^[ \t]+|[ \t]+$/, "", code)
                printf "%s\t%s\t%d\t%s\n", TYPE, method, i, code
            }
        }
    ' "$WORK/$1.cs"
}

: > "$WORK/loops.tsv"
for t in $TYPES; do inventory "$(leaf "$t")" >> "$WORK/loops.tsv"; done

# ── 2b. Inventory the NON-loop slot assumptions ──
# Added 0.7.7.1. A loop bound is only the commonest way code assumes 200 slots, not the only one, and the
# others have each cost a real bug:
#
#   SENTINEL  a bare 200/199/201 used as an index, a guard or a "nothing here" marker.
#             `int newNPC = 200;` in NPC.SpawnNPC is the classic: harmless while Main.npc[200] is the inert
#             dummy at the end of a 201-long array, an ordinary occupied enemy once the cap is raised.
#   ARRAY     `new X[200]` / `[201]`. The mod-side version of this (an array sized to the cap at load time,
#             indexed against the cap later) is what stopped ALL spawning with one big content mod.
#   BYTECAST  `(byte)npc.whoAmI`. A byte holds 0..255, so every slot past 255 wraps to a valid-looking one.
#             Found in two large content mods' packet writers; multiplayer only, and unfixable from outside.
#
# These CANNOT be diffed against the patch registry the way loops can — there is no manipulator that "covers
# a sentinel" in general, and whether a given one matters depends on what the number means in context. So
# they are reported as a triage list, not as GAP/PATCHED, and split by whether the line looks slot-related.
# Expect false positives: tile ids, NPC type ids and rand.Next odds all use these numbers too.
shapes() {
    awk -v TYPE="$1" '
        { line[NR] = $0 }
        END {
            method = "(file scope)"
            for (i = 1; i <= NR; i++) {
                if (line[i] ~ /^	(public|private|protected|internal)[^=]*\(/) {
                    m = line[i]; sub(/\(.*/, "", m); sub(/.*[ 	]/, "", m)
                    if (m != "") method = m
                }
                l = line[i]
                if (l ~ /^[ 	]*\/\/IL_/) continue          # decompiler IL-offset comments
                if (l ~ /for \(int [A-Za-z0-9_]+ = 0; [A-Za-z0-9_]+ < 200/) continue   # already in loops.tsv
                if (l ~ /for \(int [A-Za-z0-9_]+ = 199; [A-Za-z0-9_]+ >= 0/) continue

                kind = ""
                if (l ~ /new [A-Za-z0-9_.<>\[\]]*\[(200|201)\]/) kind = "ARRAY"
                else if (l ~ /\(byte\)[^;]*whoAmI/) kind = "BYTECAST"
                else if (l ~ /(^|[^0-9.])(199|200|201)([^0-9.f]|$)/) {
                    # Drop the two loudest false-positive families before they bury the real hits.
                    probe = l
                    gsub(/(rand\.Next|RollLuck|Next)\((199|200|201)\)/, "", probe)
                    if (probe !~ /(^|[^0-9.])(199|200|201)([^0-9.f]|$)/) continue
                    kind = "SENTINEL"
                }
                if (kind == "") continue

                # "slot-ish" if the same line also names an NPC slot or the array itself.
                slotish = (l ~ /npc\[|whoAmI|newNPC|NewNPC\(|\.type ==|maxNPCs/) ? "likely" : "check"
                code = l; gsub(/^[ 	]+|[ 	]+$/, "", code)
                printf "%s\t%s\t%s\t%s\t%d\t%s\n", TYPE, kind, slotish, method, i, code
            }
        }
    ' "$WORK/$1.cs"
}

: > "$WORK/shapes.tsv"
for t in $TYPES; do shapes "$(leaf "$t")" >> "$WORK/shapes.tsv"; done

# ── 3. Parse the patch registry out of EngineILPatcher.cs ──
# Explicit call shapes only — a loose "any quoted string" grep would mark gaps
# as covered, and a false PATCHED is far worse than a false GAP here.
#
# PATCHED must mean "this method's NPC LOOPS were widened", not "this method is hooked for something".
# It used to mean the latter, via a blanket `typeof(X).GetMethod("Y"` grep, and that hid a real bug for
# months: NPC.SpawnNPC is hooked twice (spawn-item modifiers, rare-spawn rolls), neither of which touches
# a loop bound, so both of its 0-199 loops reported as covered. One of them is the Pumpkin/Frost Moon
# miniboss density budget. So an Apply() only counts when its MANIPULATOR is a loop-widening one.
#
# ALLOWLIST, and deliberately so: an unlisted manipulator counts as NOT covering loops, so a new one added
# to the patcher shows up as a GAP until it is named here. That is the safe direction to be wrong in — a
# spurious GAP costs one triage, a spurious PATCHED costs a bug report. Keep in sync with EngineILPatcher.
# NOT loop-widening, on purpose: Patch_RareSpawnRolls, Patch_SpawnItemModifiers, Patch_SpawnNpcSentinel,
# Patch_SingleLiteral200, Patch_LocalImmunityDecrement, Patch_ResetLocalImmunity, Patch_ScaleMoonWave.
LOOP_MANIPULATORS='Patch_NpcLoops|Patch_AllNpcLoopBounds200|Patch_ChaseLoops|Patch_DrawLoop'
LOOP_MANIPULATORS="$LOOP_MANIPULATORS|Patch_HealthBars|Patch_InfoAccessories|Patch_MeleeHitNPCs"
LOOP_MANIPULATORS="$LOOP_MANIPULATORS|Patch_TownNPCCombat|Patch_UpdateLoop"
{
    # PatchMethod / PatchNpcLoops / PatchChaseLoops (mod, typeof(Type), "Name") — all widen loops.
    grep -oE 'Patch(Method|NpcLoops|ChaseLoops)\(mod, typeof\([A-Za-z]+\), "[A-Za-z0-9_]+"' "$PATCHER" \
        | sed -E 's/.*typeof\(([A-Za-z]+)\), "([A-Za-z0-9_]+)"/\1::\2/'
    # Apply(mod, "label", typeof(Type).GetMethod("Name"/nameof(Type.Name) ...), <Manipulator>);
    # Multi-line: accumulate from `Apply(` to the closing `);`, then keep it only if the manipulator qualifies.
    awk -v MANIP="$LOOP_MANIPULATORS" '
        /(^|[^A-Za-z_])Apply\(mod,/ { acc = $0; open = 1; next }
        open { acc = acc " " $0 }
        open && /\);[[:space:]]*$/ {
            open = 0
            if (acc !~ ("(" MANIP ")[[:space:]]*\\)")) next
            if (match(acc, /typeof\([A-Za-z]+\)\.GetMethod\("[A-Za-z0-9_]+"/)) {
                s = substr(acc, RSTART, RLENGTH)
                gsub(/typeof\(|\)\.GetMethod\("|"/, " ", s)
                split(s, p, " +"); print p[2] "::" p[3]
            } else if (match(acc, /typeof\([A-Za-z]+\)\.GetMethod\(nameof\([A-Za-z]+\.[A-Za-z0-9_]+\)/)) {
                s = substr(acc, RSTART, RLENGTH)
                gsub(/typeof\(|\)\.GetMethod\(nameof\(|\)/, " ", s)
                gsub(/\./, " ", s)
                split(s, p, " +"); print p[2] "::" p[4]
            }
        }
    ' "$PATCHER"
    # ChaseLoopAIMethods[] — every entry is a Projectile AI method.
    sed -n '/ChaseLoopAIMethods *=/,/};/p' "$PATCHER" \
        | grep -oE '"[A-Za-z0-9_]+"' | tr -d '"' | sed 's/^/Projectile::/'
    # foreach (string name in new[] { "A", "B" }) ... $"Type.{name}"
    # The DD2Event patches are registered this way. Without this rule they read as GAPs, and a report full of
    # phantom gaps is barely better than no report — you stop trusting the ones that are real.
    awk '
        /foreach *\(string name in new\[\] *\{/ { names = $0; sub(/.*\{/, "", names); pending = 6; next }
        pending > 0 {
            pending--
            if (match($0, /\$"[A-Za-z][A-Za-z0-9_]*\.\{name\}/)) {
                type = substr($0, RSTART + 2, RLENGTH - 9)
                n = split(names, parts, ",")
                for (i = 1; i <= n; i++) {
                    m = parts[i]
                    gsub(/[^A-Za-z0-9_]/, "", m)
                    if (m != "") print type "::" m
                }
                pending = 0
            }
        }
    ' "$PATCHER"
    # ApplyBossHealthBarPatches: reflection over EVERY declared method of each listed type, so the unit of
    # coverage is the type, not the method. Emitted as a `Type::*` wildcard that `covered()` understands.
    sed -n '/string\[\] typeNames *=/,/};/p' "$PATCHER" \
        | grep -oE '"[A-Za-z0-9_]+"' | tr -d '"' | sed 's/$/::*/'
} | sort -u > "$WORK/patched.txt"

# AUDIT-SKIP: Type.Method — reason. Type names may contain digits (DD2Event), which an [A-Za-z]+ class
# silently rejects — the skip then reads as an un-triaged GAP forever.
grep -oE 'AUDIT-SKIP: *[A-Za-z][A-Za-z0-9_]*\.[A-Za-z0-9_]+' "$PATCHER" 2>/dev/null \
    | sed -E 's/AUDIT-SKIP: *([A-Za-z][A-Za-z0-9_]*)\.([A-Za-z0-9_]+)/\1::\2/' | sort -u > "$WORK/skipped.txt" || : > "$WORK/skipped.txt"

# A method is covered either by its own entry or by a `Type::*` wildcard (see ApplyBossHealthBarPatches,
# which patches whole types by reflection rather than naming each method).
covered() {
    grep -qxF "$1" "$WORK/patched.txt" && return 0
    grep -qxF "${1%%::*}::*" "$WORK/patched.txt"
}

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
        covered "$key" && continue
        grep -qxF "$key" "$WORK/skipped.txt" && continue
        gaps=$((gaps + 1))
    done < "$WORK/loops.tsv"

    echo "**$total NPC loops found · $gaps in un-patched methods.**"
    echo

    for t in $TYPES; do
        echo "## $t"
        echo
        printf '| Status | Method | Line | Loop |\n|---|---|---|---|\n'
        awk -F'\t' -v T="$(leaf "$t")" '$1 == T' "$WORK/loops.tsv" | sort -t$'\t' -k2,2 -k3,3n |
        while IFS=$'\t' read -r type method line code; do
            key="$type::$method"
            if grep -qxF "$key" "$WORK/skipped.txt"; then status="SKIP"
            elif covered "$key"; then status="PATCHED"
            else status="**GAP**"; fi
            printf '| %s | `%s` | %s | `%s` |\n' "$status" "$method" "$line" "${code//|/\\|}"
        done
        echo
    done

    # ── Non-loop shapes ──
    echo "## Other slot-assumption shapes"
    echo
    echo "Not loops, so not diffable against the patch registry — **this is a triage list, not a verdict**."
    echo "\`likely\` = the same line also names an NPC slot or \`Main.npc\`. \`check\` = it does not, and is"
    echo "probably a tile id, an NPC type id or a random roll that happens to use the same number."
    echo
    for kind in SENTINEL ARRAY BYTECAST; do
        n=$(awk -F'	' -v K="$kind" '$2 == K' "$WORK/shapes.tsv" | wc -l | tr -d ' ')
        nl=$(awk -F'	' -v K="$kind" '$2 == K && $3 == "likely"' "$WORK/shapes.tsv" | wc -l | tr -d ' ')
        echo "### $kind — $n found, $nl likely"
        echo
        if [ "$nl" -eq 0 ]; then
            echo "_None slot-related._"
            echo
            continue
        fi
        printf '| Type | Method | Line | Code |
|---|---|---|---|
'
        awk -F'	' -v K="$kind" '$2 == K && $3 == "likely"' "$WORK/shapes.tsv" |
        sort -t$'	' -k1,1 -k5,5n |
        while IFS=$'	' read -r type kind2 slotish method line code; do
            printf '| %s | `%s` | %s | `%s` |
' "$type" "$method" "$line" "${code//|/\|}"
        done
        echo
    done
} > "$OUT"

echo "wrote $OUT" >&2
grep -c 'GAP' "$OUT" | sed 's/^/gap rows: /' >&2
