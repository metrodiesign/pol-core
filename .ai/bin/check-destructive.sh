#!/usr/bin/env bash
# destructive-guard.sh — PreToolUse(Bash) guard ตาม Destructive Ops + Workflow rules
# block = exit 2 พร้อมเหตุผลระบุกฎที่ติด; ผ่าน = เงียบ exit 0
# trade-off ที่รู้ตัว: มอง command เป็น string แบนๆ ไม่ parse shell quoting —
# คำสั่ง destructive ที่อยู่ใน quoted string (เช่นเขียน docs/test) อาจโดน block เกินจริง
# (ทิศ fail-safe); ห้ามแก้ด้วย prefix-skip echo/grep เพราะเป็น bypass hole

# จับ argv ทั้งหมด ไม่ใช่แค่ $1 — invocation ที่ word-split (หลาย arg) จะยังเห็นครบ
# (review #23). adapter ส่ง arg เดียว -> $* == $1 จึงไม่กระทบ path เดิม.
C="${*:-$(cat)}"
[ -n "$C" ] || exit 0

# Normalized copy สำหรับ "หา boundary ชื่อคำสั่ง" เท่านั้น (ไม่ได้เอาไป exec):
# ลบ backslash / single-quote / double-quote ออกทั้งหมด เพื่อให้ทุกรูปสะกดของ command token
# ยุบมาเป็นรูปธรรมดา แล้ว POS anchor (ที่ยอมรับ whitespace นำหน้า) จับได้:
#   \rm        -> rm           (#1 leading-backslash)
#   "rm"/'rm'  -> rm           (#4 quoted command name)
#   sh -c '... rm -rf ...'     -> sh -c ... rm -rf ...   (#5 -c '...' wrapper, rm ตามหลัง space)
#   eval 'rm -rf ...'          -> eval rm -rf ...        (#5 eval wrapper)
# ทิศ fail-safe เดิมคงไว้: ถ้า quoted destructive string โดน block เกินจริง = ยอมรับได้.
N=$(printf '%s' "$C" | tr -d '\\'\''"')

# strip git GLOBAL options that sit BEFORE the subcommand: git accepts
# `git -C dir push`, `git -c k=v commit` (global opts come before <command>).
# Every `git <subcmd>` check below anchors on the subcommand right after `git`,
# so without this an inserted global option would slip past the guard. Collapse
# `git -c k=v -C dir push` -> `git push`. Value-taking opts (-c/-C) swallow their
# following token; long/short flags stand alone. ponytail: covers the documented
# global options; an exotic value-flag could still slip — Tier-1 git hooks + CI
# remain the real floor.
N=$(printf '%s' "$N" | sed -E 's/((^|[;&|]|[[:space:]])git)([[:space:]]+(-[cC][[:space:]]+[^[:space:]]+|--[A-Za-z][A-Za-z-]*(=[^[:space:]]*)?|-[A-Za-z]+))+/\1/g')

# anchor ตำแหน่ง token คำสั่ง: ต้นบรรทัด / หลัง ; & | $( / หลัง whitespace
# (ครอบ indent, xargs/sudo/env-prefix, path prefix เช่น /bin/rm) + optional rtk proxy
POS='(^|[;&|][[:space:]]*|\$\([[:space:]]*|[[:space:]])(rtk[[:space:]]+(proxy[[:space:]]+)?)?([^[:space:]]*/)?'

block() {
  echo "Blocked: $1" >&2
  exit 2
}

# rm recursive+force ทุกรูปสะกด (-rf, -fr, -r -f, --recursive --force):
# ดึง span ของแต่ละ rm invocation (จบที่ separator ถัดไป) แล้วเช็ก r+f ภายใน span เดียวกัน
# — กัน flag จากคนละคำสั่ง (เช่น grep -r ... && rm -f ...) มา AND กันผิดๆ
# ใช้ $N (normalized) เพื่อให้ \rm / "rm" / sh -c 'rm -rf'/eval ถูกจับด้วย (#1/#4/#5)
RM_SPANS=$(echo "$N" | grep -oE "${POS}rm[[:space:]][^;&|]*")
if [ -n "$RM_SPANS" ]; then
  while IFS= read -r SPAN; do
    if echo "$SPAN" | grep -qE '[[:space:]](-[A-Za-z]*[rR][A-Za-z]*|--recursive)([[:space:]]|$)' &&
      echo "$SPAN" | grep -qE '[[:space:]](-[A-Za-z]*f[A-Za-z]*|--force)([[:space:]]|$)'; then
      block 'rm แบบ recursive+force — ยืนยันเป้าหมายกับ user ก่อน (Destructive Ops rules)'
    fi
  done <<<"$RM_SPANS"
fi

echo "$N" | grep -qE "${POS}git[[:space:]]+reset[[:space:]]+--hard" &&
  block 'git reset --hard — ยืนยันเป้าหมายก่อน (Destructive Ops rules)'

echo "$N" | grep -qE "${POS}git[[:space:]]+clean[[:space:]]([^;&|]*[[:space:]])?(-[A-Za-z]*f[A-Za-z]*|--force)([[:space:]]|$)" &&
  block 'git clean -f — ยืนยันเป้าหมายก่อน (Destructive Ops rules)'

echo "$N" | grep -qE "${POS}find[[:space:]][^;&|]*[[:space:]]-delete([[:space:]]|$)" &&
  block 'find -delete — ยืนยันเป้าหมายก่อน (Destructive Ops rules)'

# whole-tree working-copy discard (issue #30): `git restore .` / `-W .` / `--worktree .`
# และ `git checkout -- .` / `git checkout .` ทิ้ง uncommitted ทั้ง tree โดยไม่ผ่าน git
# history (Tier-1 backstop ไม่ถึง). block เฉพาะ pathspec '.' ทั้ง tree เท่านั้น —
# single-file (git restore file / git checkout -- file), branch switch (git checkout dev
# / -b), unstage-only (git restore --staged .) ผ่าน เพื่อกัน false-positive.
CO_SPANS=$(echo "$N" | grep -oE "${POS}git[[:space:]]+(restore|checkout)[[:space:]][^;&|]*")
if [ -n "$CO_SPANS" ]; then
  while IFS= read -r SPAN; do
    # pathspec '.' standing alone (after ' -- ' or a space, then space/end) = whole tree
    echo "$SPAN" | grep -qE '([[:space:]]--[[:space:]]|[[:space:]])\.([[:space:]]|$)' || continue
    # git restore --staged .  (unstage only, no worktree loss) -> allow
    if echo "$SPAN" | grep -qE '[[:space:]]restore([[:space:]]|$)' &&
      echo "$SPAN" | grep -qE '[[:space:]](--staged|-[A-Za-z]*S[A-Za-z]*)([[:space:]]|$)' &&
      ! echo "$SPAN" | grep -qE '[[:space:]](--worktree|-[A-Za-z]*W[A-Za-z]*)([[:space:]]|$)'; then
      continue
    fi
    block 'git restore/checkout ทั้ง working tree (.) — ทิ้ง uncommitted ทั้งหมด ไม่ผ่าน git history; stash/ยืนยันก่อน (Destructive Ops rules)'
  done <<<"$CO_SPANS"
fi

# --- SQL destructive (Destructive Ops rules: ห้าม DROP/DELETE/TRUNCATE บน prod) ---
# case-insensitive; anchor หัวคำที่ separator/whitespace/ต้นบรรทัด เพื่อไม่ชน substring
# ("backupdb" ไม่ match "dropdb", "select drop from menu" ไม่ match "DROP TABLE")
echo "$N" | grep -qiE "(^|[;&|]|[[:space:]])drop[[:space:]]+(table|database)([[:space:]]|$)" &&
  block 'SQL DROP TABLE/DATABASE — ยืนยันกับ user + ต้องมี backup (Destructive Ops rules)'

# SQL TRUNCATE: ต้องตามด้วย token ที่ไม่ใช่ dash-flag (TABLE / identifier / quote) —
# กัน coreutil 'truncate -s 0 logfile' / 'truncate --size=0' (log rotation ปกติ) ไม่ให้ false-block
echo "$N" | grep -qiE "(^|[;&|]|[[:space:]])truncate[[:space:]]+[^-[:space:]]" &&
  block 'SQL TRUNCATE — ยืนยันกับ user + ต้องมี backup (Destructive Ops rules)'

echo "$N" | grep -qiE "(^|[;&|]|[[:space:]])dropdb([[:space:]]|$)" &&
  block 'dropdb — ยืนยันกับ user + ต้องมี backup (Destructive Ops rules)'

# DELETE FROM ที่ไม่มี WHERE ใน command เดียวกัน (span จบที่ separator ถัดไป):
# มี WHERE = มีเงื่อนไข -> ผ่าน; ไม่มี WHERE = ลบทั้งตาราง -> block
DEL_SPAN=$(echo "$N" | grep -oiE "delete[[:space:]]+from[[:space:]][^;&|]*")
if [ -n "$DEL_SPAN" ] && ! echo "$DEL_SPAN" | grep -qiE "[[:space:]]where([[:space:]]|=|\(|$)"; then
  block 'SQL DELETE FROM ไม่มี WHERE — ลบทั้งตาราง ยืนยันกับ user ก่อน (Destructive Ops rules)'
fi

echo "$N" | grep -qE "${POS}git[[:space:]]+push[[:space:]][^;&|]*--force(-with-lease)?([[:space:]]|$)" &&
  block 'force push (Workflow rules: ห้าม force push)'

# short flag -f (รวมแบบ combined เช่น -uf) — จำกัด span ไม่ให้ข้าม command separator
echo "$N" | grep -qE "${POS}git[[:space:]]+push[[:space:]]+([^;&|]*[[:space:]])?-[A-Za-z]*f[A-Za-z]*([[:space:]]|$)" &&
  block 'force push -f (Workflow rules: ห้าม force push)'

# force via leading-'+' refspec (git push origin +feat / +main / +HEAD:main rewrite remote history)
echo "$N" | grep -qE "${POS}git[[:space:]]+push[[:space:]][^;&|]*[[:space:]]\+[^[:space:];&|]" &&
  block 'force push (+refspec rewrite remote history; Workflow rules: ห้าม force push)'

# --mirror = บังคับ overwrite ทุก ref ปลายทาง (force โดยธรรมชาติ) -> block ไม่มีเงื่อนไข
echo "$N" | grep -qE "${POS}git[[:space:]]+push[[:space:]][^;&|]*--mirror([[:space:]]|=|$)" &&
  block 'git push --mirror — overwrite ทุก ref ปลายทาง (Workflow rules: ห้าม force push)'

# --all + force = force-overwrite ทุก branch ref จาก branch ไหนก็ได้
if echo "$N" | grep -qE "${POS}git[[:space:]]+push[[:space:]][^;&|]*--all([[:space:]]|$)" &&
  echo "$N" | grep -qE "${POS}git[[:space:]]+push[[:space:]][^;&|]*(--force(-with-lease)?([[:space:]]|$)|[[:space:]]-[A-Za-z]*f[A-Za-z]*([[:space:]]|$))"; then
  block 'git push --all --force — force-overwrite ทุก branch (Workflow rules: ห้าม force push)'
fi

# branch protection: commit/push ขณะอยู่บน main/develop หรือ push ระบุ main/develop
if echo "$N" | grep -qE "${POS}git[[:space:]]+(commit|push)([[:space:]]|$)"; then
  # ข้อยกเว้นแคบ: branch-delete push (`git push --delete/-d <branch>`) แค่ลบ ref ปลายทาง
  # ไม่ได้ push commit ขึ้น main/develop จึงไม่ควรติด current-branch guard — flow sync หลัง
  # merge PR ยืนอยู่บน develop แล้วเก็บกวาด feature branch เป็นเรื่องปกติ. ยกเว้น "เฉพาะ"
  # current-branch เท่านั้น: ref-target guard ด้านล่างยังบล็อก --delete main/develop และ
  # force guard ด้านบน (บรรทัด 106-125) ยังบล็อก force ทุกกรณีก่อนถึงบล็อกนี้.
  # ประเมิน "ต่อ push span" แบบเดียวกับ RM_SPANS/CO_SPANS — ถือเป็น branch-delete ก็ต่อเมื่อ
  # *ทุก* push span ในคำสั่งเป็น branch-delete ล้วน; span ใดไม่ใช่ (เช่น `git push` เปล่าที่ต่อ
  # หลัง `&&`/`;`/newline) -> ปล่อยให้ current-branch guard ทำงานตามเดิม. แต่ละ span ต้องมี
  # --delete หรือ -d (token เดี่ยว) และหลังตัด delete flag ออกต้องไม่เหลือ option flag อื่น —
  # กัน `--push-option -d` (git กิน -d เป็นค่า option อื่น) และ force flag สวมเป็น delete.
  # เงื่อนไข `! git commit` กัน compound `git commit && git push --delete` ไม่ให้ commit หลุด.
  IS_BRANCH_DELETE=
  PUSH_SPANS=$(echo "$N" | grep -oE "${POS}git[[:space:]]+push([[:space:]][^;&|]*)?")
  if [ -n "$PUSH_SPANS" ] && ! echo "$N" | grep -qE "${POS}git[[:space:]]+commit([[:space:]]|$)"; then
    IS_BRANCH_DELETE=1
    while IFS= read -r SPAN; do
      # deny-list ของ "การซ้อนคำสั่งในตำแหน่ง argument" ทุกรูปที่ bash รองรับ: command substitution
      # $(...) และ backtick, process substitution <(...) >(...), function substitution (bash 5.3)
      # ${ cmd; } / ${| cmd; } — ทั้งหมดซ่อน push ตัวอื่นไว้ในนั้น และ ( หรือ ; ที่ปิด construct
      # อยู่หลัง token push จึงกลืนเข้ามาใน span. `[$<>]\(` ครอบ $( <( >( (จบด้วย '(' เหมือนกัน),
      # `\$\{[[:space:]|]` ครอบ funsub `${ ` / `${|`, + backtick. span ที่มีรูปเหล่านี้ -> ไม่ถือ
      # เป็น branch-delete (fail-safe). ตัวแปรธรรมดา ($BR / ${BR} / "$BR" / ${BR:-x}) มีตัวอักษร
      # ตามหลัง ${ และไม่มี '(' จึงไม่ติด — allow ตามเดิม.
      if echo "$SPAN" | grep -qE '[[:space:]](--delete|-d)([[:space:]]|$)' &&
        ! echo "$SPAN" | grep -qE '[$<>]\(|\$\{[[:space:]|]|`' &&
        ! echo "$SPAN" | sed -E 's/[[:space:]](--delete|-d)([[:space:]]|$)/ /g' | grep -qE '[[:space:]]-'; then
        continue
      fi
      IS_BRANCH_DELETE=
      break
    done <<<"$PUSH_SPANS"
  fi
  if [ -z "$IS_BRANCH_DELETE" ]; then
    BR=$(git branch --show-current 2>/dev/null)
    if [ "$BR" = "main" ] || [ "$BR" = "develop" ]; then
      block "git commit/push บน branch $BR — ต้อง branch แยกแล้วผ่าน PR (Workflow rules)"
    fi
  fi
  # anchor ก่อน (main|develop) รับ whitespace / ':' / '+' / '/' :
  #   ':' หรือ whitespace -> 'origin main', 'HEAD:main'
  #   '+' -> '+main' force push
  #   '/' -> fully-qualified refspec 'HEAD:refs/heads/main' (critic: เดิม / นำหน้า main เลยรอด)
  # trailing ([[:space:]]|$) บังคับ word boundary -> 'maintenance'/'developer-x' ไม่ false-block
  echo "$N" | grep -qE "${POS}git[[:space:]]+push[[:space:]][^;&|]*([[:space:]]|:|\+|/)(main|develop)([[:space:]]|$)" &&
    block 'git push ตรงเข้า main/develop — ต้องผ่าน PR (Workflow rules)'
fi

# KNOWN, INTENTIONALLY-UNBLOCKED GAP (ยอมรับเพื่อกัน false-positive สูงเกินไป):
#   git branch -D <branch> / find ... -exec rm {} +
# ลบข้อมูลได้แต่ใช้งานปกติบ่อย + กู้คืนได้ (branch -D ผ่าน reflog) — hard-block จะ FP สูง.
# enforcement floor จริงอยู่ที่ Tier 1 (git hooks + CI). ถ้าจะเพิ่มในอนาคตต้อง anchor ให้แคบก่อน.
# (git restore/checkout '.' ทั้ง working tree ถูก block ด้านบนแล้ว — issue #30.)
exit 0
