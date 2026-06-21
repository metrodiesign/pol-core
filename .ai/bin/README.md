# .ai/bin — harness-agnostic check engine

Single source of truth สำหรับ check logic ที่ทุก harness เรียกใช้ร่วมกัน. แต่ละ harness
(Claude hook, Codex hook, OpenCode plugin, git hook, CI) เป็น thin adapter ที่อ่าน payload
ของตัวเองแล้วเรียก script ในนี้ ตรรกะ/regex อยู่ที่นี่ที่เดียว.

## Dual interface: `C="${1:-$(cat)}"`

`check-destructive.sh` และ `check-bypass.sh` รับ command ได้ 2 ทาง:

- argv: `check-destructive.sh "git push --force"` (ส่ง command เป็น argument ตัวแรก)
- stdin: `echo "git push --force" | check-destructive.sh` (ถ้าไม่มี argv ตัวแรก จะอ่านจาก stdin)

`${1:-$(cat)}` หมายถึง "ใช้ argv[1] ถ้ามี ไม่งั้นอ่านทั้งหมดจาก stdin" adapter จะส่งทางไหนก็ได้
ตามที่สะดวกกับ payload ของ harness นั้น.

Convention ร่วม: **exit 2 = block, exit 0 = ผ่าน** (เงียบ). stderr = เหตุผลที่ block.

## Caller -> script -> interface

| Caller | Script | Interface | Block |
|---|---|---|---|
| Claude hook (`.claude/hooks/destructive-guard.sh`) | `check-destructive.sh` | adapter `jq` stdin payload -> ส่ง command เป็น argv | exit 2 |
| Claude hook (`.claude/hooks/hook-bypass-guard.sh`) | `check-bypass.sh` | adapter `jq` stdin payload -> ส่ง command เป็น argv | exit 2 |
| Claude hook (`.claude/hooks/task-gate.sh`) | `gate-task.sh` | adapter `jq` stdin -> `$1`=tasks.md path, `$2`/`$GATE_NEW`=new_string | exit 2 |
| Codex hook (`.codex/hooks/guard.sh`) | `check-destructive.sh` + `check-bypass.sh` | adapter อ่าน Codex hook input -> ส่ง command เป็น argv | exit 2 |
| OpenCode plugin (`.opencode/plugins/ai-guard.js`) | `check-destructive.sh` + `check-bypass.sh` | `$\`./.ai/bin/<c>.sh ${cmd}\`` (argv) -> `throw` เมื่อ exitCode === 2 | exit 2 -> throw |
| Claude hook (`.claude/hooks/spec-edit-guard.sh`) | `check-spec-edit.sh` | adapter `jq` stdin -> `$1`=file path; stdout -> `additionalContext` JSON | exit 0 (advisory) |
| Codex hook (`.codex/hooks/spec-edit-guard.sh`) | `check-spec-edit.sh` | adapter อ่าน Codex input -> `$1`=file path; stdout -> stderr warn | exit 0 (advisory) |
| OpenCode plugin (`.opencode/plugins/spec-edit-guard.js`) | `check-spec-edit.sh` | `$\`./.ai/bin/check-spec-edit.sh ${file}\`` -> `console.error` เมื่อ stdout ไม่ว่าง | exit 0 (advisory) |
| git hook (`.githooks/pre-commit`) | `check-secrets.sh` (default = staged) + `gate-task.sh` | ไม่มี argv (สแกน `git diff --cached`); pre-commit เรียกเอง | exit 2 |
| git hook (`.githooks/pre-push`) | branch/force ref check (ใน hook เอง ผ่าน stdin refs) | stdin refs | non-zero |
| CI (`.github/workflows/ci.yml`) | `check-secrets.sh --all` | `--all` = สแกนทั้ง tree (tracked files) | exit 2 |

## Scripts

- **check-destructive.sh** — block `rm -rf`, `git reset --hard`, `git clean -f`, `find -delete`,
  force push, และ commit/push บน main/develop. regex copy verbatim จาก
  `.claude/hooks/destructive-guard.sh` (security-critical — ห้ามดัดแปลง pattern).
- **check-bypass.sh** — block การข้าม secret-guard: `--no-verify`, `git commit -n`,
  `core.hooksPath`, `SECRET_GUARD_SKIP=`. regex copy verbatim จาก
  `.claude/hooks/hook-bypass-guard.sh`.
- **check-secrets.sh** — สแกนหา secret. default = staged (`git diff --cached`);
  `--all` = ทั้ง tree (สำหรับ CI). block patterns: Omise `skey_`/`pkey_`, Stripe `sk_`/`pk_`/`rk_`,
  AWS, GitHub token, generic high-entropy assignment, forbidden files
  (`.env`/`.env.*`/`*.pem`/`*.key`/`appsettings.*.json` ฯลฯ). port จาก
  `~/.claude/hooks/secret-guard.sh`.
- **gate-task.sh** — task-boundary gate: เมื่อ flip checkbox เป็น `[x]` ใน `tasks.md`
  ต้องรัน project typecheck command (`SDD_TYPECHECK_CMD` env หรือ auto-detect
  `package.json` typecheck script สำหรับ Node) + project test runner (`SDD_TEST_CMD` env
  หรือ `package.json` test script สำหรับ Node) ให้เขียว และมี `Evidence:` block; ถ้าไม่ได้
  ประกาศ command ไว้ จะข้าม code-green แต่ยังต้องมี Evidence อยู่. port จาก
  `.claude/hooks/task-gate.sh`.
- **check-spec-edit.sh** — advisory (NON-blocking): รับ file path (`$1`); ถ้าเป็น
  requirements.md ที่ `> Status: approved` แล้วทั้งที่ sibling tasks.md ยังมี `- [ ]` ->
  print เตือนออก stdout (adapter ห่อเป็น `additionalContext` / stderr / `console.error`).
  exit 0 เสมอ — เตือน ไม่เคย block. ใช้ร่วม Claude/Codex/OpenCode (parity, issue #29).
- **install.sh** — PRINT คำสั่ง setup ครั้งเดียว (`git config core.hooksPath .githooks`,
  `chmod +x`) ให้คนรันเอง. ไม่ mutate อะไร — guard block token `core.hooksPath`.

## Setup

รัน `bash .ai/bin/install.sh` เพื่อดูคำสั่ง setup ครั้งเดียว แล้ว copy ไปรันในเชลล์ตัวเอง
(ดูเหตุผลที่ agent รันเองไม่ได้ในหัว install.sh).
