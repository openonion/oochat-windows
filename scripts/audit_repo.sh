#!/usr/bin/env bash
# audit_repo.sh — pre-publication audit for repos inherited from student projects.
#
# Usage:  ./audit_repo.sh <repo-path>
# Exit:   0 = no blocking findings, 1 = at least one BLOCK finding, 2 = bad usage.
#
# Every finding prints as  file:line: <the line>  so it can be acted on directly.
# Tiers: BLOCK (must clear before publishing) / CLEAN (should clear) / REVIEW (human decides).
#
# Portable to bash 3.2 (macOS default) on purpose: no namerefs, no associative arrays.
# The first version used `local -n` and printed an empty report while still exiting
# non-zero — a check that says BLOCKED and shows nothing is worse than no check.

set -uo pipefail

REPO="${1:-}"
[ -d "$REPO" ] || { echo "usage: $0 <repo-path>" >&2; exit 2; }
cd "$REPO" || exit 2

# `grep` is a shell function in some environments (a ugrep shim with
# --ignore-files). That shim honours .gitignore, so it silently skips files that
# are ignored by a rule yet still tracked by git — exactly the files that ship
# without being audited. Pin the real binary.
GREP=$(command -v /usr/bin/grep || command -v /bin/grep) || {
  echo "no system grep found" >&2; exit 2; }

TMP=$(mktemp -d) || exit 2
trap 'rm -rf "$TMP"' EXIT
: > "$TMP/block"; : > "$TMP/clean"; : > "$TMP/review"; : > "$TMP/waived"
BLOCK=0

# Optional per-repo waivers: a .audit-waivers file, one entry per line:
#     <path-prefix>   # <reason>
#
# A blanket rule that a whole product cannot satisfy gets bypassed, and a
# bypassed gate protects nothing. So: waivers are per-repo and explicit, a line
# without a "# reason" is ignored on purpose, and waived hits are still PRINTED
# in their own section — they stop blocking, they do not disappear.
: > "$TMP/waivepaths"
if [ -f .audit-waivers ]; then
  while IFS= read -r line; do
    case "$line" in
      ''|'#'*)  continue ;;
      *'#'*)    _w="${line%%#*}"
                _wpath=$(printf '%s' "$_w" | awk '{print $1}')
                _wrule=$(printf '%s' "$_w" | awk '{print $2}')
                # Both fields are required. A path-only waiver would silently hide
                # every other rule on that path — which is how a docs/ waiver added
                # for loopback URLs also buried leftover coursework wording.
                [ -n "$_wpath" ] && [ -n "$_wrule" ] &&
                  printf '%s\t%s\n' "$_wpath" "$_wrule" >> "$TMP/waivepaths" ;;
    esac
  done < .audit-waivers
fi

# Directories that are never ours to clean, and binaries grep would garble.
PRUNE=(--exclude-dir=.git --exclude-dir=node_modules --exclude-dir=build
       --exclude-dir=.gradle --exclude-dir=Pods --exclude-dir=DerivedData
       --exclude-dir=dist --exclude-dir=.next --exclude-dir=vendor
       --exclude-dir=__pycache__ --exclude-dir=.venv --exclude-dir=screenshots
       --exclude=*.png --exclude=*.jpg --exclude=*.jpeg --exclude=*.gif
       --exclude=*.svg --exclude=*.pdf --exclude=*.zip --exclude=*.jar
       --exclude=*.ico --exclude=*.webm --exclude=*.mp4 --exclude=*.webp
       --exclude=*.lock --exclude=*-lock.json --exclude=*.bin
       --exclude=.audit-waivers --exclude=audit_repo.sh)

# Cap per-check output. A check that dumps 4000 lines is not actionable, but the
# true count still has to be reported or a truncated list reads as the whole list.
MAX_HITS=40

# scan <tier> <label> <extended-regex> [extra grep args...]
scan() {
  tier="$1"; label="$2"; pattern="$3"; shift 3
  hits=$("$GREP" -rInE "$pattern" . "${PRUNE[@]}" "$@" 2>/dev/null | sed 's|^\./||')
  [ -z "$hits" ] && return 0

  # Via a file, not `printf | head`: head closes the pipe on the Nth line and the
  # SIGPIPE that follows killed the run before it printed any report at all.
  printf '%s\n' "$hits" > "$TMP/hits.all"

  # Split off anything a waiver covers. Waived hits go to their own section.
  : > "$TMP/hits"
  while IFS= read -r h; do
    hf="${h%%:*}"; keep=1
    while IFS="$(printf '\t')" read -r wp wr; do
      [ -n "$wp" ] || continue
      case "$hf" in "$wp"*) ;; *) continue ;; esac
      case "$label" in *"$wr"*) keep=0; break ;; esac
    done < "$TMP/waivepaths"
    if [ "$keep" = 1 ]; then echo "$h" >> "$TMP/hits"
    else echo "[$label] $h" >> "$TMP/waived"; fi
  done < "$TMP/hits.all"

  [ -s "$TMP/hits" ] || return 0
  n=$(wc -l < "$TMP/hits" | tr -d ' ')

  case "$tier" in
    BLOCK)  out="$TMP/block"; BLOCK=1 ;;
    CLEAN)  out="$TMP/clean" ;;
    REVIEW) out="$TMP/review" ;;
  esac

  {
    echo "### $label — $n hit(s)"
    head -n "$MAX_HITS" "$TMP/hits"
    [ "$n" -gt "$MAX_HITS" ] && echo "    … $((n - MAX_HITS)) more not shown (total $n)"
    echo
  } >> "$out"
}

note() {  # note <tier> <label> <file:line: text>
  case "$1" in
    BLOCK)  out="$TMP/block"; BLOCK=1 ;;
    CLEAN)  out="$TMP/clean" ;;
    REVIEW) out="$TMP/review" ;;
  esac
  { echo "### $2 — 1 hit(s)"; echo "$3"; echo; } >> "$out"
}

# ---------------------------------------------------------------- 1. personal info
scan BLOCK "Student zID"        'z[0-9]{7}'
scan BLOCK "Student email"      '[A-Za-z0-9._%+-]+@student\.unsw\.edu\.au'
scan BLOCK "UNSW staff/AD email" '[A-Za-z0-9._%+-]+@(ad\.)?unsw\.edu\.au'

# ---------------------------------------------------------------- 2. course / team traces
scan BLOCK "Course code"        'COMP[- ]?(3900|9900)|comp(3900|9900)'
# Group codes are letter-2digits-letter. Anchored to the contexts they actually
# appear in (package names, paths, doc prose) — a bare \b[A-Za-z][0-9]{2}[A-Za-z]\b
# also matches hashes, gradle versions and colour tokens, which buries the signal.
scan BLOCK "Group code in package/path" '(^|[/.[:space:]"'"'"'])(9900|3900)?[-_]?[A-Za-z][0-9]{2}[A-Za-z]([/._[:space:]"'"'"']|$)'
scan CLEAN "Team name"          '\b(cake|donut|banana|bread)\b' -i
scan CLEAN "Coursework vocabulary" '\b(sprint|retrospective|marking|rubric|tutor|assignment|deliverable|capstone|standup|scrum)\b' -i
scan CLEAN "Classroom repo naming" 'capstone-project-[0-9]{2}t[0-9]'

# ---------------------------------------------------------------- 3. original GitHub traces
# A .git directory is not itself a problem — every CI checkout has one, and an
# unconditional block there is why the first CI wiring needed continue-on-error,
# which turns the gate off entirely. What matters is whose history it is.
if [ -d .git ] && command -v git >/dev/null 2>&1; then
  foreign=$(git log --format='%ae%n%ce' 2>/dev/null | sort -u |
            "$GREP" -vE '(@openonion\.ai|@users\.noreply\.github\.com)$' || true)
  if [ -n "$foreign" ]; then
    while IFS= read -r a; do
      [ -n "$a" ] && note BLOCK "Inherited author in git history" ".git:0: $a"
    done <<EOF
$foreign
EOF
  fi
fi

scan BLOCK "Original org/owner in URL" 'github\.com[/:]((UNSW|unsw)[A-Za-z0-9._-]*)'
# Every github.com URL that is not ours. The UNSW-prefixed rule above misses the
# common case: a contributor's personal fork, so the human-review rule covers
# every owner and the waiver list records known upstream dependencies.
#
# REVIEW rather than BLOCK, and no negative lookahead: BSD grep has no -P, and
# legitimate upstream links (gradle, roborazzi) live in every Android repo. A
# rule that reddens on gradlew would be bypassed within a day, which is worse
# than a rule that asks a human to skim a short list.
scan REVIEW "github.com URL that is not openonion" 'github\.com[/:][A-Za-z0-9._-]+/[A-Za-z0-9._-]+' --exclude=gradlew --exclude=gradlew.bat
scan REVIEW "CI badge" '!\[[^]]*\]\([^)]*(actions/workflows|badge)[^)]*\)'

for f in TUTOR.md HANDOVER.md MIRROR_TEST.md CODEOWNERS .github/CODEOWNERS; do
  [ -f "$f" ] && note CLEAN "Coursework/ownership artefact" "$f:1: file exists"
done

# ---------------------------------------------------------------- 4. secrets & endpoints
scan BLOCK "API-key-shaped string" '(sk-[A-Za-z0-9]{20,}|ghp_[A-Za-z0-9]{30,}|gho_[A-Za-z0-9]{30,}|AKIA[0-9A-Z]{16}|AIza[0-9A-Za-z_-]{30,})'
# Our own key format. Found in a screenshot test on the first dirty run, and the
# generic patterns above did not match it — a leak of ours is the one we would
# least like to learn about from a third party.
scan BLOCK "OpenOnion key format" 'oo_(live|test)_[A-Za-z0-9]{8,}'
scan BLOCK "Private key block"     'BEGIN [A-Z ]*PRIVATE KEY'
# Hardcoded dev endpoints decide whether "take it and change it" is actually true.
scan BLOCK "Hardcoded local endpoint" '(localhost|127\.0\.0\.1|10\.[0-9]{1,3}\.[0-9]{1,3}\.[0-9]{1,3}|192\.168\.[0-9]{1,3}\.[0-9]{1,3})(:[0-9]+)?'
scan REVIEW "Password/secret assignment" '(password|passwd|secret|api_?key|token)[[:space:]]*[:=][[:space:]]*["'"'"'][^"'"'"']{4,}' -i

for f in .env .env.local .env.production; do
  [ -f "$f" ] && note BLOCK "Committed env file" "$f:1: file exists"
done

# Text scans cannot recognize binary private keys, and ignored files may still
# be tracked. Block runtime state and credential-shaped filenames explicitly.
if command -v git >/dev/null 2>&1; then
  sensitive_files=$(git ls-files | "$GREP" -Ei \
    '(^|/)(\.runtime|\.co)(/|$)|(^|/)\.env($|\.)|\.(p12|p8|mobileprovision|jks|keystore|pem|key|log)$' | \
    "$GREP" -Ev '(^|/)\.env\.example$' || true)
  if [ -n "$sensitive_files" ]; then
    while IFS= read -r f; do
      [ -n "$f" ] && note BLOCK "Tracked runtime or credential file" "$f:1: tracked by git"
    done <<EOF
$sensitive_files
EOF
  fi
fi

# ---------------------------------------------------------------- 5. our branding
scan REVIEW "Bundle id / package name" '^[[:space:]]*(applicationId|PRODUCT_BUNDLE_IDENTIFIER|namespace)[[:space:]]*[=:]'
scan REVIEW "Copyright line" 'Copyright (\(c\)|©)'

# ---------------------------------------------------------------- report
echo "==============================================================="
echo " Pre-publication audit: $(pwd)"
echo " $(date -u '+%Y-%m-%dT%H:%M:%SZ')"
echo "==============================================================="

emit() {  # emit <file> <header>
  echo
  if [ -s "$1" ]; then
    echo "$2"
    echo "---------------------------------------------------------------"
    cat "$1"
  else
    echo "$2: none"
  fi
}

emit "$TMP/block"  "🔴 MUST CLEAR — blocks publication"
emit "$TMP/clean"  "🟡 SHOULD CLEAR"
emit "$TMP/review" "🔵 NEEDS A HUMAN — do not auto-strip"
emit "$TMP/waived" "⚪ WAIVED by .audit-waivers — reviewed, not blocking"

cat <<'LIMITS'

WHAT THIS SCRIPT CANNOT SEE — a clean run is not a clearance
---------------------------------------------------------------
1. Real names with no zID or email beside them. This script found the
   students' names only because they sat on the same line as their zID.
   A name on its own is invisible to it. Needs a human or an AI pass.
2. Anything inside .git history — the scan covers the working tree only.
   Author names and emails live in commit metadata regardless of how
   clean the files look. Check that .git was actually dropped.
3. Anything in an excluded binary: images, PDFs, jars, screenshots.
   A team photo or a marked-up PDF passes this check silently.
4. Judgement calls. Hits under NEEDS A HUMAN are reported, never
   auto-resolved, and a BLOCK on a test fixture still blocks — waive it
   deliberately rather than teaching the script to guess.
LIMITS

echo
echo "==============================================================="
if [ "$BLOCK" -eq 1 ]; then
  echo " RESULT: BLOCKED — clear the red section before publishing."
else
  echo " RESULT: no blocking findings (see limits above — not a clearance)."
fi
echo "==============================================================="
exit "$BLOCK"
