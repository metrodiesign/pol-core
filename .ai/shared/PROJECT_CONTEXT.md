> Canonical source for ALL agents (Claude loads via .claude/rules stub; Codex/OpenCode/Pi read directly).
> แก้ที่นี่ที่เดียว — single source of truth.

# Product Overview

> Source: `docs/reference/payment-orchestration-modules.md` (รายละเอียดเต็มของ Payments + ภาค 8 =
> Canonical Payment API **target design normative**, รับเข้า 2026-07-05) ·
> `docs/reference/platform-modules.md` (module map + สถานะ as-built ต่อฟีเจอร์ + target เชิง API
> ทุกโมดูล + ทะเบียน ADR ค้างตัดสิน)

## Purpose

**Internal Insurance Sales + Payment Platform (captive)** — SaaS ขาย**แผนประกันภัย** multi-tenant
ที่ให้บริษัทในเครือ (vPrivilege / vCommerce / vSouvenir) ขายแผนประกัน (`Product` พก `SumInsured`/
`CoverageDurationDays`/`Insurer` จริง ไม่ใช่ generic catalog item) ให้ลูกค้า พร้อมรับชำระเงินผ่าน PSP
ที่ถือใบอนุญาตอยู่แล้ว (2C2P + Omise/Opn) แบบ **redirect-only** โดย **เงินจริงไม่วิ่งผ่านแพลตฟอร์ม** —
เรา "ใช้" PSP ไม่ใช่ "เป็น" PSP; แพลตฟอร์มเป็น sales + payment channel เท่านั้น — **ไม่ออกกรมธรรม์เอง**
(ดู Non-Goals)

ระบบคือ scope เดียวกัน มี 5 โมดูล (Products · Cart · Checkout · Orders · Payments) คุยกันผ่าน
**Mediator (martinothamar/Mediator)** แบบ modular ไม่อ้างถึงกันตรง — โมดูลที่ build out มากสุดคือ Payments

## Target Users

- **Producer (Tenant Console)** — ตัวแทนประกันภัย / นายหน้าประกันภัย ในสังกัดบริษัทในเครือ:
  เลือกแผน/กรมธรรม์ → ตะกร้า → checkout → สร้าง Order เห็นเฉพาะข้อมูล tenant ตน (scope ด้วย `TenantId`)
- **ลูกค้า** — เปิดลิงก์หน้าสรุปคำสั่งซื้อ → กดยืนยัน → จ่าย (เท่านั้น) ผ่าน redirect ไปหน้า PSP
- **ทีมกลาง (Admin Console)** — internal-only: provision tenant, เก็บ PSP credential/config, ตั้ง routing, monitor, audit

## Problem It Solves

บริษัทในเครือต้องรับชำระเงินออนไลน์ แต่การ "เป็น" PSP เองทำให้เข้าข่ายใบอนุญาตประเภทที่ 3 (ธปท.)
และขยาย PCI scope. แพลตฟอร์มนี้แก้ด้วยโมเดล **captive + ไม่ถือเงิน**: เป็น merchant/orchestrator ที่
redirect ไปหน้า PSP เท่านั้น → คง **PCI SAQ A** รายนิติบุคคล, ใบอนุญาตอยู่ที่ PSP, เงิน settle จาก PSP
เข้าบัญชี merchant ของแต่ละบริษัทโดยตรง

## Key Features

- **5 SaaS modules** ผ่าน Mediator — Products → Cart → Checkout → Orders → Payments (จบที่ emit `PaymentPaid`)
- **แผนประกันเป็น field จริงบน `Product`** — `SumInsured`/`CoverageDurationDays`/`Insurer`, validate ตอน
  `Create`, เป็น source of truth เสมอ (server-side, ไม่รับจาก client) ทั้งราคาและเงื่อนไขประกัน
- **ผู้เอาประกันต่อ `OrderItem`** — จับข้อมูลผู้เอาประกัน (ชื่อ/เลขบัตร/วันเกิด) 1 คนต่อ 1 line ตอน checkout,
  snapshot เงื่อนไขประกัน (`SumInsured`/`CoverageDurationDays`/`Insurer`) เข้า line ไม่อ่านจาก `Product` สดๆ
  ภายหลัง; list/summary mask เลขบัตร (โชว์ 4 ตัวท้าย), detail read เผยเต็มพร้อมเขียน append-only reveal audit
  ต่อ line, customer summary ไม่โชว์วันเกิดเลย
- **2 console คนละแอป** — Tenant Console (public-facing, 3 บริษัทใช้ร่วม) + Admin Console (internal-only) บน backend/data ชุดเดียว เพื่อลด blast radius
- **PSP adapter** 2C2P + Omise/Opn — redirect-only ครบ 3 ช่องทาง (บัตร / PromptPay / ผ่อน), normalize เป็นสัญญาเดียว
- **Webhook = source of truth** — verify ลายเซ็น + idempotent + fetch-to-confirm ก่อนอัปเดตสถานะ (ไม่เชื่อ browser redirect)
- **Multi-tenant provisioning** — Admin สร้าง tenant + เก็บ PSP credential ลง vault (encrypt, แยก key ต่อ tenant)
- **Identity & RBAC** — Google SSO, hd-gate default-deny (Admin), register→approve (Tenant), maker-checker สำหรับ action อ่อนไหว
- **Notification (background)** — Orders ส่งลิงก์หน้าสรุปผ่าน Message Queue → Worker, retry backoff → DLQ, ลิงก์มี TTL
- **Reconciliation = reporting**, retry/dunning, idempotency, audit log (append-only)

## Business Objectives

- รับชำระ redirect-only ได้ครบ 3 ช่องทางทั้ง 2 PSP โดย **คง SAQ A** (ไม่แตะข้อมูลบัตรบนโดเมนเรา)
- **Multi-tenant isolation** เด็ดขาดที่ app layer (EF global query filter deny-default ต่อ merchant + sealed write
  guard, ทุก query/write กรอง `MerchantId`; ไม่ใช่ SQL RLS อีกต่อไป — supersede 2026-07-19, spec
  `rls-to-query-filter`) — backend ร่วมกันแต่ข้อมูลไม่รั่ว
- คงสถานะ **captive + ไม่ถือเงิน** → อยู่นอก funds flow เสมอ (ไม่เข้าข่ายใบอนุญาตประเภทที่ 3)
- จ่ายไม่ผิด/ไม่ซ้ำ: idempotency + verify amount/currency ตอน Orders รับ `PaymentPaid` (ไม่ใช่แค่ `PaymentId`)

## Non-Goals — ฟังก์ชันที่ "ห้าม implement"

> อยู่นอก scope โดยตั้งใจ — เพิ่มเข้ามาจะเปลี่ยนสถานะทางกฎหมาย + ขยาย PCI scope ทันที
> **เจอ requirement/ticket/ไอเดียที่นำไปสู่ข้อใดข้างล่าง → หยุดและถามก่อน อย่า implement เอง** แม้จะดู "เป็นประโยชน์"

1. **ห้าม settlement / payout engine** — ไม่มี money ledger / wallet / float / escrow / disbursement (อยู่นอก funds flow เสมอ)
2. **ห้าม billing / เก็บค่าบริการ** — ใช้ฟรี ไม่มี subscription / invoice / usage metering / fee deduction
3. **ห้าม public/self-serve onboarding** — allowlist เฉพาะ vPrivilege / vCommerce / vSouvenir ไม่ต่อ KYB/AML provider ภายนอก
4. **ห้ามแตะข้อมูลบัตร** — ไม่ collect/store/transmit/tokenize PAN, ไม่มี card field/hosted-fields/iframe/Omise.js บนโดเมนเรา
5. **ห้ามสร้างฟังก์ชันของ PSP/acquirer เอง** — ไม่มี acquiring, card scheme, 3DS/ACS, payment processing (เราใช้ PSP ไม่ใช่เป็น)
6. **ห้าม flow แบบ non-redirect** — ไม่ display-QR/iframe/hosted-fields บนหน้าเรา ใช้ full redirect ไปหน้า PSP เท่านั้น (คง SAQ A)
7. **Reconciliation = reporting เท่านั้น** — ห้ามลอจิกที่เคลื่อนเงิน/ปรับยอดจริง
8. **ห้าม policy issuance ใดๆ** — ไม่มี policy-number generation, policy document/PDF, issuance workflow —
   แม้มี `OrderItem` + ข้อมูลผู้เอาประกันครบแล้วก็ตาม (insurance-pivot) การเก็บข้อมูล 2 อย่างนี้ไม่ใช่การ
   issue policy; จบที่ "รับชำระเสร็จ → emit `PaymentPaid`" เสมอ ไม่มีขั้นถัดไป — รวมถึงห้าม claims /
   renewal / endorsement / underwriting / commission / reinsurance
