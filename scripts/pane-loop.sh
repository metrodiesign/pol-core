#!/usr/bin/env bash
#
# pane-loop.sh — automate, with a REAL interactive TUI (no headless `claude -p`):
# 1 cohesive task = 1 fresh iTerm pane running an interactive `claude` session.
# The controller drives that session by typing into it:
#     /spec-implement N   → (wait until task N is marked [x] in tasks.md)
#     /spec-retro         → (wait for the retrospective to be written)
#     /clear              → reset, then quit → pane closes
# Then the next task opens a brand-new pane. Sequential (tasks share the tree).
#
# Usage: scripts/pane-loop.sh [feature-name] [all-in-one | task-ids...]
#   all-in-one: ALL pending tasks in ONE pane/session (implement each in turn, then a
#               SINGLE /spec-retro + /clear). CHEAPEST when tasks are tightly coupled
#               (shared primitives/data/lib): context is acquired once and reused via
#               cache-read (~10x cheaper) instead of re-acquired per session. Measured
#               (my-feature): whole feature 1 session ~$16 vs 7 sessions ~$21.
#               Trade-off: a long context can drift (accuracy) — pick by COUPLING.
#               e.g.:  scripts/pane-loop.sh my-feature all-in-one
#   task-ids: space-separated → each its own pane (fresh session per task; most accurate).
#             join with '+' to BATCH several into ONE pane/session → run each
#             /spec-implement in turn, then a SINGLE /spec-retro + /clear. Few-session
#             middle ground for coupled-but-distinct slices (e.g. logic+data, assemble+audit).
#             e.g.:  scripts/pane-loop.sh my-feature 1 2+3 4+5 6+7
#   (no args): every pending task, auto-grouped by `Batch:` tags in tasks.md.
#
# Env:
#   CLAUDE_FLAGS  flags for the interactive claude (default: none).
#                   claude doesn't support --dangerously-skip-permissions (Claude Code flag).
#                   Permissions are handled via project config / policies.
#                   Fallback: CLAUDE_FLAGS (backward compat).
#   STEP_TIMEOUT  max seconds to wait for one task's implement step (default 2400).
#
set -uo pipefail

# macOS /bin/bash is 3.2.57 (no newer system bash); it mis-captures the `$(python3 ...)`
# group parse below, collapsing the task-id list to a single bogus token -> the orchestrator
# types `/spec-implement <garbage>` and stalls. Re-exec under zsh with bash-style word-splitting:
# zsh captures the command-substitution correctly AND `-o shwordsplit` keeps `for x in $GROUPS`
# splitting on spaces the way this script (written for bash) expects. Idempotent via the flag.
if [[ -z "${PANELOOP_REEXEC:-}" ]]; then
  export PANELOOP_REEXEC=1
  if command -v zsh >/dev/null 2>&1; then exec zsh -o shwordsplit "$0" "$@"; fi
fi

REPO="$(cd "$(dirname "$0")/.." && pwd)"
FEATURE="${1:-}"
SPECS_DIR=".ai/specs"
CLAUDE_FLAGS="${CLAUDE_FLAGS-}"
STEP_TIMEOUT="${STEP_TIMEOUT:-2400}"

cd "$REPO"

if [[ -z "$FEATURE" ]]; then
  count=0; found=""
  for d in "$SPECS_DIR"/*/; do [[ -d "$d" ]] || continue; count=$((count+1)); found="$(basename "$d")"; done
  [[ "$count" -eq 1 ]] || { echo "ระบุ feature: $0 <feature-name> (พบ $count specs)" >&2; exit 1; }
  FEATURE="$found"
fi

TASKS="$SPECS_DIR/$FEATURE/tasks.md"
RETRO_DIR="$SPECS_DIR/$FEATURE/retrospectives"
[[ -f "$TASKS" ]] || { echo "ไม่พบ $TASKS" >&2; exit 1; }

# เลือก task: arg หลัง feature = group แบบ manual (วิธี 2); ไม่มี args = ทุก task ค้าง
# auto-group ตาม `Batch:` tag ใน tasks.md (วิธี 1). 1 group = 1 pane/session. id คั่น
# ด้วย '+' ในกลุ่มเดียว = batch (1 retro/1 clear). manual args ทับ Batch tag เสมอ.
shift || true   # ทิ้ง $1 (feature) ถ้ามี — เหลือ group args ใน $@

# โหมดเลือกตาม COUPLING ของ feature (arg ตัวแรกหลัง feature):
#   all-in-one  → ทุก pending task รวมใน 1 pane/session เดียว (implement ไล่ทุก task แล้ว
#                 1 retro + 1 clear). ถูกสุดเมื่อ task พึ่งกันหนัก (shared primitives/data/lib):
#                 ได้ context ครั้งเดียว re-read ผ่าน cache-read (~10x ถูก) ไม่ re-acquire ซ้ำ.
#                 วัดจริง my-feature: 1 session ~$16 vs แยก 7 session ~$21 (~30% แพงกว่า).
#                 แลก: context ยาวอาจ drift (accuracy) — ใช้เมื่อ coupled + งานไม่ใหญ่จนล้น context.
#   (default)   → 1 task = 1 pane (fresh context/task, แม่นกว่า) เว้นแต่ Batch: tag / '+' arg.
ALLINONE=""
if [[ "${1:-}" == "all-in-one" || "${1:-}" == "--all-in-one" ]]; then ALLINONE=1; shift; fi

GROUPS=""       # space-separated groups; id ในกลุ่มคั่นด้วย '+'
if [[ -n "$ALLINONE" ]]; then
  # ทุก pending task เรียงตามไฟล์จาก shared engine (REQ-4.1: string IDs, file order)
  GROUPS="$(python3 scripts/spec_contract.py task-ids --feature "$FEATURE" --pending | paste -sd+ -)"
  [[ -n "$GROUPS" ]] && echo "โหลด: all-in-one (1 session สำหรับทุก pending task)"
elif [[ $# -gt 0 ]]; then
  # manual args: validate against the ENGINE's exact string ID sets (REQ-4.4)
  mapfile -t PENDING_IDS < <(python3 scripts/spec_contract.py task-ids --feature "$FEATURE" --pending)
  declare -A PENDING_SET=()
  for pid in "${PENDING_IDS[@]}"; do PENDING_SET["$pid"]=1; done
  for grp in "$@"; do
    members=""
    for id in ${grp//+/ }; do
      if [[ -z "${PENDING_SET[$id]:-}" ]]; then
        grep -qF -- "- [x] $id." "$TASKS" \
          && echo "!!! task $id = [x] อยู่แล้ว — ข้าม" >&2 \
          || echo "!!! task $id ไม่ใช่ pending/ไม่พบ — ข้าม" >&2
        continue
      fi
      members="${members:+$members+}$id"
    done
    [[ -n "$members" ]] && GROUPS="$GROUPS $members"
  done
else
  # default: pending task ทุกตัว — IDs จาก shared engine, auto-group ด้วย `Batch:` tag
  # (tag เดียวกัน = group เดียว). Batch scan จับคู่ ID แบบ exact-string ตาม file order.
  GROUPS="$(python3 - "$FEATURE" <<'PY'
import subprocess, sys, re
feature = sys.argv[1]
ids = subprocess.run(
    ["python3", "scripts/spec_contract.py", "task-ids", "--feature", feature, "--pending"],
    capture_output=True, text=True,
).stdout.split()
lines = open(f".ai/specs/{feature}/tasks.md").read().splitlines()
batch = {}
opening = re.compile(r"^- \[([ x])\] ([A-Za-z0-9][A-Za-z0-9_.\-]*)\.")
tag_re = re.compile(r"Batch:\s*([A-Za-z0-9_-]+)")
for ln in lines:
    m = opening.match(ln)
    if not m:
        continue
    tid = m.group(2)
    mt = tag_re.search(ln)
    if mt and tid in ids:
        batch[tid] = mt.group(1)
groups, seen = [], {}
for idn in ids:
    tag = batch.get(idn)
    if tag is None:
        groups.append([idn])
    elif tag in seen:
        groups[seen[tag]].append(idn)
    else:
        seen[tag] = len(groups); groups.append([idn])
print(" ".join("+".join(g) for g in groups))
PY
)"
fi
[[ -n "$GROUPS" ]] || { echo "ไม่มี task ที่จะรันใน $FEATURE"; exit 0; }
echo "Feature: $FEATURE | groups: $GROUPS"

# --- iTerm helpers (operate on a session by id) -----------------------------
open_pane() {  # -> echoes new session id ; opens interactive claude in a split
  local cmd="cd '$REPO' && claude $CLAUDE_FLAGS"
  osascript <<APPLESCRIPT
tell application "iTerm2"
  tell current session of current window
    set s to (split vertically with default profile command "/bin/zsh -lc \"$cmd\"")
  end tell
  return id of s
end tell
APPLESCRIPT
}

send_text() {  # $1=session id  $2=text (typed + Enter)
  osascript <<APPLESCRIPT
tell application "iTerm2"
  repeat with w in windows
    repeat with t in tabs of w
      repeat with se in sessions of t
        if (id of se) is "$1" then tell se to write text "$2"
      end repeat
    end repeat
  end repeat
end tell
APPLESCRIPT
}

close_pane() {  # $1=session id
  osascript <<APPLESCRIPT
tell application "iTerm2"
  repeat with w in windows
    repeat with t in tabs of w
      repeat with se in sessions of t
        if (id of se) is "$1" then tell se to close
      end repeat
    end repeat
  end repeat
end tell
APPLESCRIPT
}

task_done() { grep -qF -- "- [x] $1." "$TASKS"; }          # task $1 marked [x]?

# Retro completion = retrospective artifact bytes changed (REQ-4 / design §509-512):
# sorted sha256 inventory of retrospectives/**/*.md captured before /spec-retro;
# success when a new path appears OR a hash changes. Git HEAD is irrelevant.
retro_inventory() {  # -> "sha256:path" sorted lines
  python3 - <<'PY'
import glob, hashlib, os
rows = []
for p in glob.glob("retrospectives/**/*.md", recursive=True):
    if os.path.isfile(p):
        rows.append(hashlib.sha256(open(p, "rb").read()).hexdigest() + ":" + os.path.relpath(p))
print("\n".join(sorted(rows)))
PY
}

retro_artifact_changed() { # $1=baseline snapshot file ; compares current inventory
  if [[ ! -s "$1" ]]; then          # empty baseline -> any artifact counts as change
    [[ -n "$(retro_inventory)" ]]
    return
  fi
  [[ "$(retro_inventory)" != "$(cat "$1")" ]]
}

wait_for() {  # $1=predicate-cmd-string  $2=timeout-s  -> 0 ok / 1 timeout
  local waited=0
  until eval "$1"; do
    sleep 5; waited=$((waited+5))
    [[ $waited -ge $2 ]] && return 1
  done
  return 0
}

# --- main loop --------------------------------------------------------------
# 1 group = 1 pane/session. implement ทุก id ในกลุ่มก่อน แล้ว retro+clear ครั้งเดียว
# (batch → cache-write เย็นจ่ายรอบเดียวต่อกลุ่ม แทน 1 รอบ/task).
for group in $GROUPS; do
  ids="${group//+/ }"
  echo "::: group [$ids] — เปิด pane interactive"
  SID="$(open_pane | tr -d '[:space:]')"
  [[ -n "$SID" ]] || { echo "!!! เปิด pane ไม่สำเร็จ"; exit 1; }
  sleep 12   # รอ claude TUI บูต

  RETRO_SNAP="$(mktemp "${TMPDIR:-/tmp}/sdd-retro-snap.XXXXXX")"
  trap 'rm -f "$RETRO_SNAP"' EXIT
  retro_inventory >"$RETRO_SNAP"

  for id in $ids; do
    echo "::: task $id — พิมพ์ /spec-implement $id"
    send_text "$SID" "/spec-implement $id"
    if ! wait_for "task_done $id" "$STEP_TIMEOUT"; then
      echo "!!! task $id ไม่ขึ้น [x] ใน ${STEP_TIMEOUT}s — หยุด (pane เปิดไว้ให้ตรวจ)"
      exit 1
    fi
    echo "::: task $id — [x] แล้ว"
  done

  echo "::: group [$ids] — /spec-retro (รวมทุก task ในกลุ่ม)"
  send_text "$SID" "/spec-retro"

  # retro success = retrospective artifact bytes changed (แผน Task 5; ไม่อ่าน HEAD,
  # ไม่ commit เอง — commit ผูกกับ human Ship flow). no-op session → timeout แล้วไปต่อ.
  wait_for "retro_artifact_changed '$RETRO_SNAP'" 360 \
    && echo "::: group [$ids] — retro artifact เขียนแล้ว" \
    || echo "::: group [$ids] — retro timeout (ไปต่อ)"

  sleep 6   # ให้ agent จบ turn ก่อนพิมพ์ /clear (กัน race)
  echo "::: group [$ids] — /clear แล้วปิด pane"
  send_text "$SID" "/clear"
  sleep 3
  send_text "$SID" "/exit"
  sleep 2
  close_pane "$SID" >/dev/null 2>&1 || true
done

echo "เสร็จทุก group ของ $FEATURE"
