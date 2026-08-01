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
- [Merchant-user module (OIDC BFF)](reference/merchant-user-module.md) — โมดูล merchant-user SSO (BFF) + sequence
  + endpoint + RBAC + การต่อ FE กับ merchant-user API
- [payment orchestration modules](reference/payment-orchestration-modules.md) — โมดูลฝั่ง payment
  + ภาค 8 = Canonical Payment API **target design (normative)**: Payment/PaymentAttempt/Webhook inbox/Routing
- [Search / Filter / Sort](reference/search-filter-sort.md) — convention SFS + pagination บน list endpoint (JSON-DSL, EF Core, SQL Server, RLS)
- [ระบบดึงข้อมูลกรมธรรม์จากอีก 2 บริษัท (ฉบับไม่ใช้ศัพท์เทคนิค)](reference/products.md) —
  อธิบาย `Products.Application/Ports` แบบให้คนที่ไม่ได้เขียนโค้ดเข้าใจว่าทำอะไร/ทำไมต้องมี

## แหล่งความจริง (source of truth) — ห้ามขัดกับไฟล์เหล่านี้

| เรื่อง                                                    | ไฟล์                                             |
| --------------------------------------------------------- | ------------------------------------------------ |
| รัฐธรรมนูญ workflow (Claude adapter — bootstrap `.ai/`)   | `../CLAUDE.md`                                   |
| front door ของ agent อื่น (Codex/OpenCode/Pi) — read order + golden rules | `../AGENTS.md`                 |
| product / บริบท: แพลตฟอร์มนี้คืออะไร ทำไม                 | `../.ai/shared/PROJECT_CONTEXT.md`               |
| มาตรฐานโค้ด + stack + hard constraints                    | `../.ai/shared/CODING_STANDARDS.md`              |
| โครงสร้างโฟลเดอร์ / naming / การจัดไฟล์                   | `../.ai/shared/ARCHITECTURE.md`                  |
| บทเรียนสะสม (promoted จาก retro)                          | `../.ai/shared/LESSONS.md`                       |
| phase / approval gate / task sizing / Definition of Done  | `../.ai/shared/TASK_PROTOCOL.md`                 |
| spec ของแต่ละฟีเจอร์                                      | `../.ai/specs/<feature>/`                        |
| นิยาม skill (slash)                                       | `../.claude/skills/spec-*/`                      |
| persona ของ subagent (vendor-neutral, ตัวจริง)            | `../.ai/roles/`                                  |
| agent definitions (Claude wrapper — tools/model + delegate ไป `.ai/roles/`) | `../.claude/agents/`           |
| logic ของ guard/gate ทุกตัว (single source ที่ทุก harness เรียก) | `../.ai/bin/`                              |
| hooks รอบ tool call (Claude adapter บาง ๆ)                | `../.claude/hooks/` + `../.claude/settings.json` |
| enforcement floor ระดับ git: `pre-commit` = secret scan + Evidence check, `pre-push` = block main/develop + force push | `../.githooks/` |
| อ้างอิง Kiro->CC ละเอียด                                  | `../claude-code-spec-driven-workflow.md`         |

> `.claude/rules/*.md` ยังถูก auto-load ทุก turn แต่เป็นแค่ **pointer stub** ชี้ไป `.ai/shared/*`
> — อย่าแก้ stub, แก้ที่ไฟล์ canonical.

> เอกสารใน `docs/` เป็นคู่มือ "วิธีทำงาน" — เมื่อเนื้อหาขัดกับ `CLAUDE.md` / `AGENTS.md` หรือ
> `.ai/shared/` ให้ยึดไฟล์ต้นทางเสมอ แล้วอัปเดต docs ตาม.
