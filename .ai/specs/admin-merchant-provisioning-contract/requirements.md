# Requirements: Admin Merchant Provisioning Contract

> Status: approved 2026-08-09 (quick, no gates)

## Overview

ยืนยัน contract ที่มีอยู่สำหรับ Super admin provision Merchant พร้อม PSP credentials แบบ atomic ก่อนผูก
Merchant user ตอนอนุมัติ โดยไม่เพิ่ม endpoint, entity, Draft state หรือ plaintext-secret surface ใหม่

## REQ-1: Admin provisioning boundary

**User Story:** As a Super admin, I want provision Merchant พร้อมค่าที่จำเป็นในคำขอเดียว, so that ระบบไม่มี Merchant ที่ตั้งค่าเพียงบางส่วน

**Acceptance Criteria (EARS):**

- 1.1 THE SYSTEM SHALL expose `POST /api/v1/merchants` สำหรับ Merchant provisioning
- 1.2 WHILE calling `POST /api/v1/merchants` THE SYSTEM SHALL require authenticated Super admin และ valid CSRF token
- 1.3 THE SYSTEM SHALL accept Merchant profile, typed metadata และ PSP connection อย่างน้อยหนึ่งรายการใน provisioning request
- 1.4 THE SYSTEM SHALL accept Merchant code เฉพาะ `vprivilege`, `vcommerce` และ `vsouvenir`
- 1.5 IF PSP connection วาง secret-owned field ไว้นอก `secrets` THEN THE SYSTEM SHALL reject request
- 1.6 WHEN provisioning succeeds THE SYSTEM SHALL return Merchant id, PSP connection ids และ masked secret hints เท่านั้น

## REQ-2: Atomic provisioning

**User Story:** As an operator, I want Merchant และค่าที่จำเป็น commit พร้อมกัน, so that retry หรือ failure ไม่ทิ้งข้อมูลครึ่งชุด

**Acceptance Criteria (EARS):**

- 2.1 WHEN provisioning succeeds THE SYSTEM SHALL create Active Merchant, PSP connections, encrypted VaultSecrets, ProvisioningAudit และ idempotency ledger เป็น operation เดียว
- 2.2 IF provisioning fails before commit THEN THE SYSTEM SHALL rollback entity set ทั้งหมดของ operation
- 2.3 WHEN request เดิมถูก replay ด้วย operation key และ payload เดิม THE SYSTEM SHALL return stored result โดยไม่สร้าง Merchant ซ้ำ
- 2.4 IF Merchant code ซ้ำ THEN THE SYSTEM SHALL return conflict

## REQ-3: Vault custody and reveal audit

**User Story:** As a security operator, I want PSP secrets encrypted และทุก reveal ตรวจสอบย้อนหลังได้, so that credential custody ไม่พึ่ง application logs

**Acceptance Criteria (EARS):**

- 3.1 THE SYSTEM SHALL persist only envelope-encrypted secret material และ non-sensitive hint ใน `merch.VaultSecrets`
- 3.2 THE SYSTEM SHALL NOT return or log plaintext secret
- 3.3 WHEN provisioning succeeds THE SYSTEM SHALL NOT append `merch.VaultRevealAudits`
- 3.4 WHEN server reveals a secret for PSP use THE SYSTEM SHALL append one reveal audit before returning plaintext to caller
- 3.5 IF reveal-audit append fails THEN THE SYSTEM SHALL fail the reveal without returning plaintext
- 3.6 THE SYSTEM SHALL maintain each Merchant reveal audit as an append-only hash chain containing Merchant id, secret name, reveal time และ chain metadata

## REQ-4: Registration and approval ordering

**User Story:** As an admin approver, I want bind pending user เฉพาะกับ Merchant ที่มีอยู่และ Active, so that active Merchant user ไม่อ้าง Merchant ที่ provision ไม่ครบ

**Acceptance Criteria (EARS):**

- 4.1 WHEN a Merchant user registers THE SYSTEM SHALL keep the user PendingApproval with no Merchant binding
- 4.2 WHEN admin approves a Merchant user THE SYSTEM SHALL resolve the submitted Merchant code through the admin-accessible Merchant boundary
- 4.3 IF Merchant code is unknown or outside the admin accessible set THEN THE SYSTEM SHALL return 404 without dispatching approval
- 4.4 IF resolved Merchant is not Active THEN THE SYSTEM SHALL return 409 without dispatching approval
- 4.5 WHEN resolved Merchant is Active THE SYSTEM SHALL dispatch approval with the resolved Merchant id
- 4.6 THE SYSTEM SHALL NOT add Draft Merchant, deferred PSP setup, Merchant CRUD expansion, Vault admin API หรือ plaintext reveal endpoint

## Assumptions

- Registration submission อาจเกิดก่อน Merchant provisioning; prerequisite บังคับตอน approval
- Vault relationships เป็น logical Merchant scope ผ่าน `MerchantId`; ไม่มี physical FK หรือ cascade delete
- Existing API wire shapes และ persisted schema คงเดิม
