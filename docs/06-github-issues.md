# 6. GitHub Issues (teammate visibility)

เชื่อม spec-driven workflow เข้ากับ GitHub Issues เพื่อให้เพื่อนร่วมทีมเห็นความคืบหน้า.
spec ใน `.ai/specs/<feature>/` ยังเป็น source of truth — issue เป็นแค่ภาพฉาย (projection)
ที่ sync จาก tasks.md แบบ idempotent (รันซ้ำไม่ duplicate).

## 6.1 โมเดล issue

- **Feature -> Epic issue** (1 ตัว/spec) ติด label `spec-epic` + `req-spine`. body มีตาราง REQ
  coverage (mirror `## Requirement Traceability` ใน design.md) ให้เห็น spine โดยไม่ต้องเปิด repo.
- **Task -> sub-issue** (native GitHub sub-issue ใต้ epic) ติด label `spec-task`. GitHub แสดง
  progress bar "N of M done" บน epic; แต่ละ task หยิบ/มอบหมาย/ปิดแยกได้.
- state ของ issue สะท้อน checkbox: task `- [x]` -> sub-issue ปิด; `- [ ]` -> open.

หนึ่ง task = หนึ่ง sub-issue (ไม่ re-slice) — รักษา coarse-task model + REQ spine เดิม. ด้วยเหตุนี้
ไม่ใช้ skill `to-issues`/`triage` ของ matt-pocock (มัน re-slice + บังคับ triage state machine).

## 6.2 คำสั่ง sync

```
/spec-sync-github <feature>            # สร้าง/อัปเดต/ปิด issue จาก spec (preview ก่อนรอบแรก)
/spec-sync-github <feature> --dry-run  # ดู preview เฉยๆ ไม่เขียนอะไรบน GitHub
/spec-sync-github <feature> --epic-only
```

- รันหลัง task เป็น `[x]` แล้ว (รันหลัง gate ไม่ใช่ระหว่าง) — on-demand ไม่มี hook ยิง network กลาง task.
- idempotency มาจาก manifest `.ai/specs/<feature>/.github-sync.json` (commit เข้า repo,
  เฉพาะคำสั่ง sync เขียน — อย่าแก้มือ, อย่าใส่ link ลง tasks.md).
- transport = GitHub MCP tools (ไม่ใช่ `gh` ตรง — RTK hook rewrite stdout ของ bash).

## 6.3 Labels

| label | ความหมาย |
| --- | --- |
| `spec:<feature>` | ทุก issue ของ spec นั้น (filter รวม) |
| `spec-epic` | epic issue (1 ตัว/feature) |
| `spec-task` | sub-issue ของ task |
| `req-spine` | epic ที่ถือตาราง REQ coverage |

สร้างครั้งเดียว (idempotent): `scripts/bootstrap-labels.sh [feature ...]`

## 6.4 ผูก PR กับ issue

1 feature branch -> 1 PR -> ใส่ `Closes #<epic>` ใน body (template `.github/pull_request_template.md`
เตรียมช่องให้). merge เข้า develop ปิด epic อัตโนมัติ. PR ที่ทำไม่ครบใช้ `Refs #<epic>`.

## 6.5 CI

`.github/workflows/ci.yml` รัน guard regression suite + secret scan + spec-trace (REQ coverage)
บน PR เข้า develop — gate ของ framework repo เอง ให้ทีมเห็น green check. ส่วน typecheck/test ราย
โปรเจกต์ไม่ได้อยู่ใน CI ของ repo นี้ — มันเป็นของ downstream project (พิสูจน์ task green ด้วย
`.ai/bin/gate-task.sh` ที่อ่าน `SDD_TYPECHECK_CMD` / `SDD_TEST_CMD` env, auto-detect package.json
scripts สำหรับ Node project). maintainer ตั้งให้ check `CI / guards + secret-scan + spec-trace`
เป็น required ใน branch protection ของ develop (ทำครั้งเดียวบน GitHub).

## 6.6 gh cheat-sheet (อ่าน/ทำมือ)

```
gh issue list --state open --json number,title,labels
gh issue view <n> --comments
gh issue comment <n> --body "..."
gh issue edit <n> --add-label "spec:<feature>"
gh issue close <n> --comment "..."
```

## 6.7 setup ครั้งเดียว (teammate)

1. `gh auth login` (ต่อคน)
2. `scripts/bootstrap-labels.sh` สร้าง label
3. maintainer: เปิด branch protection `develop` -> require check `CI / guards + secret-scan + spec-trace` + require PR review
4. รัน `/spec-sync-github <feature>` ครั้งแรก -> ตรวจ preview -> ยืนยัน
