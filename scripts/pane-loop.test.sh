#!/usr/bin/env bash
set -u

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
TMP="$(mktemp -d "${TMPDIR:-/tmp}/pane-loop.XXXXXX")" || exit 1
trap 'rm -rf "$TMP"' EXIT
pass=0 fail=0
ok() { pass=$((pass + 1)); }
bad() { fail=$((fail + 1)); echo "FAIL: $1"; }

make_sandbox() {
  local name="$1"
  SANDBOX="$TMP/$name"
  mkdir -p "$SANDBOX/scripts" "$SANDBOX/.ai/specs/fx" "$SANDBOX/bin"
  cp "$ROOT/scripts/pane-loop.sh" "$SANDBOX/scripts/"
  cat >"$SANDBOX/.ai/specs/fx/tasks.md" <<'MD'
- [x] 1. complete task.
MD
  : >"$SANDBOX/osascript.log"
  cat >"$SANDBOX/bin/osascript" <<'SH'
#!/bin/sh
cat >>"$PANE_LOG"
printf 'mock-pane\n'
SH
  cat >"$SANDBOX/bin/sleep" <<'SH'
#!/bin/sh
:
SH
  chmod +x "$SANDBOX/bin/osascript" "$SANDBOX/bin/sleep"
}

# Engine failure is not no-pending success and opens no pane.
make_sandbox engine-fail
cat >"$SANDBOX/bin/python3" <<'SH'
#!/bin/sh
echo 'engine failed' >&2
exit 2
SH
chmod +x "$SANDBOX/bin/python3"
out=$(cd "$SANDBOX" && PATH="$SANDBOX/bin:$PATH" PANELOOP_REEXEC=1 PANE_LOG="$SANDBOX/osascript.log" bash scripts/pane-loop.sh fx 2>&1)
rc=$?
[ "$rc" = 2 ] && ok || bad "engine failure rc=$rc :: $out"
[ ! -s "$SANDBOX/osascript.log" ] && ok || bad "engine failure opened pane"

# Valid zero pending remains success and opens no pane.
make_sandbox no-pending
cat >"$SANDBOX/bin/python3" <<'SH'
#!/bin/sh
exit 0
SH
chmod +x "$SANDBOX/bin/python3"
out=$(cd "$SANDBOX" && PATH="$SANDBOX/bin:$PATH" PANELOOP_REEXEC=1 PANE_LOG="$SANDBOX/osascript.log" bash scripts/pane-loop.sh fx 2>&1)
rc=$?
[ "$rc" = 0 ] && ok || bad "no pending rc=$rc :: $out"
echo "$out" | grep -q 'ไม่มี task ที่จะรัน' && ok || bad "no pending message missing"
[ ! -s "$SANDBOX/osascript.log" ] && ok || bad "no pending opened pane"

# all-in-one with no pending must not dereference the retired GROUPS variable.
make_sandbox all-in-one-no-pending
cat >"$SANDBOX/bin/python3" <<'SH'
#!/bin/sh
exit 0
SH
chmod +x "$SANDBOX/bin/python3"
out=$(cd "$SANDBOX" && PATH="$SANDBOX/bin:$PATH" PANELOOP_REEXEC=1 PANE_LOG="$SANDBOX/osascript.log" bash scripts/pane-loop.sh fx all-in-one 2>&1)
rc=$?
[ "$rc" = 0 ] && ok || bad "all-in-one no pending rc=$rc :: $out"
echo "$out" | grep -q 'ไม่มี task ที่จะรัน' && ok || bad "all-in-one no pending message missing"
[ ! -s "$SANDBOX/osascript.log" ] && ok || bad "all-in-one no pending opened pane"

# A retrospective timeout leaves pane open and never types /clear or /exit.
make_sandbox retro-timeout
cat >"$SANDBOX/bin/python3" <<'SH'
#!/bin/sh
if [ "$1" = "scripts/spec_contract.py" ]; then
  printf '1\n'
  exit 0
fi
exit 0
SH
chmod +x "$SANDBOX/bin/python3"
out=$(cd "$SANDBOX" && PATH="$SANDBOX/bin:$PATH" PANELOOP_REEXEC=1 PANE_LOG="$SANDBOX/osascript.log" bash scripts/pane-loop.sh fx 1 2>&1)
rc=$?
[ "$rc" = 1 ] && ok || bad "retro timeout rc=$rc :: $out"
echo "$out" | grep -q 'retro timeout/failure' && ok || bad "retro timeout message missing"
grep -q '/clear\|/exit' "$SANDBOX/osascript.log" && bad "retro timeout cleared pane" || ok

echo "---"
echo "pass=$pass fail=$fail"
[ "$fail" -eq 0 ]
