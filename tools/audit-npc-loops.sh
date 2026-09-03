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
# The SECOND registry. Everything patched from MMMultiplayer/ was reported as a GAP for the whole 0.7.7-0.7.8
# line because this script only ever read the engine patcher -- MessageBuffer.GetData, NPC.checkDead,
# VanillaAI_Inner, VanillaHitEffect, AI_121_QueenSlime, SpawnFaelings, SpawnBoss, SpawnNPC,
# WorldGen.TriggerLunarApocalypse and the three DD2 gate spawners all read as unpatched. Worse than the
# inflated count: a method patched in ONE registry and only partly in the other looked identical to one
# nobody had touched.
MP_PATCHER="$REPO_DIR/MMMultiplayer/MultiplayerNetPatcher.cs"
OUT="${1:-$REPO_DIR/tools/audit-report.md}"
WORK="${TMPDIR:-/tmp}/mmm-audit"

# Types holding NPC-indexed loops. Fully-qualified; the leaf name is used for filenames and report headings.
#
# This list has been WRONG TWICE, both times found by a player report rather than by the audit:
#   0.7.6.2  Terraria.Item was missing      -> Copper Town Slime unobtainable (GetPickedUpByMonsters_Special)
#   0.7.6.4  DD2Event was missing            -> Old One's Army triage had to be done by hand
#   0.7.6.6  the whole BigProgressBar namespace was missing -> bosses above slot 200 drew no health bar
#   0.7.7.2  FIFTEEN more types, found by the new FULL_SCAN=1 sweep rather than by a bug report — 35 loops
#            between them, including Mount.CastSuperCartLaser (the Mechanical Cart's laser cannot target
#            anything above slot 199) and the whole WorldGen housing/town family.
# Widen this list before concluding that code is fine. An absent type reads exactly like a clean audit.
# The lesson from the third one: UI namespaces count. A loop that only decides what to draw still goes
# wrong the same way, and "the boss has no health bar" is a louder bug report than most gameplay ones.
# The lesson from the fifth: do not curate this by hand alone. Run FULL_SCAN=1 after every tML update — it
# scans the whole assembly with the same matcher and reports anything this list is missing.
TYPES="Terraria.Player Terraria.Projectile Terraria.Main Terraria.NPC Terraria.Item
       Terraria.Mount Terraria.Collision Terraria.Wiring Terraria.WorldGen
       Terraria.NetMessage Terraria.MessageBuffer
       Terraria.GameContent.Events.DD2Event
       Terraria.GameContent.Events.BirthdayParty
       Terraria.GameContent.Events.LanternNight
       Terraria.Utilities.NPCUtils
       Terraria.GameContent.ShopHelper
       Terraria.GameContent.CoinLossRevengeSystem
       Terraria.GameContent.TeleportPylonsSystem
       Terraria.GameContent.Bestiary.NPCWasNearPlayerTracker
       Terraria.GameContent.Events.ScreenDarkness
       Terraria.GameContent.ObjectInteractions.NPCSmartInteractCandidateProvider
       Terraria.GameContent.UI.EmoteBubble
       Terraria.GameContent.Shaders.WaterShaderData
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
[ -f "$MP_PATCHER" ] || { echo "error: multiplayer patcher not found at $MP_PATCHER" >&2; exit 1; }

mkdir -p "$WORK"

# Leaf name of a fully-qualified type — used for the cache filename and the report heading.
leaf() { echo "${1##*.}"; }

# ── 1. Decompile (cached; refresh whenever the dll is newer than our copy) ──
# A type name that does not resolve makes ilspycmd write NOTHING and exit 0. The cached file is then a
# zero-byte file that every matcher below reads as "no loops here" -- a silently clean audit for a type
# nobody has ever actually scanned. Four entries in TYPES were wrong this way for the whole 0.7.7 line
# (NPCUtils, NPCWasNearPlayerTracker, ScreenDarkness, NPCSmartInteractCandidateProvider all sat under the
# wrong namespace), and the report showed them as fine because it had zero lines to disagree with.
# So: write to a temp file, and only accept it if it has content. Anything else is a hard error.
DECOMPILE_FAILURES=""
for t in $TYPES; do
    src="$WORK/$(leaf "$t").cs"
    if [ ! -s "$src" ] || [ "$TML_DLL" -nt "$src" ]; then
        echo "decompiling $t ..." >&2
        ilspycmd "$TML_DLL" -t "$t" > "$src.tmp" 2>/dev/null
        if [ -s "$src.tmp" ]; then
            mv -f "$src.tmp" "$src"
        else
            rm -f "$src.tmp"
            DECOMPILE_FAILURES="$DECOMPILE_FAILURES $t"
        fi
    fi
done
if [ -n "$DECOMPILE_FAILURES" ]; then
    echo "error: these types produced NO output -- the name is wrong or the type moved:" >&2
    for t in $DECOMPILE_FAILURES; do echo "         $t" >&2; done
    echo "       Fix TYPES before trusting this report; an unresolvable type reads as a clean audit." >&2
    exit 1
fi

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
                # Ascending. Any initialiser, and BOTH `< 200` and `<= 199` — the `<= 199` form was missing
                # until 0.7.7.2 and is a silent hole, not a GAP, exactly like the others below.
                asc  = (line[i] ~ /for \(int [A-Za-z0-9_]+ = [^;]+; [A-Za-z0-9_]+ (< 200|<= 199)[;&) ]/)
                # Descending. `>= 0` and `> -1` are the same loop written two ways.
                desc = (line[i] ~ /for \(int [A-Za-z0-9_]+ = (199|200); [A-Za-z0-9_]+ (>= 0|> -1)/)
                # Chain walks: `while (num > 0 && num < 200 && ...)`. The bound is often
                # ANDed with other terms and the index is usually re-read from ai[0], so
                # there is no initialiser to anchor on -- match the bound alone.
                wh   = (line[i] ~ /while \(/ && line[i] ~ /(< *200f?|<= *199f?)([^0-9]|$)/)
                # Float-typed bound: `for (int num12 = 0; (float)num12 < 200f; num12++)`. The cast and the
                # trailing `f` defeat every regex above, and -- far worse -- the PATCHER cannot see these
                # either: a `200f` is `ldc.r4 200`, and SafeLdcI4 only inspects the ldc.i4 family. So a float
                # bound is not merely un-audited, it is un-blanket-patchable. Matched here so it at least
                # reaches triage; fixing one needs an anchored patch with SafeLdcR4.
                fasc = (line[i] ~ /for \(int [A-Za-z0-9_]+ = [^;]+; \((float|double)\)[A-Za-z0-9_]+ (< *(200f|200\.0)|<= *(199f|199\.0))/)
                if (!asc && !desc && !wh && !fasc) continue
                # A while header carries its own npc[] test (`&& Main.npc[num].active`),
                # so scan it too; a for header never needed it.
                body = wh ? line[i] : ""
                for (j = i + 1; j <= i + 6 && j <= NR; j++) body = body " " line[j]
                # An NPC-slot loop does not have to say `npc[`. perIDStaticNPCImmunity, localNPCImmunity and
                # meleeNPCHitCooldown are all indexed by NPC slot too, and a `< 200` loop over one of them is
                # exactly as broken. Projectile.ResetImmunity hid here for three bug reports: its body reads
                # `perIDStaticNPCImmunity[i][j] = 0u` and nothing else, so the npc[-only filter never saw it.
                if (body !~ /npc\[|perIDStaticNPCImmunity|localNPCImmunity|meleeNPCHitCooldown|lazyNPCOwnedProjectileSearchArray/) continue
                code = line[i]; gsub(/^[ \t]+|[ \t]+$/, "", code)
                printf "%s\t%s\t%d\t%s\n", TYPE, method, i, code
            }
        }
    ' "${2:-$WORK/$1.cs}"
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
#   FLOATGUARD a slot bound written as a FLOAT: `if (ai[1] < 0f || ai[1] > 200f)`. Added 0.7.7.2 and the
#             worst of the four, because it is invisible on BOTH sides. The audit could not see it (every
#             regex above ends before the `f`) and neither can the patcher: `200f` is `ldc.r4 200`, while
#             SafeLdcI4 only inspects the ldc.i4 family, so no blanket patch has ever been capable of
#             touching one. Two were found by hand -- Betsy's breath killing itself, and aiStyle 55 homing
#             refusing to home -- and they were found only because someone read the method.
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
                else if (l ~ /(<|>|<=|>=|==|!=) *(199|200|201)f([^0-9]|$)/) kind = "FLOATGUARD"
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

                # FLOATGUARD needs a different test entirely. A float compared against 200 is USUALLY a
                # distance or a timer -- 72 of them across the four hot types, and most are exactly that --
                # so the same-line npc[ test would rank nearly every real hit as "check". The reliable tell
                # is the two-sided RANGE GUARD, `x >= 0f && x < 200f` (or its inverted early-out
                # `x < 0f || x > 200f`): code asking "is this a valid slot?" and nothing else. Both float
                # slot bugs found so far wore exactly that shape --
                #   Projectile.AI_136_BetsyBreath   `if (ai[1] < 0f || ai[1] > 200f) { Kill(); return; }`
                #   Projectile.VanillaAI aiStyle 55 `if (this.ai[0] >= 0f && this.ai[0] < 200f)`
                # -- and both were found by hand, because nothing here could see them. Failing that shape,
                # a cast-to-int index into Main.npc within the next few lines promotes it to "likely": that
                # is the guard proving what the float was actually for.
                if (kind == "FLOATGUARD") {
                    if (l ~ />= *0f *&& *[^;]*(< *(200|201)f|<= *199f)/ ||
                        l ~ /< *0f *\|\| *[^;]*(> *(199|200)f|>= *(200|201)f)/) slotish = "range"
                    else {
                        slotish = "check"
                        for (k = i; k <= i + 6 && k <= NR; k++)
                            if (line[k] ~ /npc\[\(int\)/ || line[k] ~ /Main\.npc\[[A-Za-z0-9_]*\]/) { slotish = "likely"; break }
                    }
                }
                code = l; gsub(/^[ 	]+|[ 	]+$/, "", code)
                printf "%s\t%s\t%s\t%s\t%d\t%s\n", TYPE, kind, slotish, method, i, code
            }
        }
    ' "${2:-$WORK/$1.cs}"
}

: > "$WORK/shapes.tsv"
for t in $TYPES; do shapes "$(leaf "$t")" >> "$WORK/shapes.tsv"; done

# ── 2c. Whole-assembly sweep (opt-in: FULL_SCAN=1) ──
# The TYPES list above is hand-maintained and has been WRONG four times, every time found by a bug report
# rather than by this script (Item, DD2Event, the BigProgressBar namespace, and Projectile.ResetImmunity's
# enclosing scope). An absent type reads exactly like a clean audit, which is the worst possible failure mode
# for a tool whose whole job is telling you where to look.
#
# FULL_SCAN dumps the ENTIRE assembly once and greps every file for the same loop shapes, then reports only
# hits in types the TYPES list does NOT cover. It is slow (a few minutes, ~1900 files) and it does no registry
# diff -- it cannot tell PATCHED from GAP, only "here is a slot assumption nobody has looked at". Run it after
# every tModLoader update, and whenever a report does not match anything the normal audit knows about.
FULL_DIR="$WORK/full"
if [ "${FULL_SCAN:-0}" = "1" ]; then
    if [ ! -d "$FULL_DIR" ] || [ "$TML_DLL" -nt "$FULL_DIR" ]; then
        echo "FULL_SCAN: decompiling the whole assembly (slow, cached afterwards) ..." >&2
        rm -rf "$FULL_DIR"; mkdir -p "$FULL_DIR"
        ilspycmd "$TML_DLL" -o "$FULL_DIR" -p >/dev/null 2>&1
    fi
    # Reuse the SAME matcher the normal audit uses (body check included) — an independent regex here would
    # drift out of sync and reintroduce exactly the blind spots this section exists to close. Narrow to files
    # that contain a candidate bound first, so this is seconds rather than minutes over ~1900 files.
    KNOWN=$(for t in $TYPES; do leaf "$t"; done | paste -sd'|' -)
    : > "$WORK/fullscan.tsv"
    grep -rlE "(for \(int [A-Za-z0-9_]+ = [^;]+; [A-Za-z0-9_]+ (< 200|<= 199))|(while \([^)]*(< 200|<= 199))" \
         --include=*.cs "$FULL_DIR" 2>/dev/null |
    while IFS= read -r src; do
        leafname=$(basename "$src" .cs)
        # Skip the types the normal audit already diffs against the patch registry.
        case "|$KNOWN|" in *"|$leafname|"*) continue ;; esac
        inventory "$leafname" "$src" >> "$WORK/fullscan.tsv"
    done

    # And sweep the NON-loop shapes over the same files. FULL_SCAN originally ran inventory() only, which
    # left the whole assembly outside TYPES unscanned for float guards -- the one shape neither the audit nor
    # the patcher could see at all. A blind spot inside the tool built to find blind spots.
    #
    # Only FLOATGUARD is kept here. SENTINEL over ~1900 files is tens of thousands of rows of tile ids and
    # random rolls, which would bury the report; the TYPES-scoped run already covers the code that matters
    # for those. Float guards are rare enough to read in full.
    : > "$WORK/fullshapes.tsv"
    grep -rlE "(<|>|<=|>=|==|!=) *(199|200|201)f" --include=*.cs "$FULL_DIR" 2>/dev/null |
    while IFS= read -r src; do
        leafname=$(basename "$src" .cs)
        case "|$KNOWN|" in *"|$leafname|"*) continue ;; esac
        shapes "$leafname" "$src" | awk -F'	' '$2 == "FLOATGUARD" && $3 != "check"' >> "$WORK/fullshapes.tsv"
    done
else
    : > "$WORK/fullscan.tsv"
    : > "$WORK/fullshapes.tsv"
fi

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
# Patch_SingleLiteral200, Patch_ScaleMoonWave.
#
# Patch_LocalImmunityDecrement and Patch_ResetLocalImmunity WERE on that "not loop-widening" list and should
# not have been -- both replace a `ldc.i4 200` loop bound with `localNPCImmunity.Length`. It was harmless
# while the body filter only matched `npc[`, because those loops were invisible to the inventory anyway;
# widening the filter to NPC-slot-indexed arrays turned them into false GAPs. Moved here 0.7.7.1b.
LOOP_MANIPULATORS='Patch_NpcLoops|Patch_AllNpcLoopBounds200|Patch_DrawLoop'   # Patch_ChaseLoops never existed; the real name was Patch_NpcChaseLoops and it was dead code, now removed
LOOP_MANIPULATORS="$LOOP_MANIPULATORS|Patch_HealthBars|Patch_InfoAccessories|Patch_MeleeHitNPCs"
LOOP_MANIPULATORS="$LOOP_MANIPULATORS|Patch_TownNPCCombat|Patch_UpdateLoop"
LOOP_MANIPULATORS="$LOOP_MANIPULATORS|Patch_LocalImmunityDecrement|Patch_ResetLocalImmunity"
LOOP_MANIPULATORS="$LOOP_MANIPULATORS|Patch_CanReleaseNPCs|Patch_DaybreakSpread|Patch_BrainOfGravityGate|Patch_SpawnBossSentinel"
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
            } else if (match(acc, /GetType\("[A-Za-z0-9_.]+"\)[^)]*\?\.GetMethod\("[A-Za-z0-9_]+"/)) {
                # Assembly.GetType("Ns.Sub.Type")?.GetMethod("Name") — types that cannot be named with
                # typeof() because they are not referenced directly. Without this rule they read as GAPs
                # forever, and a report full of phantom gaps is how you stop trusting the real ones.
                s = substr(acc, RSTART, RLENGTH)
                if (match(s, /GetType\("[A-Za-z0-9_.]+"\)/)) {
                    q = substr(s, RSTART + 9, RLENGTH - 11)
                    n = split(q, seg, "."); type = seg[n]
                }
                if (match(s, /GetMethod\("[A-Za-z0-9_]+"/)) {
                    meth = substr(s, RSTART + 11, RLENGTH - 12)
                }
                if (type != "" && meth != "") print type "::" meth
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
    # Reflection-by-predicate: `GetMethods(...).FirstOrDefault(m => m.Name == "X" && m.GetParameters()...)`,
    # used where an overload has to be picked by arity. Without this rule the method reads as a GAP forever
    # even though it is patched -- NPCUtils.SearchForTarget did exactly that. The type comes from a local
    # holding an Assembly.GetType(...) result, so credit it to the leaf name of that string instead.
    #
    # The type comes from EITHER an Assembly.GetType("Ns.Type") string OR a plain typeof(X) -- the latter
    # added because NPC.StrikeNPC has to be resolved this way (it is overloaded, so a bare GetMethod throws)
    # and without it the method read as a GAP while being correctly patched. Both forms reset lastType, which
    # also bounds an older hazard: lastType used to be set ONLY by GetType, so a typeof-based predicate could
    # silently inherit a stale type from an unrelated block far above it and credit the wrong method.
    awk '
        /typeof\([A-Za-z0-9_]+\)/ {
            if (match($0, /typeof\([A-Za-z0-9_]+\)/)) {
                lastType = substr($0, RSTART + 7, RLENGTH - 8)
            }
        }
        /GetType\("[A-Za-z0-9_.]+"\)/ {
            if (match($0, /GetType\("[A-Za-z0-9_.]+"\)/)) {
                q = substr($0, RSTART + 9, RLENGTH - 11)
                n = split(q, seg, "."); lastType = seg[n]
            }
        }
        /m\.Name == "[A-Za-z0-9_]+"/ {
            if (lastType != "" && match($0, /m\.Name == "[A-Za-z0-9_]+"/)) {
                m = substr($0, RSTART + 11, RLENGTH - 12)
                print lastType "::" m
            }
        }
    ' "$PATCHER"

    # ── The multiplayer registry ────────────────────────────────────────────────────────────────────
    # ONLY MessageBuffer.GetData is credited here, and the distinction matters more than it looks.
    #
    # MultiplayerNetPatcher has two registration shapes. Patch_GetData genuinely widens a LOOP (the packet-8
    # join sync, `for (i < 200)` broadcasting each NPC to a joining client), so GetData counts. The tuple
    # array fed to Patch_ServerBroadcastGuards does NOT: it rewrites `netMode == 2 && spawned < 200` broadcast
    # guards, which are not loop bounds at all.
    #
    # Crediting that array cost 33 phantom "fixes" on the first attempt at this: NPC.VanillaAI_Inner is opened
    # by the MP patcher for five netMode guards, and counting it flipped all 29 of its genuinely unpatched
    # NPC loops to PATCHED in one go. A false GAP costs one triage; a false PATCHED costs a bug report, and
    # this report's whole value is that its green column can be trusted.
    grep -q 'MonoModHooks.Modify(getData, Patch_GetData)' "$MP_PATCHER" && echo "MessageBuffer::GetData"
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
    echo "- **PATCHED** — a patch in \`EngineILPatcher.cs\` OR \`MMMultiplayer/MultiplayerNetPatcher.cs\`"
    echo "  targets this method. **Presence, not proof.** It does not mean every loop in the method is covered,"
    echo "  and it cannot tell a working patch from one that silently matches nothing."
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

    # ── Whole-assembly blind-spot sweep (FULL_SCAN=1 only) ──
    if [ -s "$WORK/fullscan.tsv" ]; then
        n=$(wc -l < "$WORK/fullscan.tsv" | tr -d ' ')
        echo "## Whole-assembly sweep (types NOT in the audit's own list)"
        echo
        echo "$n slot-assuming loop(s) found OUTSIDE the types this audit normally scans."
        echo "No PATCHED/GAP verdict here -- this section only answers \"is the TYPES list missing something?\"."
        echo
        printf '| Type | Method | Line | Loop |\n|---|---|---|---|\n'
        sort -t$'\t' -k1,1 -k3,3n "$WORK/fullscan.tsv" |
        while IFS=$'\t' read -r type method line code; do
            printf '| %s | `%s` | %s | `%s` |\n' "$type" "$method" "$line" "${code//|/\|}"
        done
        echo
    fi
    if [ -s "$WORK/fullshapes.tsv" ]; then
        n=$(wc -l < "$WORK/fullshapes.tsv" | tr -d ' ')
        echo "## Whole-assembly sweep - float slot guards (types NOT in the audit's own list)"
        echo
        echo "$n float-typed slot guard(s) outside the types this audit normally scans. Same caveat as above:"
        echo "no PATCHED/GAP verdict, this only answers \"is the TYPES list missing something?\"."
        echo
        printf '| Type | Conf | Method | Line | Code |
|---|---|---|---|---|
'
        sort -t$'	' -k3,3 -k1,1 -k5,5n "$WORK/fullshapes.tsv" |
        while IFS=$'	' read -r type kind slotish method line code; do
            printf '| %s | %s | `%s` | %s | `%s` |
' "$type" "$slotish" "$method" "$line" "${code//|/\|}"
        done
        echo
    fi

    # ── Non-loop shapes ──
    echo "## Other slot-assumption shapes"
    echo
    echo "Not loops, so not diffable against the patch registry — **this is a triage list, not a verdict**."
    echo "\`likely\` = the same line also names an NPC slot or \`Main.npc\`. \`check\` = it does not, and is"
    echo "probably a tile id, an NPC type id or a random roll that happens to use the same number."
    echo
    echo "**Read FLOATGUARD first.** A \`200f\` compiles to \`ldc.r4\`, and the patcher's \`SafeLdcI4\` inspects"
    echo "only the \`ldc.i4\` family — so no blanket patch can reach one of these, ever. They need an anchored"
    echo "patch using \`SafeLdcR4\`. \`range\` = the two-sided \`x >= 0f && x < 200f\` slot-validity shape"
    echo "(near-certain); \`likely\` = a cast-to-int index into \`Main.npc\` follows within a few lines."
    echo
    for kind in FLOATGUARD SENTINEL ARRAY BYTECAST; do
        n=$(awk -F'	' -v K="$kind" '$2 == K' "$WORK/shapes.tsv" | wc -l | tr -d ' ')
        nl=$(awk -F'	' -v K="$kind" '$2 == K && $3 != "check"' "$WORK/shapes.tsv" | wc -l | tr -d ' ')
        echo "### $kind — $n found, $nl worth reading"
        echo
        if [ "$nl" -eq 0 ]; then
            echo "_None slot-related._"
            echo
            continue
        fi
        printf '| Type | Conf | Method | Line | Code |
|---|---|---|---|---|
'
        awk -F'	' -v K="$kind" '$2 == K && $3 != "check"' "$WORK/shapes.tsv" |
        sort -t$'	' -k3,3 -k1,1 -k5,5n |
        while IFS=$'	' read -r type kind2 slotish method line code; do
            printf '| %s | %s | `%s` | %s | `%s` |
' "$type" "$slotish" "$method" "$line" "${code//|/\|}"
        done
        echo
    done
} > "$OUT"

echo "wrote $OUT" >&2
grep -c '^| \*\*GAP\*\*' "$OUT" | sed 's/^/gap rows: /' >&2
