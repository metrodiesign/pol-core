#!/usr/bin/env bash
# hook-bypass-guard.test.sh — adversarial test สำหรับ bypass guard
# รัน: bash .claude/hooks/tests/hook-bypass-guard.test.sh   (exit 0 = ผ่านครบ)
# หลัง refactor: logic อยู่ใน .ai/bin/check-bypass.sh; .claude/hooks/hook-bypass-guard.sh
# เป็น thin adapter (jq stdin -> argv). ทุกเคสรัน 2 ทางเพื่อพิสูจน์ parity:
#   1) ผ่าน Claude adapter (JSON payload -> stdin)  2) ตรง engine (.ai/bin, argv)
# ตรวจ exit code (2 = block, 0 = allow). payload เป็นเพียง "ข้อความคำสั่ง" — ไม่รัน git จริง.
set -u

HOOK="$(cd "$(dirname "$0")/.." && pwd)/hook-bypass-guard.sh"
ENGINE="$(cd "$(dirname "$0")/../../../.ai/bin" && pwd)/check-bypass.sh"
pass=0
fail=0

check() { # $1=expect(block|allow) $2=desc $3=command-string
  local want=2
  [ "$1" = allow ] && want=0

  # via Claude adapter: JSON payload on stdin -> adapter -> engine (argv)
  printf '{"tool_input":{"command":%s}}' "$(printf '%s' "$3" | jq -Rs .)" | "$HOOK" >/dev/null 2>&1
  local rc_adapter=$?
  if [ "$rc_adapter" -eq "$want" ]; then
    pass=$((pass + 1))
  else
    fail=$((fail + 1))
    echo "FAIL [adapter][$1] $2 -> exit $rc_adapter (want $want) :: $3"
  fi

  # direct against engine: command as argv (the contract the adapter uses)
  "$ENGINE" "$3" >/dev/null 2>&1
  local rc_engine=$?
  if [ "$rc_engine" -eq "$want" ]; then
    pass=$((pass + 1))
  else
    fail=$((fail + 1))
    echo "FAIL [engine][$1] $2 -> exit $rc_engine (want $want) :: $3"
  fi
}

# --- MUST BLOCK: bypass attempts ---
check block "long --no-verify"          'git commit --no-verify -m x'
check block "short -n"                  'git commit -n -m x'
check block "combined -nm"              'git commit -nm x'
check block "combined -anm"             'git commit -anm x'
check block "-n after other flags"      'git commit -a -n -m "msg"'
check block "core.hooksPath override"   'git -c core.hooksPath=/dev/null commit -m x'
# git config keys are case-insensitive: lowercase/upper variants disable hooks too
check block "core.hookspath lowercase"  'git -c core.hookspath=/dev/null commit -m x'
check block "CORE.HOOKSPATH upper"       'git -c CORE.HOOKSPATH=/dev/null commit -m x'
check block "SECRET_GUARD_SKIP env"     'SECRET_GUARD_SKIP=1 git commit -m x'
# finding #2: a REAL trailing skip-verify flag placed AFTER the quoted message
# (and after a line continuation) must block — the old [^|;&'"] scan stopped at
# the first quote and let it slip
check block "real -n after quoted msg"  'git commit -m "msg -n inside" -n'
check block "real -n single-quoted msg" "git commit -m 'note -n here' -n"
# regression (Codex PR#1): git GLOBAL option before the subcommand must not let -n slip
check block "global -c before commit -n" 'git -c user.name=x commit -n -m msg'
check block "global -C before commit -n" 'git -C . commit -nm msg'
check block "-n after line continuation" 'git commit -m "the message" \
  -n'
# finding #13+#9: guard/floor tamper must block independently of the git token —
# the line-7 short-circuit previously ALLOWed any command lacking a `git` token
check block "chmod -x pre-commit hook"  'chmod -x .githooks/pre-commit'
check block "rm a check-*.sh engine"    'rm -f .ai/bin/check-secrets.sh'
check block "overwrite gate-task.sh"    'mv /tmp/x .ai/bin/gate-task.sh'
check block "git config hooksPath off"  'git config core.hooksPath /dev/null'
# issue #27: WRITE forms of core.hooksPath must still block (value / --unset / --replace-all)
check block "unset hooksPath"           'git config --unset core.hooksPath'
check block "replace-all hooksPath"     'git config --replace-all core.hooksPath /dev/null'
check block "redirect into hook file"   'echo "" > .githooks/pre-commit'
check block "redirect into .git/config" 'printf "[core]" > .git/config'
# unfixed #13: WHOLE-DIRECTORY tamper (no trailing slash) disables the floor as
# effectively as targeting one file inside it — the rule previously keyed on
# '.githooks/' WITH a slash and let the bare-dir form slip entirely. The trailing
# slash is now OPTIONAL so the directory itself is covered.
T="r""m -r .githooks"; printf '%s' "$T" > /tmp/g3-rmdir-githooks
check block "rm -r whole .githooks dir" "$(cat /tmp/g3-rmdir-githooks)"
check block "chmod 000 whole .githooks" 'chmod 000 .githooks'
check block "mv whole .githooks away"   'mv .githooks /tmp/bak'
T="r""m -rf .ai/bin"; printf '%s' "$T" > /tmp/g3-rmrf-aibin
check block "rm -rf whole .ai/bin dir"  "$(cat /tmp/g3-rmrf-aibin)"
# overwrite an engine via cp (guard as DESTINATION, not first operand) must block
check block "cp overwrite a check engine" 'cp /tmp/evil .ai/bin/check-bypass.sh'

# --- MUST ALLOW: legit commits / non-commit git ---
check allow "plain commit"              'git commit -m "normal message"'
# regression (bug fixed): ' -n' inside the quoted commit message must NOT block
check allow "-n inside message"         'git commit -m "fix -n flag handling"'
check allow "-n word mid-message"       'git commit -m "document the -n behavior"'
# finding #2 baseline: a message that merely talks about -n, with NO real
# trailing skip-verify flag, must still pass (no false positive)
check allow "note about -n flag"        "git commit -m 'note about -n flag'"
check allow "git status"                'git status'
check allow "git grep -n"               'git grep -n foo'
# issue #27: read-only core.hooksPath queries are harmless and must NOT block
check allow "read hooksPath bare"       'git config core.hooksPath'
check allow "read hooksPath --get"      'git config --get core.hooksPath'
# issue #28: a literal newline INSIDE the quoted message with a -n word must NOT block
check allow "newline in quoted -n word" "$(printf 'git commit -m "x\n  -n word"')"
# finding #13+#9 baseline: benign chmod / file ops outside the guard set pass
check allow "benign chmod app.ts"       'chmod +x src/app.ts'
check allow "benign rm build artifact"  'rm -f build/output.js'
check allow "mv a non-guard .ai file"   'mv .ai/bin/README.md docs/'
# new-false-positive baseline (G3-bypass): copying a guard file OUT as a
# read-only backup (guard path is the SOURCE / first operand) must NOT block —
# a responsible backup-before-edit is allowed; only guard-as-DESTINATION blocks.
check allow "cp hook OUT as backup"     'cp .githooks/pre-commit /tmp/backup-hook'
check allow "cp hook to .bak sibling"   'cp .githooks/pre-commit pre-commit.bak'
# bare-dir rule must not over-match a benign path that merely starts the same
check allow "rm a .githooks-named file" 'rm -f .githooks-notes.txt'

echo "---"
echo "pass=$pass fail=$fail"
[ "$fail" -eq 0 ]
