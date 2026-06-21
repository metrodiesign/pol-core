# pol-core

**Internal Payment Orchestration Platform (captive)** — SaaS อีคอมเมิร์ซประกันภัย multi-tenant
ที่ให้บริษัทในเครือ (vCentral / vCommerce / vSouvenir) รับชำระเงินผ่าน PSP ที่ถือใบอนุญาตอยู่แล้ว
(2C2P + Omise/Opn) แบบ **redirect-only** โดย **เงินจริงไม่วิ่งผ่านแพลตฟอร์ม** — เรา "ใช้" PSP ไม่ใช่ "เป็น" PSP

โมเดล: **captive · ไม่ถือเงิน · ใช้ฟรีภายในเครือ · PCI SAQ A**

## สถานะ

ยังไม่มี source code — repo อยู่ในเฟส spec-driven (specs come before code, ALWAYS).
ผลิตภัณฑ์ถูก spec ไว้แล้ว, รอ implement ผ่าน workflow gates.

## Stack

Modular Monolith (Clean Architecture + CQRS) · .NET 10 / ASP.NET Core 10 / C# 14 (LTS) ·
EF Core 10 + SQL Server 2025 Standard · **martinothamar/Mediator** 3.x (in-process, source-generated)

5 โมดูลคุยกันผ่าน Mediator: Products → Cart → Checkout → Orders → Payments (จบที่ emit `PaymentPaid`).
version pin เต็มดู `.ai/shared/CODING_STANDARDS.md`

## โครงสร้าง

| path | คือ |
|---|---|
| `.ai/shared/` | single source of truth — product canon, standards, architecture, protocols (ทุก agent อ่านที่นี่) |
| `.ai/specs/<feature>/` | spec artifact ต่อ feature: requirements → design → tasks |
| `.claude/` · `.codex/` · `.opencode/` | per-agent adapter (commands, hooks, agents) |
| `.githooks/` · `.github/` | enforcement floor: pre-commit/pre-push + CI |
| `docs/reference/` | สเปกอ้างอิงเต็มของ Payments module |

## เริ่มต้น (สำหรับ contributor / agent)

1. อ่าน read order: `.ai/shared/PROJECT_CONTEXT.md` → `ARCHITECTURE.md` → `CODING_STANDARDS.md` → `TASK_PROTOCOL.md`
   (AI agent: เริ่มที่ `AGENTS.md` หรือ `CLAUDE.md`)
2. เปิด git hooks: `git config core.hooksPath .githooks`
3. งานใหม่ผ่าน spec workflow เสมอ — ไม่ code ก่อน requirements → design → tasks

## กฎที่ขาดไม่ได้

- **Spec first** — ไม่ code ก่อนผ่าน gate (requirements → design → tasks)
- **redirect-only / ไม่แตะข้อมูลบัตร / ไม่ถือเงิน** — ดู Non-Goals ใน `PROJECT_CONTEXT.md`; เจอ requirement ที่ขัด → หยุดถามก่อน
- ห้าม push ตรง `main`/`develop` — ผ่าน PR + CI เสมอ
