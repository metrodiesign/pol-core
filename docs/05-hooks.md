# 5. Hooks / Guardrails

automation ของโปรเจกต์นี้มีสองครึ่ง: [pane-loop](02-automation.md) (ขับ TUI ทีละ task) และ
**ชั้น guardrail** — การบังคับกฎแบบ deterministic ที่รันให้เองรอบ ๆ การทำงาน.

โครงสร้างปัจจุบันแยกเป็น 3 ชั้น (เปลี่ยนจากเวอร์ชันเดิมที่ logic อยู่ใน `.claude/hooks/*.sh`):

| ชั้น | อยู่ที่ไหน | ทำอะไร |
| --- | --- | --- |
| **Tier 1 — floor** | `../.githooks/pre-commit`, `../.githooks/pre-push` + CI | git-level, **harness-agnostic** — บังคับกับทุก agent และทุกคน. นี่คือด่านจริง |
| engine | `../.ai/bin/check-*.sh`, `../.ai/bin/gate-task.sh` | **logic จริงทั้งหมด** อยู่ที่นี่ที่เดียว (single source) |
| Tier 2 — adapter | `../.claude/settings.json` (wiring) + `../.claude/hooks/*.sh` | adapter บาง ๆ ของ Claude Code: แกะ JSON payload แล้ว `exec` ต่อไป engine |

`.claude/hooks/*.sh` **ไม่มี logic ของตัวเอง** — เป็น convenience layer ที่ยิงเร็วกว่า (ตอน
tool call ไม่ใช่ตอน commit) เท่านั้น. Codex (`../.codex/hooks/*.sh`) และ OpenCode
(`../.opencode/plugins/*.js`) เป็น adapter ชุดเดียวกันชี้ engine เดียวกัน ทุก harness จึงบังคับ
กฎเหมือนกันแบบ byte-for-byte (ดู `../.ai/bin/README.md` ตาราง caller -> script).

## 5.0 ภาพรวมชั้น Claude (Tier 2)

- hook ผูกใน `settings.json` ใต้ key ตาม event: `PreToolUse`, `PostToolUse`, `PreCompact`,
  `SessionStart`
- block = **exit 2** (Claude เห็น stderr แล้วต้องแก้ก่อนไปต่อ); ผ่าน/เตือน = exit 0
- การเพิ่ม/แก้ hook ใน `settings.json` **มีผลทันทีกลาง session** (ไม่ใช่ snapshot ตอนเริ่ม) —
  เหยื่อรายแรกของ guard ใหม่อาจเป็นคำสั่งทดสอบของตัวเอง (ดู §5.5 + `../.ai/shared/LESSONS.md`)
- `PreToolUse(Bash)` block exit 2 จะ**ฆ่า compound command ทั้งก้อน** — setup ที่มัดมาในก้อน
  เดียว (chmod/mkdir/cp) ไม่รัน; แยก setup ออกจากคำสั่งที่เสี่ยงโดน block

## 5.1 ตาราง hook ที่ wire อยู่

| adapter | event (matcher) | engine ที่เรียก | ผล |
| ------- | --------------- | --------------- | --- |
| `destructive-guard.sh` | PreToolUse(Bash) | `.ai/bin/check-destructive.sh` | block (exit 2) |
| `hook-bypass-guard.sh` | PreToolUse(Bash) | `.ai/bin/check-bypass.sh` | block (exit 2) |
| `spec-edit-guard.sh` | PreToolUse(Edit) | `.ai/bin/check-spec-edit.sh` | warn (exit 0) |
| `task-gate.sh` (timeout 120s) | PostToolUse(Edit\|Write) | `.ai/bin/gate-task.sh` | block (exit 2) |
| `precompact-persist.sh` | PreCompact | — (logic ในไฟล์เอง) | inject (exit 0) |
| _(inline ใน settings)_ | SessionStart | — (`jq` inline) | inject (exit 0) |

### destructive-guard.sh -> check-destructive.sh — PreToolUse(Bash), block

adapter คือ 5 บรรทัด: `jq -r '.tool_input.command'` แล้ว `exec` engine. engine
(`../.ai/bin/check-destructive.sh`) รับได้ทั้ง argv (`"${*:-...}"` — จับทุก arg ไม่ใช่แค่ `$1`)
และ stdin. block ตามกฎ Destructive Ops / Workflow:

**ไฟล์ / working tree**

- `rm` แบบ recursive+force ทุกรูปสะกด (`-rf`, `-fr`, `-r -f`, `--recursive --force`) — เช็ก r+f
  ภายใน **span ของ `rm` ตัวเดียวกัน** (จบที่ separator ถัดไป) กัน `grep -r ... && rm -f ...`
  ไม่ให้ AND กันผิด
- `git reset --hard`
- `git clean` ที่มี `-f`/`--force`
- `find ... -delete`
- `git restore .` / `git checkout .` / `git checkout -- .` **ทั้ง working tree** — ทิ้ง
  uncommitted โดยไม่ผ่าน git history (Tier 1 ไม่เห็น). block เฉพาะ pathspec `.` เท่านั้น;
  single-file, branch switch (`git checkout dev`, `-b`) และ `git restore --staged .`
  (unstage อย่างเดียว ไม่เสีย worktree) ผ่าน

**SQL**

- `DROP TABLE` / `DROP DATABASE` (case-insensitive, anchor หัวคำ — `select drop from menu` ไม่ติด)
- `TRUNCATE` ที่ตามด้วย token ที่ไม่ใช่ dash-flag — กัน coreutil `truncate -s 0 logfile` ไม่ให้ false-block
- `dropdb` (anchor word boundary — `backupdb` ไม่ติด)
- `DELETE FROM` ที่ **ไม่มี `WHERE`** ใน span เดียวกัน

**git remote / branch protection**

- force push: `--force`, `--force-with-lease`, short `-f` รวม combined (`-uf`)
- `git push origin +ref` (leading-`+` refspec = rewrite remote history)
- `git push --mirror` (overwrite ทุก ref ปลายทาง — block ไม่มีเงื่อนไข)
- `git push --all` + force flag
- `git commit`/`git push` ขณะอยู่บน branch `main`/`develop`
- `git push` ที่ระบุปลายทาง main/develop รวม fully-qualified (`HEAD:refs/heads/main`)

**รายละเอียดที่ควรรู้**

- normalize ก่อน match: ลบ `\`, `'`, `"` ทั้งหมด เพื่อให้ `\rm`, `"rm"`, `sh -c 'rm -rf ...'`,
  `eval 'rm -rf ...'` ยุบมาเป็นรูปธรรมดาแล้วโดนจับ
- strip git **global options** ที่วางก่อน subcommand (`git -C dir push`, `git -c k=v commit`)
  ไม่งั้น flag แทรกจะเลื่อน subcommand พ้น anchor
- anchor ตำแหน่ง token คำสั่ง (ต้นบรรทัด / หลัง `;&|` / `$(` / whitespace / path prefix
  `/bin/rm`) + รองรับ prefix `rtk (proxy )?`
- trade-off ที่รู้ตัว: มอง command เป็น string แบน ไม่ parse shell quoting — destructive string
  ที่อยู่ใน quote (เขียน docs/test) อาจโดน block เกินจริง (ทิศ fail-safe); **ห้าม**แก้ด้วย
  prefix-skip `echo`/`grep` เพราะเป็น bypass hole
- **known gap ที่จงใจไม่ block** (comment ท้าย script): `git branch -D <branch>` และ
  `find ... -exec rm {} +` — ใช้งานปกติบ่อยและกู้คืนได้ (branch -D ผ่าน reflog) hard-block จะ
  false-positive สูง; enforcement จริงอยู่ที่ Tier 1

### hook-bypass-guard.sh -> check-bypass.sh — PreToolUse(Bash), block

adapter 5 บรรทัดแบบเดียวกัน. engine block 2 กลุ่ม:

**(ก) tamper กับ guard / floor เอง** — เช็คก่อนและ**ไม่ขึ้นกับ**ว่าคำสั่งมี token `git` หรือไม่.
เป้าหมายที่คุ้มครอง (`$GUARD`) = `.githooks/**` และ `.ai/bin/check-*.sh` / `gate-task.sh`
— match ทั้งไฟล์ข้างในและ**ตัว directory เปล่า ๆ** (`rm -rf .ai/bin`, `chmod 000 .githooks`
ปิด floor ได้พอกัน):

- `chmod` / `chown` / `rm` / `truncate` / `tee` / `mv` ที่มี guard path อยู่หลัง verb
  (ทำลาย ย้ายออก หรือเขียนทับ)
- `cp` / `ln` / `install` ที่มี guard path เป็น **destination** — ถ้า guard เป็น operand แรก
  ถือเป็นการ **อ่าน** ที่ไม่อันตราย (`cp .githooks/pre-commit pre-commit.bak`) จึงผ่าน
- shell redirect เข้า guard file หรือเข้า `.git/config` (`echo >> .githooks/pre-commit`)
- `core.hooksPath` แบบ **write** เท่านั้น: inline `-c core.hooksPath=...`, `git config ...
  core.hooksPath <value>`, และ write flags `--unset` / `--unset-all` / `--replace-all` / `--add`.
  **read-only query ผ่านได้** (`git config --get core.hooksPath`) — issue #27

**(ข) ข้าม pre-commit** — ส่วนนี้ short-circuit: ทำงานต่อเมื่อคำสั่งมี token `git` เดี่ยว ๆ:

- `--no-verify` (ทุกตำแหน่ง)
- `SECRET_GUARD_SKIP=`
- `git commit -n` รวม combined (`-nm`, `-anm`) — de-quote ก่อนสแกน (ลบเนื้อใน `'...'`/`"..."`)
  เพื่อไม่ให้ `-n` ใน commit message เป็น false positive, และยุบ newline เป็น space ก่อน เพื่อให้
  message ที่คร่อมหลายบรรทัดถูก de-quote ครบ (issue #28); strip git global options ก่อนเช่นกัน

### spec-edit-guard.sh -> check-spec-edit.sh — PreToolUse(Edit), warn (non-blocking)

ไม่เคย block — adapter แกะ `file_path` เรียก engine แล้วห่อ stdout เป็น
`hookSpecificOutput.additionalContext`, exit 0 เสมอ. engine เตือนเมื่อ **ครบทุกเงื่อนไข**:

1. ไฟล์คือ `.ai/specs/*/requirements.md` (หรือ legacy `.claude/specs/*/`) — แก้ `tasks.md`/`design.md` เงียบ
2. ไฟล์มีอยู่จริงบน disk และ header match `^> *Status: *approved` (ครอบ "approved <date>",
   "(quick, no gates)", "approved <orig>, amended <date>")
3. sibling `tasks.md` ยังมี task ค้าง (`- [ ]`)

ข้อความเตือนบอกจำนวน task ที่ค้าง + ว่าการแก้ requirements ตอนนี้ต้อง propagate ไป
`design.md`/`tasks.md` และอาจต้อง re-approve. logic อยู่ใน engine ที่เดียว Claude/Codex/OpenCode
จึงเตือนข้อความเดียวกัน (parity, issue #29).

### task-gate.sh -> gate-task.sh — PostToolUse(Edit|Write), block

adapter ทำ 3 อย่างก่อน delegate: filter path ให้เป็น `.ai/specs/*/tasks.md`, ตรวจว่า
**flip เป็น `- [x]` จริง** (Edit: เทียบ count `[x]` ใน old/new; Write: trigger เมื่อ content มี
`[x]` ใด ๆ เพราะทับทั้งไฟล์ เทียบก่อน/หลังไม่ได้) แล้ว `exec` engine ด้วย
`$1`=path, `$2`=เนื้อหาจริง (ไม่มีการฉีด Evidence ปลอม).

engine (`../.ai/bin/gate-task.sh`) เมื่อ trigger:

1. **typecheck** — `$SDD_TYPECHECK_CMD` (stack-agnostic) หรือ auto-detect `npm run typecheck`
   จาก package.json สำหรับ Node. แดง -> block. ไม่ได้ประกาศ = ข้าม
2. **test** — `$SDD_TEST_CMD` หรือ auto-detect `npm test`. แดง -> block **ยกเว้น** runner exit
   เพราะหา test ไม่เจอ (`no test files found` / `no tests ran` / `collected 0 items`)
3. **Evidence per task** — แต่ละ `- [x]` ที่ flip ต้องมี `Evidence:` **ในบล็อกของตัวเอง**
   (region = ตั้งแต่บรรทัด checkbox จนถึง checkbox ถัดไป/EOF). Evidence ของ task อื่นยืมไม่ได้.
   ค่าที่ใส่ต้อง **non-trivial** — ว่าง หรือ placeholder (`TODO`/`TBD`/`???`/`-`/`.`/`none`/
   `pending`/`n/a (write path)`) ไม่ผ่าน. รองรับทั้งแบบ inline (`Evidence: <ค่า>`) และแบบ
   header + bullet list

การ scope Evidence ต่อ task ทำให้ verdict เหมือนกันไม่ว่า input จะเป็น hunk เดียว (Claude Edit /
Codex) หรือทั้งไฟล์ (Write / OpenCode). เขียวครบ + มี Evidence = เงียบ exit 0 (zero token).
นี่คือกลไกบังคับ Evidence block ที่ [`01-spec-driven-flow.md`](01-spec-driven-flow.md) §1.5
อธิบายฝั่ง workflow.

### precompact-persist.sh — PreCompact, inject (non-blocking)

ก่อน history ถูก compact (auto หรือ manual) inject คำเตือนให้เขียน active-task state ลง
`tasks.md`/`design.md` ให้ครบ 5 อย่าง: (1) active spec + task ID (2) ไฟล์ที่แก้ไปแล้ว (3) คำสั่ง
test/build/run ที่ใช้จริง (4) architectural decision + rationale (5) เสร็จอะไรแล้ว + next step.
best-effort — **โมเดลยังเป็นคนเขียน** hook แค่เตือน ไม่ persist ให้. ไม่มี `jq` = exit 0 เงียบ.

### SessionStart (inline ใน settings.json)

inject `Branch: <current branch>. Active specs: <ls .ai/specs>` เข้า context ทุก session.

## 5.2 Tier 1 floor — `.githooks/` (ด่านจริง)

git hooks ที่ commit ไว้ใน repo. **harness-agnostic** — บังคับกับทุก agent (Claude, Codex,
OpenCode, Pi) และกับคนที่พิมพ์ git เองเท่ากันหมด. ชั้น `.claude/hooks/*` ข้างบนเป็นแค่ความ
สะดวกที่ยิงเร็วกว่า ไม่ใช่ด่านที่เชื่อถือได้ (root [`../CLAUDE.md`](../CLAUDE.md) และ
`../.ai/README.md` ระบุ framing นี้ตรง ๆ).

### เปิดใช้ (ครั้งเดียวต่อ clone)

```sh
./.ai/bin/install.sh        # sets core.hooksPath=.githooks + chmod +x
git config core.hooksPath   # verify -> .githooks
```

**agent รันคำสั่ง `git config core.hooksPath .githooks` เองไม่ได้** — bypass guard (§5.1) block
token นั้น. ทางออกคือให้ *คน* รัน `install.sh` แล้ว *ตัว script* เป็นผู้เรียก `git config`
(idempotent + no-op นอก git repo). CI เป็น server-side floor ที่ทำงานอยู่ดีไม่ว่า clone จะ wire
หรือไม่.

### `.githooks/pre-commit` — block

เร็วโดยตั้งใจ (ไม่มี test — test อยู่ใน CI) เพื่อไม่ให้ใครถูกยั่วให้ใช้ `--no-verify`. 2 gate:

1. **secret scan** ของ staged diff — เรียก `.ai/bin/check-secrets.sh` (§5.3)
2. **Evidence check** สำหรับ `*/tasks.md` ที่ staged — **scope-aware**: block เฉพาะเมื่อบรรทัด
   `- [x]` ที่ diff นี้ **เพิ่มเข้ามาใหม่** ไม่มี `Evidence:` ในบล็อกของตัวเอง. task `[x]` เดิม
   ไม่ถูกตรวจซ้ำ และ edit ที่ไม่ได้เพิ่ม `[x]` ใหม่ block ไม่ได้เลย (กัน false-block)

non-zero ใด ๆ = commit ถูก block.

### `.githooks/pre-push` — block

ตรวจแบบ **ref-based ไม่ใช่ regex** — แข็งแรงกว่าเพราะ match กับ model ของ git เอง ไม่ใช่
วิธีสะกดคำสั่ง. git ป้อน `<local ref> <local sha> <remote ref> <remote sha>` มาทาง stdin ทีละ ref:

1. **protected branch** — `refs/heads/main` / `refs/heads/develop` -> block (ต้องผ่าน PR)
2. **ลบ remote ref** (local sha เป็นศูนย์ทั้งหมด) -> block
3. **non-fast-forward / force push** — ถ้า remote sha ไม่ใช่ ancestor ของ local sha
   (`git merge-base --is-ancestor`) = จะ rewrite remote history -> block. ข้ามเมื่อสร้าง ref ใหม่

non-zero = push ทั้ง batch ถูก block.

## 5.3 secret-guard — `.ai/bin/check-secrets.sh`

engine เดียวสำหรับ secret scan ทั้งระบบ (port มาจาก `~/.claude/hooks/secret-guard.sh` เดิมที่เคย
symlink เป็น `.git/hooks/pre-commit` แบบ global — วิธีนั้น **เลิกใช้แล้ว**; detection pattern
ยกมาตรง ๆ). เรียกจาก 2 ที่:

- `.githooks/pre-commit` — default mode = สแกน **staged** (`git diff --cached`)
- CI `.github/workflows/ci.yml` — `--all` = สแกน **ทั้ง tree** (tracked files) จับของที่หลุด
  pre-commit หรือ commit มาตอนยังไม่ได้ set `core.hooksPath`

**block เมื่อเจอ (exit 2)**

- filename ต้องห้าม: `.env`, `.env.*`, `*.env` (anchor ที่ basename — `en.env.json` ไม่ติด),
  `*.pem`, `*.key`, `*.pfx`, `*.p12`, `secrets.json`, `id_rsa`, `id_ed25519`,
  `appsettings.{Development,Production,Local}.json` — ยกเว้นชื่อที่มี example/template/sample
- key pattern: Omise (`skey_`/`pkey_`), Stripe (`sk_`/`pk_`/`rk_`), AWS (`AKIA…`,
  `aws_secret_access_key=`), GitHub (`ghp_`, `github_pat_`)
- PEM block `-----BEGIN … PRIVATE KEY-----` ในไฟล์ชื่ออะไรก็ตาม
- generic assignment: `secret|api_key|password|token|private_key` = ค่ายาว >= 20 ตัว. ตัด
  placeholder ออกจาก **ค่าที่ match เท่านั้น** (ไม่ใช่ทั้งบรรทัด) — comment `# TODO` ต่อท้าย
  จึง whitelist ของจริงไม่ได้; และตัด hit ที่ค่าเป็นตัวอักษรล้วน (PascalCase identifier ใน C#)
  ออก เพราะ secret จริงแทบทุกตัวมีตัวเลข/อักขระพิเศษ
- connection string ที่ฝัง credential: `scheme://user:PASS@host` (password >= 6 ตัว)

**escape hatch** — `SECRET_GUARD_SKIP=1` ใช้ได้เฉพาะ staged path (คนพิมพ์เอง) และถูก
**ignore ใน `--all`/CI โดยสิ้นเชิง** (CI เป็น hard gate ที่ env var สั่งปิดไม่ได้). ฝั่ง session
`check-bypass.sh` block ตัวแปรนี้อยู่แล้ว (§5.1).

test corpus ของ guard เอง (`.claude/hooks/tests/`) ถูก exclude จากการสแกน — มันเก็บ token
รูป Stripe/GitHub/AWS ปลอมไว้พิสูจน์ว่า pattern จับได้ ถ้าไม่ exclude scanner จะ commit test
ตัวเองไม่ได้.

secret หลุดแล้ว -> rotate/revoke ทันที (ดู [`04-git-pr-and-rules.md`](04-git-pr-and-rules.md) §4.3).

## 5.4 test suite + CI

`../.claude/hooks/tests/` (adversarial, รันผ่าน `bash <file>` ตรง ๆ):

| ไฟล์ | ครอบ |
| --- | --- |
| `destructive-guard.test.sh` | `check-destructive.sh` — block case + false-positive case |
| `hook-bypass-guard.test.sh` | `check-bypass.sh` — bypass + tamper + read-only-ผ่าน |
| `secrets-guard.test.sh` | `check-secrets.sh` — pattern + placeholder + forbidden file |
| `gate-task.test.sh` | `gate-task.sh` — flip detection + per-task Evidence |
| `spec-edit-guard.test.sh` | `check-spec-edit.sh` — cross-vendor parity ของ advisory |
| `codex-adapters.test.sh` | `.codex/hooks/*` แกะ payload ของ Codex ถูกและ route ไป engine เดียวกัน (issue #26) |

CI job **`verify`** ชื่อ **"guards + spec-trace"** ใน `../.github/workflows/ci.yml` (required
check บน PR เข้า main/develop) รันทั้งหมดนี้:

1. **Guard regression tests** — loop ทุก `.claude/hooks/tests/*.test.sh`; ถ้าไม่เจอไฟล์เลย =
   fail (กันสถานการณ์ suite หายไปเงียบ ๆ). guard ที่ถูกทำให้อ่อนลงจะตก paired block/allow case ของตัวเอง
2. **Secret scan** — `.ai/bin/check-secrets.sh --all` โดย force `SECRET_GUARD_SKIP: ''`
3. **Rename identifier gate** — `scripts/check-rename-identifiers.sh`
4. **Spec trace** — `scripts/spec-trace.sh` ต่อทุก feature ใต้ `.ai/specs/`

## 5.5 กับดัก / discipline

- hook fire live กลาง session (§5.0) — test คำสั่งที่มี destructive string ให้เขียนเป็นไฟล์
  แล้วรัน ไม่ใช่ inline (ไม่งั้น guard จับ argument ของ test เอง)
- guard/regex เขียนเสร็จ **ห้ามเชื่อจนผ่าน adversarial test เป็นไฟล์** (bypass case +
  false-positive case คู่กัน); regex ที่ผ่านตายังโดน fresh-context reviewer เจาะได้
  (ดู `../.ai/shared/LESSONS.md`)
- ก่อน commit script ตรวจ mode ใน index (`git ls-files -s`) — `chmod +x` ที่มัดกับคำสั่งโดน block
  จะไม่รัน ทำให้ไฟล์เข้า commit เป็น `100644` ผิด
- แก้ logic ของ guard ให้แก้ที่ `../.ai/bin/*.sh` **ที่เดียว** — `.claude/hooks/`, `.codex/hooks/`,
  `.opencode/plugins/` เป็น adapter ห้ามใส่ logic ซ้ำ (จะหลุด parity ทันที)
- อย่าพยายามปิด guard: bypass guard block `chmod`/`mv`/`rm`/redirect ที่แตะ `.githooks/` หรือ
  `.ai/bin/check-*.sh` และ block การ set `core.hooksPath` (§5.1 ก)

## 5.6 เกี่ยวข้อง

- กฎต้นทางที่ guard บังคับ: [`04-git-pr-and-rules.md`](04-git-pr-and-rules.md) +
  `../CLAUDE.md` + `../.ai/shared/CODING_STANDARDS.md`
- ตาราง caller -> script -> interface ของ engine ทุกตัว: `../.ai/bin/README.md`
- setup / parity matrix ต่อ harness: `../.ai/README.md`
- บทเรียน hook (fire live, compound block, adversarial test, mode-in-index):
  `../.ai/shared/LESSONS.md`
- ฝั่ง workflow ของ Evidence/Status: [`01-spec-driven-flow.md`](01-spec-driven-flow.md)
