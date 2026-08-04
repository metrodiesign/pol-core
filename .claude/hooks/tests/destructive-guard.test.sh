#!/usr/bin/env bash
# destructive-guard.test.sh — adversarial test สำหรับ destructive guard
# รัน: bash .claude/hooks/tests/destructive-guard.test.sh   (exit 0 = ผ่านครบ)
# logic อยู่ใน .ai/bin/check-destructive.sh; .claude/hooks/destructive-guard.sh เป็น thin
# adapter (jq stdin -> argv). ทุกเคสรัน 2 ทางพิสูจน์ parity: 1) Claude adapter (JSON->stdin)
# 2) ตรง engine (.ai/bin, argv). ตรวจ exit code (2 = block, 0 = allow). ไม่รัน git/rm จริง.
# NOTE: allow-case ของ "push ที่ไม่ใช่ branch-delete" ตัดสินจาก current branch — ทดสอบผ่าน
# check_on_feat (pin cwd = temp repo บน feat/x -> current-branch guard inert) จึง deterministic
# ทุก host ไม่ skip. เคส branch-delete + compound-leak ใช้ check_on_develop (pin cwd บน develop).
# ทั้งสอง fixture รันทั้ง adapter + engine และมี trap เก็บกวาด. check() (cwd=host) ใช้กับกฎที่
# ตัดสินจาก command string ล้วน (ไม่ขึ้นกับ branch).
set -u

HOOK="$(cd "$(dirname "$0")/.." && pwd)/destructive-guard.sh"
ENGINE="$(cd "$(dirname "$0")/../../../.ai/bin" && pwd)/check-destructive.sh"
pass=0
fail=0
skip=0

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

# branch-pinned repo fixtures: temp git repo ที่ checkout branch ไว้ ทำให้ engine เห็น
# current-branch จาก cwd (adapter/engine อ่าน `git branch --show-current`) ไม่ว่า host ยืน branch ไหน
# -> ทุกเคสที่ตัดสินจาก current branch เป็น deterministic ทุก host (skip=0). trap เก็บกวาดตอนจบ.
#   PROT_REPO (develop): ทดสอบ branch-delete + compound-leak (branch-delete span เผลอปลด
#     current-branch guard ให้ push span อื่นในคำสั่งเดียวกัน) ต้องยัง block บน develop
#   FEAT_REPO (feat/x): current-branch guard inert -> allow-case ของ non-delete push
#     (`git push origin feat` ฯลฯ) รันได้จริงแทนการ skip (เดิม LOW#4: allow-path ไม่เคยถูกรันบน develop)
PROT_REPO=$(mktemp -d)
git -C "$PROT_REPO" init -q >/dev/null 2>&1
git -C "$PROT_REPO" checkout -q -b develop >/dev/null 2>&1
FEAT_REPO=$(mktemp -d)
git -C "$FEAT_REPO" init -q >/dev/null 2>&1
git -C "$FEAT_REPO" checkout -q -b feat/x >/dev/null 2>&1
trap 'rm -rf "$PROT_REPO" "$FEAT_REPO"' EXIT

check_on_develop() { # $1=expect(block|allow) $2=desc $3=command — รัน adapter+engine จาก cwd=develop
  local want=2
  [ "$1" = allow ] && want=0

  printf '{"tool_input":{"command":%s}}' "$(printf '%s' "$3" | jq -Rs .)" |
    ( cd "$PROT_REPO" && "$HOOK" ) >/dev/null 2>&1
  local rc_adapter=$?
  if [ "$rc_adapter" -eq "$want" ]; then pass=$((pass + 1)); else
    fail=$((fail + 1)); echo "FAIL [adapter/dev][$1] $2 -> exit $rc_adapter (want $want) :: $3"
  fi

  ( cd "$PROT_REPO" && "$ENGINE" "$3" ) >/dev/null 2>&1
  local rc_engine=$?
  if [ "$rc_engine" -eq "$want" ]; then pass=$((pass + 1)); else
    fail=$((fail + 1)); echo "FAIL [engine/dev][$1] $2 -> exit $rc_engine (want $want) :: $3"
  fi
}

check_on_feat() { # $1=expect(block|allow) $2=desc $3=command — รัน adapter+engine จาก cwd=feat/x
  local want=2
  [ "$1" = allow ] && want=0

  printf '{"tool_input":{"command":%s}}' "$(printf '%s' "$3" | jq -Rs .)" |
    ( cd "$FEAT_REPO" && "$HOOK" ) >/dev/null 2>&1
  local rc_adapter=$?
  if [ "$rc_adapter" -eq "$want" ]; then pass=$((pass + 1)); else
    fail=$((fail + 1)); echo "FAIL [adapter/feat][$1] $2 -> exit $rc_adapter (want $want) :: $3"
  fi

  ( cd "$FEAT_REPO" && "$ENGINE" "$3" ) >/dev/null 2>&1
  local rc_engine=$?
  if [ "$rc_engine" -eq "$want" ]; then pass=$((pass + 1)); else
    fail=$((fail + 1)); echo "FAIL [engine/feat][$1] $2 -> exit $rc_engine (want $want) :: $3"
  fi
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

# push-delete-guard: branch-delete push ถูกยกเว้นเฉพาะ current-branch guard เท่านั้น —
# ref-target guard ยังบล็อก --delete main/develop และ force guard ยังกัน force ทุกกรณี
check block "push --delete develop"      'git push origin --delete develop'
check block "push --delete main"         'git push origin --delete main'
check block "push --delete origin main"  'git push --delete origin main'
check block "push -d develop"            'git push origin -d develop'
check block "push --force + --delete"     'git push --force origin --delete feat'
check block "push -f + --delete"          'git push -f origin --delete feat'

# push-delete-guard rework-1 (audit BLOCKING/HIGH-1): ข้อยกเว้นต้องประเมิน "ต่อ push span" —
# --delete ใน span แรกต้องไม่ปลด current-branch guard ให้ push span อื่นในคำสั่งเดียวกัน.
# รันบน develop-pinned cwd จึง deterministic ทุก branch (skip=0). เดิม (buggy) rc=0 = leak.
DELF="--del""ete"
check_on_develop block "delete && bare push"       "git push origin $DELF chore/foo && git push"
check_on_develop block "delete ; bare push"        "git push origin $DELF chore/foo ; git push"
check_on_develop block "delete && push origin"     "git push origin $DELF chore/foo && git push origin"
check_on_develop block "delete && push origin HEAD" "git push origin $DELF chore/foo && git push origin HEAD"
check_on_develop block "delete -d && push -u HEAD"  "git push origin -d chore/foo && git push -u origin HEAD"
check_on_develop block "delete newline bare push"  "git push origin $DELF chore/foo"$'\n'"git push"
check_on_develop block "delete && push feat"       "git push origin $DELF chore/foo && git push origin feat"
# HIGH-1: -d ที่ git กินเป็นค่าของ option อื่น (--push-option) ไม่ใช่ delete flag -> ต้องไม่ยกเว้น
check_on_develop block "push --push-option -d HEAD" 'git push --push-option -d origin HEAD'
# AC-3 บน develop-pinned: commit ใน compound ยังบล็อกแม้มี branch-delete span
check_on_develop block "delete && commit"          "git push origin $DELF chore/foo && git commit -m x"
check_on_develop block "commit && delete"          "git commit -m x && git push origin $DELF chore/foo"
# rework-2 (audit HIGH): $(...)/backtick ไม่ถูกนับเป็น span boundary ของ [^;&|] จึงกลืน push ที่
# ซ้อนข้างใน — span ที่มี $( หรือ backtick ต้องไม่ถือเป็น branch-delete (single-quote = literal)
check_on_develop block "delete + \$() nested push"  'git push origin --delete chore/foo $(git push)'
check_on_develop block "delete + backtick push"     'git push origin --delete chore/foo `git push`'
check_on_develop block "delete -d + \$() push HEAD"  'git push origin -d chore/foo $(git push origin HEAD)'
# rework-3 (audit MEDIUM): process substitution <(...) >(...) ก็ซ่อน push ได้เหมือน $()/backtick
# — deny-list `[$<>]\(` ครอบทุกรูป nesting ในตำแหน่ง argument (single-quote = literal)
check_on_develop block "delete + <() proc-sub"     'git push origin --delete chore/foo <(git push)'
check_on_develop block "delete + >() proc-sub"     'git push origin --delete chore/foo >(git push)'
# rework-4 (audit LOW#9): bash 5.3 function substitution ${ cmd; } / ${| cmd; } — ; ปิด funsub
# อยู่หลัง token push, span กลืน push ตัวใน; deny-list `\$\{[[:space:]|]` จับ `${ ` / `${|`
check_on_develop block "delete + funsub"           'git push origin --delete chore/foo ${ git push; }'
check_on_develop block "delete + funsub pipe"      'git push origin --delete chore/foo ${| git push; }'
# กัน regression: ตัวแปรธรรมดา / default-expansion (ไม่ใช่ command/function substitution) ต้อง allow
# — ไม่มี '(' ตามหลัง และ ${ ตามด้วยตัวอักษร ไม่ใช่ space/pipe
check_on_develop allow "delete quoted var"         'git push origin --delete "$BRANCH"'
check_on_develop allow "delete braced var"         'git push origin --delete ${BRANCH}'
check_on_develop allow "delete default-expansion"  'git push origin --delete ${BR:-x}'
check_on_develop allow "delete -d bare var"        'git push origin -d $BR'

# --- MUST ALLOW: safe ---
check allow "rm single file"         'rm /tmp/onefile'
# allow ของ non-delete push ตัดสินจาก current branch -> รันจาก feat-pinned cwd (guard inert) จึงได้
# assertion จริงทุก host (เดิม skip บน develop)
check_on_feat allow "push feature branch"    'git push origin feat'
check_on_feat allow "push HEAD:feat"         'git push origin HEAD:feat'
check_on_feat allow "push full refspec"      'git push origin refs/heads/feat:refs/heads/feat'
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
check_on_feat allow "push --all no force"    'git push --all origin'
check_on_feat allow "branch maintenance"     'git push origin maintenance'
# push-delete-guard: branch-delete ของ branch ที่ไม่ใช่ main/develop ต้องผ่านทุก branch — assertion
# จริงบน develop-pinned (ยกเว้นจาก current-branch guard แล้ว) เป็นแกนหลักของ AC-1 (nit 1)
check_on_develop allow "push --delete feature"    'git push origin --delete chore/foo'
check_on_develop allow "push -d feature"          'git push origin -d feature/bar'

echo "---"
echo "pass=$pass fail=$fail skip=$skip"
[ "$fail" -eq 0 ]
