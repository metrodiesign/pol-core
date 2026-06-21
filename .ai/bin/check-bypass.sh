#!/usr/bin/env bash
# hook-bypass-guard.sh — กันการข้าม pre-commit secret-guard (PreToolUse: Bash)
# block = exit 2; ผ่าน = เงียบ exit 0

C="${1:-$(cat)}"
[ -n "$C" ] || exit 0

block() {
  echo "Blocked: $1" >&2
  exit 2
}

# --- guard/floor tamper (independent of the 'git' short-circuit below) ---
# A command that disables or overwrites the enforcement floor must block even
# when it contains no standalone `git` token (chmod/mv/rm of .githooks or the
# .ai/bin/check-*.sh|gate-task.sh engines, or pointing git's hooksPath away).
# This is the runnable backstop for the "do not weaken the guards" rule.
# GUARD = a guard/floor path, matched both as a file INSIDE the dir and as the
# WHOLE directory itself (no trailing slash) — `rm -r .githooks` / `chmod 000
# .githooks` / `mv .githooks /tmp/bak` / `rm -rf .ai/bin` disable the floor just
# as effectively as targeting one file inside it, so the trailing slash is
# OPTIONAL ((/|$|[[:space:]])) and the bare dir form is covered.
GUARD='(\.githooks(/[^[:space:]]*)?|\.ai/bin(/check-[^[:space:]]*\.sh|/gate-task\.sh|/?))([[:space:]]|$)'
# in-place destroy / move-away / write-to: chmod/chown/rm/truncate operate ON
# their path arg, `tee FILE` writes TO its file arg, and `mv` of a guard path
# REMOVES the floor from its place whether the guard is the source (move away)
# or the destination (overwrite). For these verbs a guard path ANYWHERE after
# the verb is the target.
echo "$C" | grep -qE "(chmod|chown|rm|truncate|tee|mv)[[:space:]].*$GUARD" &&
  block 'disable/move/overwrite guard or floor (.githooks | .ai/bin/check-*.sh | gate-task.sh) — ห้ามปิด ย้าย หรือทับ enforcement floor'
# copy/link/install where a guard path is the DESTINATION overwrites the floor.
# Unlike mv, `cp`/`ln`/`install` with the guard as the FIRST operand is a benign
# READ (e.g. `cp .githooks/pre-commit pre-commit.bak` backs the hook OUT), so the
# guard must NOT be the first operand — require a non-guard operand before it.
echo "$C" | grep -qE "(cp|ln|install)[[:space:]]+[^[:space:]]+[[:space:]].*$GUARD" &&
  block 'overwrite into guard or floor (.githooks | .ai/bin/check-*.sh | gate-task.sh) — ห้ามทับ enforcement floor'
# redirect/write INTO a guard/floor file (e.g. `echo >> .githooks/pre-commit`,
# overwrite an engine, or pipe into .git/config) disables it just the same
echo "$C" | grep -qE '>[[:space:]]*(\.githooks/|\.ai/bin/check-[^[:space:]]*\.sh|\.ai/bin/gate-task\.sh|[^[:space:]]*\.git/config)' &&
  block 'redirect/overwrite into guard, floor, or .git/config — ห้ามปิดหรือทับ enforcement floor'
# setting hooksPath via config WRITE points hooks at an empty dir and disables the
# secret-guard floor regardless of a `git` token. block only WRITES; a read-only
# query (`git config core.hooksPath`, `git config --get core.hooksPath`) is harmless
# and must pass (issue #27). case-insensitive: section.key names are case-insensitive.
# 1) inline `-c core.hooksPath=...` / any `key=value` set form (has '=')
echo "$C" | grep -qiE 'core\.hookspath[[:space:]]*=' &&
  block 'set core.hooksPath (inline -c / =value) ปิด git hooks floor — ห้ามใช้'
# 2) `git config ... core.hooksPath <value>`: a non-flag value token AFTER the key = WRITE
echo "$C" | grep -qiE 'config[^|;&]*core\.hookspath[[:space:]]+[^-[:space:]]' &&
  block 'git config core.hooksPath <value> เขียนทับ hooks floor — ห้ามใช้ (read-only query ผ่านได้)'
# 3) `git config` WRITE flags on hooksPath: --unset / --unset-all / --replace-all / --add
echo "$C" | grep -qiE 'config[^|;&]*(--unset(-all)?|--replace-all|--add)[^|;&]*core\.hookspath|config[^|;&]*core\.hookspath[^|;&]*(--unset(-all)?|--replace-all|--add)' &&
  block 'git config --unset/--replace-all/--add core.hooksPath แก้ hooks floor — ห้ามใช้'

echo "$C" | grep -qE '(^|[[:space:]])git([[:space:]]|$)' || exit 0

echo "$C" | grep -qE -- '--no-verify' &&
  block '--no-verify ข้าม secret-guard pre-commit hook — commit ตามปกติเพื่อให้ scan ทำงาน'

echo "$C" | grep -q 'SECRET_GUARD_SKIP=' &&
  block 'SECRET_GUARD_SKIP ข้าม secret scan — ถ้าจำเป็นจริงให้ user รันเองนอก session'

# short flag -n (= --no-verify ของ git commit) รวม combined เช่น -nm, -anm
# de-quote ก่อน: ลบเนื้อใน '...' และ "..." ออกเป็นช่องว่าง เพื่อไม่ให้ -n ใน
# commit message เป็น false positive — แต่ -n จริงที่วางก่อน/หลัง quoted message
# (และหลัง line continuation) จะยังเหลืออยู่ในสตริงที่สแกน จึงถูกจับทุกตำแหน่ง
# (--no-verify ทุกตำแหน่งยังถูกจับโดยบรรทัด --no-verify ด้านบนแยกต่างหาก)
# collapse newlines to spaces FIRST so a quoted message spanning a literal newline is
# a single line when de-quoted — otherwise sed (line-oriented) leaves an in-message -n
# behind and false-blocks (issue #28). real -n/--no-verify outside quotes still survives.
DQ=$(printf '%s' "$C" | tr '\n' ' ' | sed -e "s/'[^']*'/ /g" -e 's/"[^"]*"/ /g')
# strip git GLOBAL options before the subcommand so `git -c k=v commit -n` is seen
# as `git commit -n` (git accepts global opts before <command>); otherwise the
# adjacency in `git[[:space:]]+commit` lets -n slip past. -c/-C swallow a value
# token; long/short flags stand alone. ponytail: documented global opts only —
# Tier-1 git hooks + CI remain the real floor.
DQ=$(printf '%s' "$DQ" | sed -E 's/((^|[;&|]|[[:space:]])git)([[:space:]]+(-[cC][[:space:]]+[^[:space:]]+|--[A-Za-z][A-Za-z-]*(=[^[:space:]]*)?|-[A-Za-z]+))+/\1/g')
echo "$DQ" | grep -qE 'git[[:space:]]+commit.*[[:space:]]-[a-zA-Z]*n[a-zA-Z]*([[:space:]]|$)' &&
  block 'git commit -n (--no-verify) ข้าม secret-guard — commit ตามปกติ'

exit 0
