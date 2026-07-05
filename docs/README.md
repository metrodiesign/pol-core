# คู่มือการทำงาน (Operating Manual)

คู่มือปฏิบัติของโปรเจกต์นี้ — spec-driven development บน Claude Code พร้อม automation,
cost tracking และ retrospective. อ่านตามลำดับสำหรับคนใหม่ หรือกระโดดเข้าหัวข้อที่ต้องการ.

## สารบัญ

1. [Spec-driven flow + gates](01-spec-driven-flow.md) — วงจร requirements -> design -> tasks
   -> implement -> retro, approval gate, EARS, การ size task, slash command ทั้งหมด
2. [Automation (pane-loop)](02-automation.md) — รันหลาย task อัตโนมัติด้วย interactive pane
   (สรุป + ลิงก์คู่มือเต็ม `../scripts/pane-loop.md`)
3. [Cost ledger + retrospective](03-cost-and-retro.md) — cost จริงต่อ session/task, ledger,
   สคริปต์ cost, การทำ retro และ promote บทเรียน
4. [Git / PR + rules](04-git-pr-and-rules.md) — นโยบาย branch/PR, secrets/CI/destructive,
   conventions (structure/tech/product)
5. [Hooks / guardrails](05-hooks.md) — ชั้น deterministic hook (destructive/secret/spec-edit/
   task-gate/precompact) ที่ block/warn อัตโนมัติรอบ tool call
6. [GitHub Issues (teammate visibility)](06-github-issues.md) — เชื่อม spec -> GitHub Issues,
   epic + sub-issue, label, ผูก PR, CI gate

## เอกสารอ้างอิง (reference) — คู่มือโมดูล / สถาปัตยกรรม / convention

- [platform modules (บริบท+บทบาท)](reference/platform-modules.md) — module map ระดับแพลตฟอร์ม:
  บริบท/บทบาทของทั้ง 14 โมดูล + สถานะ as-built + **target เชิง API ทุกโมดูล (normative, 2026-07-05)**
  + ช่องว่างเทียบเป้าหมาย + ทะเบียน ADR ค้างตัดสิน (จุดเริ่มอ่านภาพรวม)
- [src structure](reference/src-structure.md) — โครงสร้าง `src/`, layer, การวาง handler/repository
- [entity fields](reference/entity-fields.md) — schema + field ของทุก entity/ตาราง
- [Admin module (SSO BFF + FE integration)](reference/admin-module.md) — โมดูล admin SSO (BFF) + sequence + การต่อ FE กับ admin API
- [Producer module (SSO BFF)](reference/producer-module.md) — โมดูล producer SSO (BFF) + sequence
- [payment orchestration modules](reference/payment-orchestration-modules.md) — โมดูลฝั่ง payment
  + ภาค 8 = Canonical Payment API **target design (normative)**: Payment/PaymentAttempt/Webhook inbox/Routing
- [Search / Filter / Sort](reference/search-filter-sort.md) — convention SFS + pagination บน list endpoint (JSON-DSL, EF Core, SQL Server, RLS)

## แหล่งความจริง (source of truth) — ห้ามขัดกับไฟล์เหล่านี้

| เรื่อง                   | ไฟล์                                           |
| ------------------------ | ---------------------------------------------- |
| รัฐธรรมนูญ workflow      | `../CLAUDE.md`                                 |
| มาตรฐานโปรเจกต์          | `../.claude/rules/{product,tech,structure}.md` |
| บทเรียนสะสม              | `../.claude/rules/lessons.md`                  |
| spec ของแต่ละฟีเจอร์     | `../.ai/specs/<feature>/`                  |
| นิยาม skill (slash)      | `../.claude/skills/spec-*/`                    |
| hooks (guardrails)       | `../.claude/hooks/` + `../.claude/settings.json` |
| agent definitions        | `../.claude/agents/`                           |
| อ้างอิง Kiro->CC ละเอียด | `../claude-code-spec-driven-workflow.md`       |

> เอกสารใน `docs/` เป็นคู่มือ "วิธีทำงาน" — เมื่อเนื้อหาขัดกับ `CLAUDE.md` หรือ `.claude/rules/`
> ให้ยึดไฟล์ต้นทางเสมอ แล้วอัปเดต docs ตาม.
