# 5. Hooks / Guardrails

automation ของโปรเจกต์นี้มีสองครึ่ง: [pane-loop](02-automation.md) (ขับ TUI ทีละ task) และ
**ชั้น hooks** — guardrail แบบ deterministic ที่ Claude Code รันให้เองรอบ ๆ tool call.
ต้นทาง: `../.claude/settings.json` (wiring) + `../.claude/hooks/*.sh` (logic). ที่นี่สรุปว่าแต่ละ
hook ยิงเมื่อไร block/warn อะไร — ส่วน logic จริงอ่านจาก script.

## 5.0 ภาพรวม

- hook ผูกใน `settings.json` ใต้ key ตาม event: `PreToolUse`, `PostToolUse`, `PreCompact`,
  `SessionStart`
- block = **exit 2** (Claude เห็น stderr แล้วต้องแก้ก่อนไปต่อ); ผ่าน/เตือน = exit 0
- การเพิ่ม/แก้ hook ใน `settings.json` **มีผลทันทีกลาง session** (ไม่ใช่ snapshot ตอนเริ่ม) —
  เหยื่อรายแรกของ guard ใหม่อาจเป็นคำสั่งทดสอบของตัวเอง (ดู §5.3 + `../.claude/rules/lessons.md`)
- `PreToolUse(Bash)` block exit 2 จะ**ฆ่า compound command ทั้งก้อน** — setup ที่มัดมาในก้อน
  เดียว (chmod/mkdir/cp) ไม่รัน; แยก setup ออกจากคำสั่งที่เสี่ยงโดน block

## 5.1 ตาราง hook ที่ wire อยู่

| script | event (matcher) | ผล | block/warn อะไร |
| ------ | --------------- | --- | --------------- |
| `destructive-guard.sh` | PreToolUse(Bash) | block (exit 2) | destructive ops + branch protection |
| `hook-bypass-guard.sh` | PreToolUse(Bash) | block (exit 2) | การข้าม secret-guard pre-commit |
| `spec-edit-guard.sh`   | PreToolUse(Edit) | warn (exit 0)  | แก้ approved requirements ขณะมี task ค้าง |
| `task-gate.sh`         | PostToolUse(Edit\|Write) | block (exit 2) | mark `[x]` ทั้งที่ test แดง / ไม่มี Evidence |
| `precompact-persist.sh`| PreCompact | inject (exit 0) | เตือน persist state ก่อน compact |
| _(inline ใน settings)_ | SessionStart | inject (exit 0) | ใส่ branch + active specs เข้า context |

### destructive-guard.sh — PreToolUse(Bash), block

block คำสั่งที่ติดกฎ Destructive Ops / Workflow (`../.claude/rules` + `../CLAUDE.md`):

- `rm` แบบ recursive+force (`-rf`, `-fr`, `-r -f`, `--recursive --force`)
- `git reset --hard`, `git clean -f`, `find ... -delete`
- force push (`--force`, `-f`/combined `-uf`, `--force-with-lease`)
- `git commit`/`git push` ขณะอยู่บน branch `main`/`develop`, หรือ push ระบุปลายทาง main/develop

รายละเอียด: รองรับ prefix `rtk (proxy )?` (เครื่องนี้ rewrite คำสั่งผ่าน rtk hook), anchor ตำแหน่ง
token คำสั่ง (ต้นบรรทัด / หลัง `;&|` / `$(` / whitespace) เพื่อกัน flag จากคนละคำสั่งมา AND กันผิด
(เช่น `grep -r ... && rm -f ...`). trade-off ที่รู้ตัว: มอง command เป็น string แบน ไม่ parse shell
quoting — destructive string ที่อยู่ใน quote (เขียน docs/test) อาจโดน block เกินจริง (ทิศ fail-safe);
ห้ามแก้ด้วย prefix-skip `echo`/`grep` เพราะเป็น bypass hole.

### hook-bypass-guard.sh — PreToolUse(Bash), block

กันการข้าม `secret-guard` pre-commit (§5.2). block เมื่อคำสั่ง git มี:

- `--no-verify` หรือ `git commit -n` (รวม combined เช่น `-nm`, `-anm`)
- `core.hooksPath` (ปิด git hooks ทั้งหมด)
- `SECRET_GUARD_SKIP=` (env ข้าม scan)

### spec-edit-guard.sh — PreToolUse(Edit), warn (non-blocking)

ไม่เคย block — inject คำเตือนอย่างเดียว. ยิงเมื่อ **ครบทุกเงื่อนไข**: ไฟล์ที่แก้คือ
`.ai/specs/*/requirements.md`, header มี `> Status: approved`, และ sibling `tasks.md`
ยังมี task ค้าง (`- [ ]`). เตือนว่าการแก้ requirements ตอนนี้ต้อง propagate ไป
`design.md`/`tasks.md` (CLAUDE.md: keep specs in sync) และอาจต้อง re-approve.

### task-gate.sh — PostToolUse(Edit|Write), block

ยิงเฉพาะเมื่อ edit/write **flip checkbox เป็น `- [x]`** ใน `.ai/specs/*/tasks.md`
(Edit: เทียบ count `[x]` ใน old/new; Write: ทับทั้งไฟล์ -> trigger เมื่อ content มี `[x]` ใด ๆ).
เมื่อ trigger:

1. รัน the project typecheck command (ผ่าน `SDD_TYPECHECK_CMD` env, หรือ package.json typecheck
   script สำหรับ Node project) — แดง -> block; ไม่ได้ประกาศ command ใด = ข้าม step นี้
2. รัน the project test runner (ผ่าน `SDD_TEST_CMD` env, หรือ package.json test script สำหรับ Node
   project) — แดง -> block (ยกเว้น runner exit เพราะหา test ไม่เจอ เช่น "No test files found", ไม่ block);
   ไม่ได้ประกาศ command ใด = ข้าม step นี้
3. (Edit path) ต้องมี `Evidence:` block ใน new_string — ขาด -> block

โค้ดเขียวเป็น env-driven: `../.ai/bin/gate-task.sh` อ่าน `SDD_TYPECHECK_CMD` / `SDD_TEST_CMD`
(auto-detect package.json scripts สำหรับ Node) แล้วค่อย per-task Evidence check. เขียวครบ + มี
Evidence = เงียบ exit 0 (zero token). นี่คือกลไกบังคับ Evidence block ที่
[`01-spec-driven-flow.md`](01-spec-driven-flow.md) §1.5 อธิบายฝั่ง workflow.

### precompact-persist.sh — PreCompact, inject (non-blocking)

ก่อน history ถูก compact (auto หรือ manual) inject คำเตือนให้เขียน active-task state ลง
`tasks.md`/`design.md` ให้ครบ 5 อย่าง: (1) active spec + task ID (2) ไฟล์ที่แก้ไปแล้ว (3) คำสั่ง
test/build/run ที่ใช้จริง (4) architectural decision + rationale (5) เสร็จอะไรแล้ว + next step.
best-effort — **โมเดลยังเป็นคนเขียน** hook แค่เตือน ไม่ persist ให้.

### SessionStart (inline ใน settings.json)

inject `Branch: <current branch>. Active specs: <ls .ai/specs>` เข้า context ทุก session.

## 5.2 secret-guard (git pre-commit)

`~/.claude/hooks/secret-guard.sh` (อยู่ **global** ไม่ใช่ project) symlink เป็น
`.git/hooks/pre-commit` — scan secret (API key/token/private key + entropy) ก่อนทุก commit.
`hook-bypass-guard.sh` (§5.1) คอยกันไม่ให้ข้าม. secret หลุดแล้ว -> rotate/revoke ทันที
(ดู [`04-git-pr-and-rules.md`](04-git-pr-and-rules.md) §4.3).

## 5.3 กับดัก / discipline

- hook fire live กลาง session (§5.0) — test คำสั่งที่มี destructive string ให้เขียนเป็นไฟล์
  `/tmp` แล้วรัน ไม่ใช่ inline (ไม่งั้น guard จับ argument ของ test เอง)
- test suite ของ hook อยู่ `../.claude/hooks/tests/` (ปัจจุบัน `hook-bypass-guard.test.sh`) —
  guard/regex เขียนเสร็จ **ห้ามเชื่อจนผ่าน adversarial test เป็นไฟล์** (bypass case +
  false-positive case รันผ่าน stdin JSON); regex ที่ผ่านตายังโดน fresh-context reviewer เจาะได้
  (ดู `../.claude/rules/lessons.md`)
- ก่อน commit script ตรวจ mode ใน index (`git ls-files -s`) — `chmod +x` ที่มัดกับคำสั่งโดน block
  จะไม่รัน ทำให้ไฟล์เข้า commit เป็น `100644` ผิด

## 5.4 เกี่ยวข้อง

- กฎต้นทางที่ guard บังคับ: [`04-git-pr-and-rules.md`](04-git-pr-and-rules.md) +
  `../CLAUDE.md` + `../.claude/rules/`
- บทเรียน hook (fire live, compound block, adversarial test, mode-in-index):
  `../.claude/rules/lessons.md`
- ฝั่ง workflow ของ Evidence/Status: [`01-spec-driven-flow.md`](01-spec-driven-flow.md)
