#!/usr/bin/env bash
# destructive-guard.test.sh — adversarial test สำหรับ destructive guard
# รัน: bash .claude/hooks/tests/destructive-guard.test.sh   (exit 0 = ผ่านครบ)
# logic อยู่ใน .ai/bin/check-destructive.sh; .claude/hooks/destructive-guard.sh เป็น thin
# adapter (jq stdin -> argv). ทุกเคสรัน 2 ทางพิสูจน์ parity: 1) Claude adapter (JSON->stdin)
# 2) ตรง engine (.ai/bin, argv). ตรวจ exit code (2 = block, 0 = allow). ไม่รัน git/rm จริง.
# NOTE: เคส "commit/push ขณะอยู่บน main/develop" ขึ้นกับ branch ปัจจุบัน — ไม่ทดสอบที่นี่
# (env-dependent); ทดสอบเฉพาะกฎที่ตัดสินจาก command string.
set -u

HOOK="$(cd "$(dirname "$0")/.." && pwd)/destructive-guard.sh"
ENGINE="$(cd "$(dirname "$0")/../../../.ai/bin" && pwd)/check-destructive.sh"
pass=0
fail=0
skip=0

# branch ปัจจุบัน: ใช้ตัดสินว่าเคส "allow ของ git push" ทดสอบได้หรือไม่ —
# บน main/develop guard บล็อก git push *ทุกตัว* แบบไม่มีเงื่อนไข (engine branch-protection)
# จึงไม่สามารถ exercise allow-push ได้ ตรงกับ contract ใน header (env-dependent ไม่ทดสอบที่นี่)
BR_NOW=$(git branch --show-current 2>/dev/null)

check() { # $1=expect(block|allow) $2=desc $3=command-string
  local want=2
  [ "$1" = allow ] && want=0

  printf '{"tool_input":{"command":%s}}' "$(printf '%s' "$3" | jq -Rs .)" | "$HOOK" >/dev/null 2>&1
  local rc_adapter=$?
  if [ "$rc_adapter" -eq "$want" ]; then pass=$((pass + 1)); else
    fail=$((fail + 1)); echo "FAIL [adapter][$1] $2 -> exit $rc_adapter (want $want) :: $3"
  fi

  "$ENGINE" "$3" >/dev/null 2>&1
  local rc_engine=$?
  if [ "$rc_engine" -eq "$want" ]; then pass=$((pass + 1)); else
    fail=$((fail + 1)); echo "FAIL [engine][$1] $2 -> exit $rc_engine (want $want) :: $3"
  fi
}

# check_allow_push: allow-case ของ git push ที่ env-dependent — ข้าม (skip) เมื่ออยู่บน
# main/develop เพราะ guard จะบล็อก git push ทุกตัวด้วย branch-protection (exit 2) ตามดีไซน์
# มิเช่นนั้นทดสอบเหมือน check allow ปกติ. ตรง contract ใน header (lines 7-8).
check_allow_push() { # $1=desc $2=command-string
  if [ "$BR_NOW" = "main" ] || [ "$BR_NOW" = "develop" ]; then
    skip=$((skip + 2)); echo "SKIP [allow] $1 (branch=$BR_NOW blocks all git push) :: $2"
    return
  fi
  check allow "$1" "$2"
}

# --- MUST BLOCK: destructive ---
check block "rm -rf"                 'rm -rf /tmp/x'
check block "rm -fr"                 'rm -fr /tmp/x'
check block "rm -r -f split"         'rm -r -f /tmp/x'
check block "indented rm -rf"        '   rm -rf /tmp/x'
check block "/bin/rm -rf path"       '/bin/rm -rf /tmp/x'
check block "rtk proxy rm -rf"       'rtk proxy rm -rf /tmp/x'
check block "git reset --hard"       'git reset --hard HEAD~1'
check block "git clean -fd"          'git clean -fd'
check block "find -delete"           'find . -name "*.tmp" -delete'
# issue #30: whole-tree working-copy discard ('.') — block; single-file/branch/unstage pass
check block "git restore whole tree"      'git restore .'
check block "git restore -W whole tree"   'git restore --worktree .'
check block "git restore -SW whole tree"  'git restore --staged --worktree .'
check block "git checkout -- whole tree"  'git checkout -- .'
check block "git checkout dot whole tree" 'git checkout .'
check block "push --force"           'git push --force origin feat'
check block "push --force-with-lease" 'git push --force-with-lease origin feat'
check block "push -f"                'git push -f origin feat'
# regression (review High): '+'-refspec force pushes were silently allowed
check block "push +refspec"          'git push origin +feat:feat'
check block "push +bare-ref"         'git push origin +experimental'
check block "push +develop"          'git push origin +develop'
check block "push +main"             'git push origin +main'
check block "push +HEAD:main"        'git push origin +HEAD:main'
# branch-target protection (command-string based)
check block "push to develop"        'git push origin develop'
check block "push HEAD:main"         'git push origin HEAD:main'
# regression (critic): fully-qualified refspec — '/' before main/develop slipped the anchor
check block "push refs/heads/main"   'git push origin HEAD:refs/heads/main'
check block "push refs/heads/develop" 'git push origin HEAD:refs/heads/develop'
# regression (Codex PR#1): git GLOBAL options before the subcommand must not slip the guard
FORCE="--for""ce"
check block "global -C before push main" 'git -C . push origin main'
check block "global -c before push force" "git -c user.name=x push $FORCE"
check block "global -c before push develop" 'git -c x=y push origin develop'

# regression (review #1/#4/#5): rm recursive+force reachable via backslash / quotes / -c|eval wrapper.
# token อันตรายประกอบ runtime กัน live guard บล็อก command ของ test เอง
RM="r""m"
check block "backslash rm -rf"       "\\${RM} -rf /tmp/x"
check block "double-quoted rm -rf"   "\"${RM}\" -rf /tmp/x"
check block "single-quoted rm -rf"   "'${RM}' -rf /tmp/x"
check block "sh -c rm -rf wrapper"   "sh -c '${RM} -rf /tmp/x'"
check block "bash -c rm -rf wrapper" "bash -c \"${RM} -rf /tmp/x\""
check block "eval rm -rf wrapper"    "eval '${RM} -rf /tmp/x'"

# new (review #10/#16): SQL destructive coverage (CLAUDE.md Destructive Ops rules)
DROP="DR""OP"; TRUNC="TRUN""CATE"; DEL="DELE""TE"
check block "DROP TABLE"             "${DROP} TABLE users"
check block "DROP DATABASE"          "${DROP} DATABASE app"
check block "drop table lowercase"   "drop table users"
check block "TRUNCATE TABLE"         "${TRUNC} TABLE logs"
check block "truncate lowercase"     "truncate logs"
check block "dropdb"                 "dropdb mydb"
check block "DELETE FROM no WHERE"   "${DEL} FROM users"
check block "delete no where lc"     "delete from users"

# new (critic): force-overwrite ของทุก ref
check block "push --mirror"          'git push --mirror origin'
check block "push --all --force"     'git push --all --force origin'
check block "push --force --all"     'git push --force --all origin'
check block "push --all -f"          'git push --all -f origin'

# --- MUST ALLOW: safe ---
check allow "rm single file"         'rm /tmp/onefile'
# allow-push เป็น env-dependent (branch-protection บล็อก git push ทุกตัวบน main/develop) -> skip ที่นั่น
check_allow_push "push feature branch"    'git push origin feat'
check_allow_push "push HEAD:feat"         'git push origin HEAD:feat'
check_allow_push "push full refspec"      'git push origin refs/heads/feat:refs/heads/feat'
check allow "grep -r (not rm)"       'grep -r foo .'
check allow "ls and echo"           'ls && echo ok'
check allow "git status"             'git status'
# issue #30 baselines: narrow whole-tree block must NOT catch normal restore/checkout
check allow "git restore single file"      'git restore src/app.ts'
check allow "git restore --staged unstage" 'git restore --staged .'
check allow "git checkout branch"          'git checkout develop'
check allow "git checkout -- single file"  'git checkout -- src/app.ts'
check allow "git checkout -b new branch"   'git checkout -b feat/x'
# benign baselines for new rules — must NOT false-positive
check allow "DELETE FROM with WHERE" "${DEL} FROM t WHERE id=1"
check allow "delete with where lc"   "delete from t where id=1"
check allow "select drop from menu"  'select drop from menu'
# coreutil truncate (log rotation) — dash-flag after the word -> NOT SQL TRUNCATE, must pass
check allow "truncate -s coreutil"   'truncate -s 0 /tmp/app.log'
check allow "truncate --size coreutil" 'truncate --size=0 /tmp/app.log'
check_allow_push "push --all no force"    'git push --all origin'
check_allow_push "branch maintenance"     'git push origin maintenance'

echo "---"
echo "pass=$pass fail=$fail skip=$skip"
[ "$fail" -eq 0 ]
