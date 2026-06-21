# แผน optimize spec-driven development (อ้างอิง Kiro docs)

> ที่มา: workflow `kiro-sdd-optimizer` (45 agents, 2026-06-12) — fetch Kiro docs 16/16 หน้า
> (specs 8, hooks 6, steering 1, skills 1) + audit setup ปัจจุบัน 5 ด้าน → ข้อเสนอ 20 ข้อ
> ทุกข้อผ่าน adversarial verify กับไฟล์จริง (apply ตรงได้ 3, ต้องปรับแล้วใช้ได้ 17, reject 0)
>
> - completeness critic พบ gap เพิ่มอีก 22 จุด
>
> สถานะ: Tier 1+2 applied แล้ว (2026-06-12) ผ่าน adversarial review (4 reviewers, 25 findings)
> และแก้ findings สำคัญครบ: destructive-guard restructure (span-based rm check, anchor ครอบ
> indent/xargs/sudo/path-prefix), hook-bypass-guard.sh แยกไฟล์ (ปิด -nm combined /
> core.hooksPath / SECRET_GUARD_SKIP bypass), task-gate กัน test-runner no-tests false block,
> spec_trace.py boundary guards + sub-heading fix + Satisfies continuation,
> pane-loop pre-flight Status check, flip-on-confirm + amended re-stamp ใน skills.
> Tier 3 applied แล้ว (2026-06-12, PR แยก): ST2 stack rule เหลือ paths ของ UI source directory +
> \*.config (33KB) แยก verify recipes 16 ข้อไป spec-implement/references/browser-verify.md
> (15.9KB, โหลดเฉพาะช่วง verify) — agent ตรวจแล้ว 49/49 bullets ครบ ไม่มี substance หาย;
> ST4 ถอด @-import ซ้ำใน CLAUDE.md; ST5 ย้าย cost mechanics ไป
> spec-retro/references/cost-accounting.md เหลือ kernel ใน lessons.md;
> ST6 sync tech.md (dependency/test-runner ที่หลุดบันทึก) + structure.md (ไฟล์จริง) + lessons.md (สถานะ test-runner
> bump) + เพิ่มขั้น Steering sync ใน spec-retro.
> Tier 4 applied แล้ว (2026-06-13, PR #7 บน main): W2 design-first ใช้ได้จริง (spec-design
> 2 โหมด, spec-requirements derive mode + EARS 3→5 patterns + atomic/subjective guard,
> spec-tasks design-first second gate, CLAUDE.md 1 บรรทัด); W6 rewrite spec-bugfix เต็ม
> shape (intake batch, bugfix.md + F-ID/B-ID, Status gate, 3-dim validation observable
> failure mode, spec_trace.py skip bugfix.md-only); A6 harden agent contracts (pbt-runner
> →opus + test-runner path contract, bug-investigator reproduce จริง, spec-architect
> produce/critique mode, ทั้งหมดรายงานไทย). ผ่าน adversarial review 4-lens 2 รอบ: รอบแรก
> verifier 16/17 ตายเพราะ spend-limit จึง re-verify ด้วยมือ; รอบสอง (harness แก้ partition
> ให้ verdict ที่หาย = unresolved ไม่ใช่ refuted) รันสะอาด 0 unresolved พบ dead-end เพิ่ม 1 จุด:
> spec-tasks second gate ดูด requirements approval ไป แต่ design.md traceability backfill
> ผูกกับ approval turn ของ spec-requirements → design-first path ที่รัน /spec-tasks ต่อ ทำให้
> design.md ไม่มี Requirement Traceability → spec-trace.sh hard-fail. แก้แล้ว (4d0d56c, option b:
> spec-requirements derive mode backfill ตอน derive ไม่รอ approval — single ownership).
> Tier 5 (ข้อ 1-6) applied แล้ว (2026-06-13): ข้อ 1 (EARS 5 patterns + atomic/subjective
> guard) = done อยู่แล้วจาก Tier 4 PR #7 → ข้าม; ข้อ 2 PreCompact hook (precompact-persist.sh
> inject เตือน persist active-task state ก่อน compact, jq-guard กัน exit non-zero); ข้อ 3
> spec-edit-guard.sh (PreToolUse Edit, non-blocking warn เมื่อแก้ approved requirements.md ที่ยังมี
> task ค้าง); ข้อ 4 completion evidence (spec-implement step 4 บังคับ Evidence block + spec-tasks
> sync คง block + task-gate.sh evidence-presence gate, anchored grep กัน substring-fake, Edit-path;
> Write = conscious limit เดิม); ข้อ 5 full repurpose spec-architect (default=critique reviewer-primary,
> producer outline ชี้ spec-design เป็น single source ปิด drift, wire critique pass ใน spec-design +
> design-first carve-out); ข้อ 6 backfill artifact (Satisfies bare IDs, REQ-15.6 reorder, bugfix
> placeholder → a real test file co-located with the logic under test). ผ่าน adversarial verify 6 agents: baseline เขียว (47/47,
> trace 70), hook test suite (precompact/spec-guard/task-gate), review arch/impl — พบ 3 hole/finding
> แก้แล้ว (anchor grep, jq-guard, design-first carve-out) re-verify ผ่าน. Tier 5 ข้อ 7-20 (รอง) ยังไม่ทำ.

## สรุปการจัดลำดับ

| Tier | ธีม                                                                          | ข้อ                            |
| ---- | ---------------------------------------------------------------------------- | ------------------------------ |
| 1    | ซ่อมของที่พังเงียบอยู่ตอนนี้ (hooks ตาย, rules orphan, skill ขัดกันเอง)      | A1, S1, A4, A2, A3, ST3, W1    |
| 2    | ปิด loop ของ spec workflow (state durable, coverage gate, analyze repair)    | W3, W4+ST7 (merge), W5, A5, W7 |
| 3    | โครงสร้าง steering (path scope, dedup loading, ย้าย cost, sync ground truth) | ST2, ST4, ST5, ST6             |
| 4    | ขยายความสามารถ (design-first, bugfix shape, agent contracts)                 | W2, W6, A6                     |
| 5    | gap จาก critic ที่ควรยกเป็นงานเพิ่ม                                          | ดูท้ายไฟล์                     |

หมายเหตุ overlap ที่ต้อง merge ตอน implement:

- W4(ข) filesystem reconcile = ใช้ script ของ ST7 (`scripts/spec-state.sh`) เป็นตัวรัน — ทำคู่กัน
- W4(ก) REQ-coverage rule ใน spec-tasks = ฝั่ง "กติกา" ของ A5 (`scripts/spec-trace.sh`) ที่เป็นฝั่ง "ตัวตรวจ" — เขียน rule ให้อ้าง script

---

## Tier 1 — ซ่อมของที่พังเงียบ

### A1. สร้าง task-boundary quality gate แทน Stop/TaskCompleted ที่ตายทั้งคู่ [apply, high/medium]

ไฟล์: `.claude/settings.json`, `.claude/hooks/task-gate.sh` (ใหม่)

- พิสูจน์แล้ว: Stop hook exit code เป็นของ `tail` + `exit 0` ปิดท้าย = โมเดลไม่เคยเห็นผล test; TaskCompleted ตายสองชั้น (lint script ที่ผูกกับ tool ซึ่งถูกถอดออกใน toolchain เวอร์ชันที่ใช้ → exit 1 เสมอ, event ยิงเฉพาะ Task tools ที่ workflow นี้ไม่ใช้)
- ทำ: ลบ Stop + TaskCompleted hooks; เพิ่ม PostToolUse (matcher `Edit|Write`, timeout 120) → `.claude/hooks/task-gate.sh`: อ่าน stdin JSON ด้วย jq, early-exit ถ้า file ไม่ใช่ `.ai/specs/*/tasks.md`, ตรวจว่า edit flip `- [ ]` → `- [x]` (เทียบ old_string/new_string; Write ดู content), ถ้าใช่รัน gate ที่ขับด้วย env (`.ai/bin/gate-task.sh` อ่าน `SDD_TYPECHECK_CMD` / `SDD_TEST_CMD` — auto-detect package.json scripts ของ Node ถ้ามี) — เขียว = เงียบ exit 0, แดง = exit 2 + stderr ไทยสั้น "ห้าม mark [x] จนกว่าเขียว" ยิงแค่ 5-10 ครั้ง/feature

### S1. ซ่อม feedback loop ของ Stop hook (ถ้าเลือกเก็บ Stop ไว้แทน A1) [adjust, high/low]

ไฟล์: `.claude/settings.json`, `package.json`

- A1 กับ S1 เลือกแนวเดียว: A1 = gate ที่ task boundary (แนะนำ), S1 = ซ่อม Stop ให้ทำงานจริง
- ถ้าทำ S1: Stop hook ต้องมี guard `stop_hook_active` กัน infinite loop:
  `INPUT=$(cat); [ "$(echo "$INPUT" | jq -r '.stop_hook_active // false')" = "true" ] && exit 0; OUT=$("$SDD_TEST_CMD" 2>&1); ST=$?; if [ $ST -ne 0 ]; then echo 'Tests failing:' >&2; echo "$OUT" | tail -20 >&2; exit 2; fi; exit 0` (`$SDD_TEST_CMD` = the project test runner ผ่าน env หรือ package.json test script สำหรับ Node)
- ทั้งสองแนว: TaskCompleted เปลี่ยนเป็น gate code-green ที่ขับด้วย env (`.ai/bin/gate-task.sh` อ่าน `SDD_TYPECHECK_CMD` แล้ว `SDD_TEST_CMD` — auto-detect package.json scripts ของ Node) `|| { echo 'Task not green' >&2; exit 2; }` หรือลบทิ้ง; ถอด lint script ที่ตายออกจาก manifest (ผูกกับ tool ที่ถูกถอดใน toolchain เวอร์ชันที่ใช้, ไม่มี linter ติดตั้ง)

### A4. ถอด prettier hook + ซ่อม SessionStart JSON [adjust, medium/low]

ไฟล์: `.claude/settings.json`

- ข้อเท็จจริงจาก verify (แรงกว่าที่คิด): prettier hook "ทำงานจริง" — npm 11 auto-install prettier เวอร์ชัน floating ผ่าน npx (ไม่เคยผ่าน dependency review, ขัด Dependency rules) format ทุกไฟล์รวม .md ใน `.ai/specs/` (~0.6s/ครั้ง) + ทำ harness file-state stale บังคับ re-read
- ทำ: ลบ PostToolUse prettier entry; ถ้าอยากได้ format จริงในอนาคต = pin prettier exact version ผ่าน approval + `.prettierignore` กัน `.claude/**` และ `*.md` + `npx --no-install`
- SessionStart: แทน string interpolation ด้วย jq (กัน JSON พัง/context injection จากชื่อ branch แปลก):
  `jq -n --arg b "$(git branch --show-current 2>/dev/null)" --arg s "$(ls .ai/specs 2>/dev/null | tr '\n' ' ')" '{hookSpecificOutput:{hookEventName:"SessionStart",additionalContext:("Branch: "+$b+". Active specs: "+$s)}}'`

### A2. ขยาย destructive-command guard ให้ครอบ Destructive Ops + Workflow rules จริง [adjust, high/low]

ไฟล์: `.claude/settings.json`, `.claude/hooks/destructive-guard.sh` (ใหม่)

- ปัจจุบันจับแค่ substring `rm -rf` — พลาด `rm -fr`, `rm -r -f`, `git reset --hard`, `git clean -fd`, `find -delete`, `git push --force`, push ตรงเข้า main; และ false positive กับ `grep 'rm -rf'` (verify agent โดนเองระหว่างตรวจ) เสี่ยงสูงเพราะ global ตั้ง bypassPermissions
- ทำ: ย้ายเป็น script ชี้จาก settings.json (`$CLAUDE_PROJECT_DIR/.claude/hooks/destructive-guard.sh`)
- rm ตรวจ 3 เงื่อนไข AND กัน (อย่าใช้ regex เดี่ยว — verify พิสูจน์ว่า miss `rm -r -f`):
  ตำแหน่งคำสั่ง `(^|[;&|]\s*|\$\(\s*)(rtk\s+(proxy\s+)?)?rm\s` + มี r-flag `\s(-[A-Za-z]*[rR][A-Za-z]*|--recursive)(\s|$)` + มี f-flag `\s(-[A-Za-z]*f[A-Za-z]*|--force)(\s|$)`
- pattern อื่น: `git reset --hard`, `git clean -[A-Za-z]*f|--force`, `find ... -delete`, `git push --force|--force-with-lease|-f`
- branch protection: `git (commit|push)` ขณะ branch = main/develop หรือ push ระบุ main/develop → block
- ทุก pattern รองรับ prefix `rtk (proxy )?` (rtk hook rewrite คำสั่งบนเครื่องนี้); ห้ามใช้ prefix-skip grep/echo (เป็น bypass hole); ทดสอบ standalone ทั้ง positive/negative ก่อน wire; hooks snapshot ตอนเริ่ม session — มีผล session ถัดไป

### A3. Wire secret-guard.sh ที่เขียนเสร็จแล้วแต่ลอยอยู่ [adjust, high/low]

ไฟล์: `.git/hooks/pre-commit`, `.claude/settings.json`, `~/.claude/hooks/secret-guard.sh`

- `~/.claude/hooks/secret-guard.sh` ครบเครื่อง (Omise/Stripe/AWS/GitHub patterns + entropy + remediation) แต่ไม่ถูกอ้างที่ไหนเลย
- ทำ: (1) `ln -sf ~/.claude/hooks/secret-guard.sh .git/hooks/pre-commit` (symlink ไม่ใช่ cp — แก้ต้นฉบับครั้งเดียวมีผลทันที)
- (2) ห้ามทำ wrapper รัน secret-guard ใน PreToolUse (timing gap: ตอน hook ยิง ไฟล์ยังไม่ staged → ตรวจไม่เจอแบบ vacuous pass) — แทนด้วย PreToolUse entry เล็กกัน bypass:
  `C=$(jq -r '.tool_input.command // empty'); echo "$C" | grep -qE 'git commit[^|;&]*(--no-verify|[[:space:]]-n([[:space:]]|$))' && { echo 'Blocked: --no-verify ข้าม secret-guard pre-commit hook' >&2; exit 2; }; exit 0`
- (3) ลบ dead call `~/.claude/scripts/afk-notify.sh` ที่ secret-guard.sh:102 (ไฟล์ไม่มีจริง)

### ST3. ลบ rules orphan: api-design.md + components.md [apply, medium/low]

ไฟล์: `.claude/rules/api-design.md`, `.claude/rules/components.md`

- ทั้งคู่ frontmatter `paths:` ชี้ไป directory ที่โปรเจกต์ไม่ได้ใช้จริง = ไม่เคยถูกโหลดตั้งแต่ commit แรก; api-design.md ขัด Non-Goals (ไม่มี backend); components.md มี placeholder ค้าง + เนื้อหาซ้ำ tech.md
- ทำ: ลบทั้งคู่ (stack-nextjs.md ครอบ domain component อยู่แล้ว — one domain per file)

### W1. ลบย่อหน้า stale ใน spec-implement ที่สั่งกลับด้านกับ default all-in-one [adjust, high/low]

ไฟล์: `.claude/skills/spec-implement/SKILL.md`

- บรรทัด 27-29 สั่งห้ามรัน "all" ใน session เดียว — ขัด spec-tasks:30-36 (default = all-in-one) และ Discovery ใน lessons.md (multi-session แพงกว่า ~39%); git ยืนยัน drift (spec-tasks แก้ใน bcf2afc แต่ spec-implement ค้าง)
- ทำ: แทนด้วยข้อความอังกฤษ (ตามภาษาไฟล์เดิม): default coupled feature = ALL tasks ใน ONE session ตาม dependency order (`/spec-implement all` หรือ `scripts/pane-loop.sh <feature> all-in-one`); แยก session เฉพาะ task อิสระจริงหรือ isolate CORE accuracy (conscious trade ไม่ใช่ cost win); session ยาวเสี่ยง drift → persist state ลง tasks.md ที่ task boundary

---

## Tier 2 — ปิด loop ของ spec workflow

### W3. Approval state durable ใน artifact + กลไก resolve active spec [adjust, high/low]

ไฟล์: skills spec-requirements / spec-design / spec-tasks / spec-implement / spec-analyze / spec-pbt / spec-quick

- ปัญหา: "approved" อยู่ในบทสนทนาเท่านั้น — หลัง /clear หรือ headless session แยกไม่ออกว่า gate ไหนผ่าน; 4 skills อ้าง "the active spec" โดยไม่มี argument-hint ทั้งที่ specs/ มี 2 feature; spec-pbt แนะนำ dedicated PBT framework ที่ไม่ได้ติดตั้ง (ชนกฎ tech.md)
- ทำ: (ก) template artifact ขึ้นหัว `> Status: draft` → user approve explicit แล้ว flip เป็น `> Status: approved <YYYY-MM-DD>` ก่อน phase ถัดไป; downstream skill เช็ก status ต้นน้ำ ยัง draft = เตือนภาษาไทยแบบ warning (ถามยืนยัน ไม่ hard block); spec-quick เขียน `> Status: approved <date> (quick, no gates)` ทันที
- (ข) เพิ่ม `argument-hint: <feature-folder (optional)>` ใน spec-analyze/spec-design/spec-tasks/spec-pbt + rule: หลายโฟลเดอร์และไม่ระบุ → list แล้วถาม ห้ามเดา; spec-implement ใช้กติกา headless-safe: เลือกโฟลเดอร์เดียวที่มี task id ที่ขอยังเป็น `[ ]`; เข้าเงื่อนไขมากกว่า 1 → ถาม (รองรับ pane-loop ที่ส่ง `/spec-implement <id>` ไม่แนบ feature)
- (ค) spec-pbt เพิ่ม guard: เช็ก package.json ก่อน — PBT framework ไม่มี = ขออนุมัติตามกฎ tech.md ก่อนเพิ่ม

### W4 + ST7. Promote บทเรียนเข้า skill ต้นเหตุ + script ตรวจ ground truth [adjust, high/low+medium]

ไฟล์: `.claude/skills/spec-tasks/SKILL.md`, `.claude/skills/spec-implement/SKILL.md`, `scripts/spec-state.sh` (ใหม่)

- (ก) spec-tasks เพิ่ม rule (อังกฤษ, หลังบรรทัด "Map each task to a whole REQ..."): ก่อน STOP ทำ reverse coverage check — ทุก REQ-N ต้องอยู่ใน `Satisfies:` ของอย่างน้อย 1 task; uncovered = blocker ประกาศดัง ยกเว้นเฉพาะ declared out-of-scope ที่ approve แล้ว (ปิดช่อง Articles/REQ-13 ตกร่อง)
- (ข) spec-implement แทรก step 0 ก่อน loop: reconcile tasks.md กับ filesystem — filesystem คือ ground truth (checkbox/git log โกหกได้, untracked ไม่โผล่ใน `git diff --stat`); checkbox ขัดความจริง = แก้ checkbox + จด reconciliation ใน tasks.md ก่อน implement
- (ค) ตัวรันของ (ข) = `scripts/spec-state.sh <feature>` (วางที่ root scripts/ ตาม convention เดิม — ไม่ใช่ skill-local): พิมพ์ 4 ก้อน [a] ls artifacts ใน specs/<feature>/ [b] checkbox `grep -n '^- \[.\]'` จาก tasks.md [c] `git log --oneline -15` + `git status --short` (เห็น `??`) [d] list the project source/test directories + detect the project manifest; เพิ่มหนึ่งบรรทัดใน spec-implement: "Before starting, run `scripts/spec-state.sh <feature>`..."
- ไม่ทำ scaffold-spec.sh (ขัด spec-new "Do NOT generate any artifact yet")

### W5. ปิด loop ของ /spec-analyze: repair เข้า requirements.md + propagate [adjust, high/medium]

ไฟล์: skills spec-analyze / spec-design / spec-tasks

- ปัญหา: spec-analyze เป็น report-only ไม่มีใคร apply fix กลับ; หลักฐานจริง: requirements.md ของ feature spec หนึ่งอ้างรหัส finding L1/A1/C2/G2/A4/A5 ที่ dangle ไปบทสนทนาที่หายแล้ว; working agreement "propagates to design and tasks" ไม่มี skill ไหนทำจริง
- ทำ: (ก) เพิ่มหมวดที่ 5 "Unstated assumptions" + concurrent/interaction ใน Gaps + reason ข้าม requirement เป็นชุด; (ข) ทุก finding = คำถามมีตัวเลือก fix 2-3 ทาง + "ตอบเอง" + "ข้าม — ambiguity ตั้งใจ" batch ภาษาไทยข้อความเดียว; (ค) หลัง user ตัดสิน: เขียน fix เข้า requirements.md (คง REQ ID) + บันทึกทุก finding รวม dismissed ลง section "Edge Cases & Open Questions" พร้อม anchor commit hash (`git log -1 --format=%h -- <requirements.md>`); (ง) re-run incremental: `git diff <anchor>` หา REQ ที่เปลี่ยน focus เฉพาะนั้น + interaction; ไม่มี anchor = audit เต็ม
- เพิ่มย่อหน้า "sync mode" ใน spec-design + spec-tasks (requirements เปลี่ยนหลัง artifact มีแล้ว = patch เฉพาะส่วน ไม่ regenerate ทับ [x]/decision)
- ระวัง: skill 17 บรรทัด อย่าบวมเกิน ~2 เท่า; คง "Do NOT silently edit" โดยขยายว่า edit ได้เฉพาะหลัง user ตัดสิน

### A5. Deterministic traceability: scripts/spec-trace.sh [adjust, high/medium]

ไฟล์: `scripts/spec-trace.sh` (ใหม่), skills spec-tasks / spec-implement

- ทำ: wrapper bash เรียก python3 (ห้าม naive grep — verify พิสูจน์ว่า reference grammar จริงซับซ้อน):
  parser รองรับ criterion `^- N.M ` ใต้ heading `## REQ-N:`; expand bare suffix list (`15.1, 15.3`), dash range (`17.1-17.4`), prefix form (`REQ-1.2`), whole-REQ (`REQ-1 (all criteria)`), tolerate annotation `(partial)`
- ตรวจ: ทุก criterion ต้องอยู่ใน design.md (Requirement Traceability) + ถูกอ้างโดย task (`Satisfies:`) — orphan = exit 1 + รายการไทย; ผ่าน = เงียบ exit 0
- EARS lint: บรรทัด `^- N.M ` ต้องมี THE SYSTEM SHALL / WHEN / WHILE / WHERE / IF...THEN (ทดสอบกับ spec จริง 70/70 ผ่าน)
- bugfix spec ไม่มี `## REQ-N:` = skip พร้อมแจ้ง 1 บรรทัด exit 0
- wire: spec-tasks (รันก่อนปิด tasks.md) + spec-implement (รันก่อน mark task สุดท้าย/assembly)

### W7. spec-quick: front-loaded questions + self-check [adjust, medium/low]

ไฟล์: `.claude/skills/spec-quick/SKILL.md`

- Kiro ระบุ batched clarifying questions ก่อน generate คือ "the key interaction point" ของโหมดไม่มี gate — spec-quick ปัจจุบันกระโดดเข้า create folder ทันที
- ทำ (อังกฤษตามไฟล์เดิม): Step 0 conditional — $ARGUMENTS ตอบครบ who/what/why/success/edge/constraints = ข้าม; ไม่ครบ = ถาม ONE batched message (ไทย) แล้วเดินรวดเดียว; ท้าย step 2 self-check inline เทียบ 4 หมวดของ spec-analyze (ไฟล์จริงมี 4 ไม่ใช่ 5) แก้ก่อนเขียน design; ท้าย step 4 print task list แบบ compact (หนึ่งบรรทัด/task: title + REQ IDs) เป็น interrupt point ฟรี — ไม่หยุดรอ approval

---

## Tier 3 — โครงสร้าง steering

### ST2. stack-nextjs.md path-scoped จริง + แยก browser-verify ไป references/ [adjust, high/medium]

ไฟล์: `.claude/rules/stack-nextjs.md`, `.claude/skills/spec-implement/SKILL.md` + `references/browser-verify.md` (ใหม่), `.claude/skills/spec-retro/SKILL.md`

- 47.8KB/49 bullets, glob `**/*.{ts,tsx}` = de-facto always-on; ~ครึ่งเป็น verify recipes ที่ใช้เฉพาะช่วง verify; lesson ขัดกันเอง (บรรทัด 19 ชดเชย scrollbar +15 เสมอ vs บรรทัด 45 headless = overlay 0px)
- ทำ: (1) แคบ paths เหลือ glob ของ UI source directory + ไฟล์ config ของโปรเจกต์ + แก้ header ให้ตรง; (2) ย้าย bullets verify-method ล้วน (19+45 merge เป็นข้อเดียว: ยืนยัน `document.documentElement.clientWidth === target` ก่อนเชื่อผล ชดเชยเฉพาะเมื่อไม่ตรง, 21, 26, 27, 41-43, 47, 51-53, 55, 56, 58, 63) → `references/browser-verify.md` (ไทย ไม่มี emoji); คง 44, 46, 54 (implementation patterns) โดยชี้ประโยค verify ไป references; (3) spec-implement เพิ่ม "Before any browser-based verification, Read references/browser-verify.md"; (4) spec-retro routing เพิ่มปลายทางที่สาม browser-verify.md
- ทำระหว่าง session (ไม่แก้กลางงาน) เพื่อรักษา prefix cache

### ST4. เลิกโหลด product/tech/structure ซ้ำสองทาง [apply, medium/low]

ไฟล์: `CLAUDE.md` (โปรเจกต์)

- โหลดซ้ำ 2 กลไก: @-import ใน CLAUDE.md + rules auto-loader (พิสูจน์จาก lessons.md ที่อยู่ใน context โดยไม่มี @) ≈ เสี่ยง double ~3.5k tokens; ทั้งสามไฟล์ไม่มี paths frontmatter = always-rules แน่นอน ถอด @ ปลอดภัย
- ทำ: แทนสามบรรทัด `See @.claude/rules/...` ด้วย pointer ธรรมดาไม่มี @; เปิด session ใหม่ตรวจหนึ่งครั้งว่า rules ยังครบ

### ST5. ย้าย cost-accounting จาก lessons.md ไป references/ ของ spec-retro [adjust, medium/low]

ไฟล์: `.claude/rules/lessons.md`, `.claude/skills/spec-retro/SKILL.md` + `references/cost-accounting.md` (ใหม่)

- lessons.md บรรทัด 12+18 (~4.3KB จาก 9.9KB) อยู่ใน prefix ทุก turn แต่ใช้เฉพาะตอน retro
- ทำ: ย้ายบรรทัด 12 เต็ม + ส่วน "cost ของ multi-session = sum ของ ledger" จากบรรทัด 18 → references/cost-accounting.md (ไทย); lessons.md เหลือ kernel: "cost จริงอ่านจาก ledger .cost.total_cost_usd เท่านั้น ห้าม recompute (overcount 1.6-3.7x) — รายละเอียด: references/cost-accounting.md"; ลบบรรทัด 18 (kernel การตัดสินใจอยู่ครบที่ pane-loop.md:2,22 + spec-tasks:30-36 แล้ว); spec-retro step 3 routing เพิ่ม: cost mechanics ใหม่เขียนลง references ไม่ใช่ lessons.md
- หมายเหตุจาก verify: pane-loop เป็น command (`.claude/commands/pane-loop.md`) ไม่มี SKILL.md

### ST6. Sync steering กับ ground truth + ขั้น steering sync ใน spec-retro [adjust, medium/low]

ไฟล์: `.claude/rules/tech.md`, `structure.md`, `lessons.md`, `.claude/skills/spec-retro/SKILL.md`

- drift จริง 3 จุด: มี dependency ที่ใช้จริงแต่ tech.md ไม่บันทึก (ขัด Rule ของไฟล์เอง); test runner ไม่อยู่ใน Tooling; structure.md ขาดไฟล์จริงหลายตัว; lessons.md:10 บอก bump test runner เวอร์ชันใหม่แล้วแต่ branch ยังค้างเวอร์ชันเดิม
- ทำ: tech.md เพิ่ม dependency ที่หลุดบันทึก (ระบุตรงๆ ว่า "ถูกเพิ่มระหว่าง implementation โดยไม่มีบันทึกอนุมัติ — บันทึกให้ตรง ground truth, approve PR นี้ = อนุมัติย้อนหลัง" ห้ามเขียน "อนุมัติแล้ว" ลอยๆ) + test runner ใต้ Tooling; structure.md เติมไฟล์ขาด (component/module/logic + test ที่ co-located) หรือกำกับ "ตัวแทนหลัก ไม่ exhaustive"; lessons.md:10 เติมท้าย "(bump เวอร์ชันใหม่ทำใน scaffold session ที่ไม่ถูก commit; branch ปัจจุบันยังค้างเวอร์ชันเดิม — vuln chain ต้อง audit ใหม่)"
- institutionalize: spec-retro เพิ่มขั้น "Steering sync" ก่อน Commit — เทียบ manifest ของโปรเจกต์กับ tech.md, ไฟล์ใหม่ใน source directory กับ structure.md, glob frontmatter ใน rules ว่ายัง match path จริง

---

## Tier 4 — ขยายความสามารถ

### W2. ทำเส้นทาง Design-First ให้ใช้ได้จริง [adjust, medium/medium]

ไฟล์: skills spec-design / spec-requirements, `.claude/agents/spec-architect.md`, `CLAUDE.md` (1 บรรทัด)

- spec-new โฆษณาเส้นทาง Design → Requirements → Tasks แต่ spec-design บังคับอ่าน requirements.md ก่อนเสมอ = พังตั้งแต่ skill ที่สอง; โปรเจกต์นี้เข้าโปรไฟล์ Design-First ของ Kiro พอดี (stack pin ตายตัว)
- ทำ: (1) CLAUDE.md เพิ่มวงเล็บหนึ่งบรรทัดตรง non-negotiable workflow: "(Design-First swaps 1 and 2 — same approval gates)" ปลดความขัดแย้ง constitution กับ skill; (2) spec-design เพิ่ม conditional design-first mode (input = คำตอบ /spec-new + rules; ถาม HLD/LLD หนึ่งคำถาม; งด traceability table; เพิ่ม "## Non-Functional Considerations" เฉพาะโหมดนี้); delegate ไป spec-architect ต้องส่ง design-first context (แก้ spec-architect.md ~2 บรรทัดรองรับ); (3) spec-requirements เพิ่มโหมด derive จาก design.md (REQ cite design section; sync ทิศเดียว: design เป็น upstream) + backfill traceability table กลับเข้า design.md หลัง derive; (4) ขยาย EARS template จาก 3 เป็น 5 patterns (เพิ่ม ubiquitous + WHERE) — แก้ gap ที่ critic ชี้ด้วย

### W6. ยกระดับ spec-bugfix เต็ม shape ของ Kiro Bugfix Spec [adjust, medium/medium]

ไฟล์: `.claude/skills/spec-bugfix/SKILL.md`, `.claude/skills/spec-implement/SKILL.md` (1 บรรทัด)

- ความเสียหายเกิดแล้วในรอบ bugfix รอบหนึ่ง: requirements ประกาศ do-not-modify scope ชัด แต่ tasks กลับแก้ไฟล์ที่ห้ามแตะ, placeholder `?` ถูก commit, บาง B-ID ไม่มี assertion, regression test จับ implementation ไม่จับ failure mode
- ทำ: rewrite skill — (ก) intake batch ถามไทย 4 ข้อ (repro steps / current / expected / constraints รวม do-not-modify เป็น hard scope); (ข) Phase 2 เขียน `bugfix.md` 3 sections: "Current Behavior (Defect)" รูป WHEN...THEN พร้อม repro steps ที่รันได้จริง (หน้า/viewport/คำสั่ง/ค่าที่วัด), "Expected Behavior" ใช้ SHALL, "Unchanged Behavior" ใช้ SHALL CONTINUE TO + B-ID; STOP gate ก่อน Phase 3; (ค) validation 3 มิติ: repro test RED ก่อน fix → GREEN หลัง fix → ทุก B-ID มี assertion 1:1 ที่ observable failure mode (ไม่ใช่ internal implementation — อ้าง regression test ที่จับ implementation แทน failure mode เป็น anti-pattern); (ง) อุดรอยต่อ: spec-implement:14 เปลี่ยน "requirements.md" เป็น "requirements.md (หรือ bugfix.md สำหรับ bugfix spec)"
- ไม่ migrate bugfix spec เดิมที่ปิดไปแล้ว

### A6. Harden agent contracts [adjust, medium/low]

ไฟล์: `.claude/agents/pbt-runner.md`, `bug-investigator.md`, `spec-architect.md`, skills spec-design / spec-pbt

- (1) pbt-runner: เปลี่ยน `model: opus` (หรือ inherit) — sonnet กับ CORE domain ขัด memory flow-quality-over-cost; เพิ่ม contract: runner = the project test runner (declared via SDD_TEST_CMD env, or a package.json test script for a Node project), test ต้องอยู่ในthe project test directory ที่ co-located กับ logic under test เท่านั้น (นอก path = ไม่ถูกรัน = เขียวปลอม), ทุก test อ้าง REQ ID, dedicated PBT framework ยังไม่มี — ใช้ randomized-loop บน the project test runner, จะเพิ่มต้องขออนุมัติ, รายงานไทย
- (2) bug-investigator: "reproduce mentally" → "Reproduce จริงด้วย Bash เมื่อรันได้; วิเคราะห์จากโค้ดเฉพาะเมื่อรันไม่ได้ + ระบุใน report ว่าไม่ได้รันจริง" + รายงานไทย
- (3) spec-architect: เพิ่มโหมด critique (adversarial reviewer ไม่ใช่ producer) + ไทย
- (4) spec-design:21-22 + spec-pbt:21 แทน "consider delegating" ด้วยเกณฑ์ตัดสิน 1 บรรทัด (delegate เมื่อแตะ CORE logic)
- (5) ต้นตอจริงอยู่ที่ spec-pbt:14-16 เรียก dedicated PBT framework ว่า "the project's framework" — แก้เป็น randomized-loop บน the project test runner (declared via SDD_TEST_CMD env, หรือ package.json test script สำหรับ Node) ที่มีอยู่ การเพิ่ม PBT framework แยกต้องขออนุมัติ

---

## Tier 5 — gap จาก critic (22 จุด) ที่ยังไม่มีเจ้าภาพ

จัดกลุ่มตามน้ำหนัก:

ควรยกเป็นงานเพิ่มทันที (เจ็บจริง/เคยพังจริง):

1. spec-requirements ไม่ได้ rec ตรงตัว — EARS template มี 3/5 patterns + ไม่มี guard กัน criterion non-atomic/subjective (ถูก W2(4) ครอบบางส่วน — ตอน implement ให้ปิดทั้งสองจุด)
2. PreCompact hook + breadcrumb กลางงาน — context discipline ใน CLAUDE.md ตอนนี้พึ่งความจำโมเดลล้วน; เพิ่ม PreCompact hook inject คำสั่ง persist active-task state ก่อน compact
3. spec-file guard hook — PreToolUse Edit บน `.ai/specs/**/requirements.md` เตือนเมื่อแก้ spec ที่ approved ระหว่างมี task ค้าง (เสริม W3 status field พอดี)
4. completion evidence ใน tasks.md — บังคับบันทึกผลเทสต์/viewport ที่ตรวจ/deviation ตอน mark [x] (เสริม A1 task-gate)
5. spec-architect ซ้ำ outline กับ spec-design (drift risk) — repurpose เป็น design-reviewer/requirements-auditor แบบ fresh-context adversarial (audit ชี้ว่า "คุ้มสุดและถูก")
6. backfill artifact เดิมที่ commit แล้ว: ID format asymmetry (`1.1` vs `REQ-1.1` — A5 parser รองรับแล้วแต่ artifact ควร normalize), REQ-15.6 แทรกผิดลำดับ, placeholder ใน bugfix tasks.md

รอง (ทำเมื่อแตะไฟล์นั้นอยู่แล้ว): 7. dedup policy สำหรับกฎซ้ำหลายที่ (task-sizing 3 ที่, EARS 2 ที่) — กำหนด single source + pointer 8. นโยบาย prune/merge lesson ที่ขัดกันเองใน stack-nextjs.md (ST2 แก้คู่ 19/45 แล้ว แต่ไม่ได้วางกติกากันเกิดซ้ำ) 9. product.md ทำตัวเป็น mini-spec ของฟีเจอร์เดียว (rubric/จำนวนการ์ดซ้ำ requirements.md) — จะ stale เมื่อมีฟีเจอร์ถัดไป 10. dedup เนื้อหา tech.md Hard Constraints กับ structure.md Anti-Patterns (~4 กฎซ้ำ) 11. per-task history ที่ตรวจสอบได้ (commit ที่ task boundary แทน commit เดียวจบ feature) 12. design sync หลัง implementation decision (เช่น การ pin เวอร์ชันของ styling system ไม่ถูกบันทึกใน design.md) 13. spec-analyze report เป็นไฟล์ artifact ใน specs/<feature>/ (W5 บันทึกใน requirements.md section — พอใช้ แต่ถ้าอยาก audit trail เต็มค่อยแยกไฟล์) 14. hook verification discipline — smoke-test ว่า hook ยิงจริง (`claude --debug`) — สาเหตุที่ 3 hooks ตายเงียบนาน 15. Bash อ่าน secret (`cat .env`) — deny-Read ไม่ครอบ Bash; A3 ครอบเฉพาะจุด commit 16. cache-ts.sh ใน global settings ยิงทุก PostToolUse ไม่มี matcher (spawn process ทุก tool call) — อย่างน้อยบันทึกเป็น trade-off ที่รู้ตัว 17. pane-loop.md:25 ชี้แหล่ง bypassPermissions ผิดไฟล์ (อยู่ global ไม่ใช่ project) 18. ภาษา output (ไทย) ใน agent definitions — A6 ครอบ 3 ตัว project แล้ว เหลือ global 3 ตัว 19. Kiro agentic hook actions (NL-prompt hook) — ฝั่ง Claude Code ทำได้ผ่าน hook JSON `additionalContext` / UserPromptSubmit injection; ตอนนี้ rec ทั้งหมดเป็น deterministic shell — ตัดสินใจ explicit ว่าจงใจไม่ใช้ 20. Kiro steering manual inclusion mode (`#steering-name`) — map ได้กับ @-mention / skill references/ — ยังไม่ถูกใช้เป็น pattern

## ลำดับ implement ที่แนะนำ

1. Tier 1 ทั้งก้อน (ครึ่งวัน — ส่วนใหญ่ low effort, ผลทันที: enforcement ที่คิดว่ามีแต่ไม่มี กลับมามีจริง)
2. Tier 2: W3 → W4+ST7 → A5 → W5 → W7 (script ก่อน เพราะ W4/W5 อ้างใช้)
3. Tier 3 ระหว่าง session ว่าง (แตะ always-on prefix — ทำตอนไม่มีงานค้างเพื่อไม่เสีย cache กลางงาน)
4. Tier 4 + Tier 5 ข้อ 1-6 เมื่อ Tier 1-2 นิ่ง
