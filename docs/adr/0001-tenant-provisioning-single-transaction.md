# ADR 0001: Tenant provisioning uses a single transaction (not a saga)

- Status: accepted
- Date: 2026-06-22
- Context spec: `.ai/specs/tenant/` (REQ-4.1)

## Context

`.ai/shared/ARCHITECTURE.md` และ `.ai/shared/SECURITY_RULES.md` กำหนดว่า provisioning ต้องเป็น
**saga** (`PendingProvisioning -> write DB -> write vault -> verify -> activate -> compensation/retry`)
ด้วยเหตุผลว่า "DB กับ vault คนละ store จึงไม่มี distributed transaction".

reference `docs/reference/payment-orchestration-modules.md` section 2.4 กลับระบุชัดว่าขั้นเขียน
`Tenant` + `PspConnection` + `VaultSecret` + activate "ต้องอยู่ใน **transaction เดียว** กัน partial
provision".

ทั้งสองขัดกัน. ข้อเท็จจริงของโค้ดจริง: vault ปัจจุบัน (`LocalEnvelopeVaultStore`) เขียน
`VaultSecretBlob` ลง `ProducerDbContext` เดียวกับตารางอื่น (`producer.VaultSecrets`) — **DB กับ vault
เป็น store เดียวกันตอนนี้** premise ของ saga rule จึงไม่เป็นจริงในสถานะปัจจุบัน.

## Decision

ทำ provisioning เป็น **single transaction** ตาม reference 2.4 (REQ-4.1): เขียน `Tenant` +
`PspConnection`(s) + `VaultSecret`(s) + `ProvisioningAudit` แล้ว commit ครั้งเดียวผ่าน
`IUnitOfWork.ExecuteInTransactionAsync` บน `pol_admin` connection. fail กลางทาง = rollback ทั้งหมด
(ไม่มี partial provision) โดยไม่ต้องมี compensation logic.

## Consequences

- ได้ atomicity จริงโดยไม่ต้องเขียน saga state machine / compensation — code น้อยลง ทดสอบง่ายขึ้น.
- ผูกกับสมมติฐานว่า **vault อยู่ใน DB เดียวกัน**. ถ้าใดวัน vault ย้ายไป external KMS/HSM (target ใน
  ARCHITECTURE.md:91) premise นี้พัง.

## Trigger to revert (กลับไปใช้ saga)

เมื่อ `IVaultSecretStore` ถูกเปลี่ยน impl ให้เขียน secret ลง store ภายนอก (KMS/HSM/Key Vault) ที่ไม่ใช่
`ProducerDbContext` — ต้องกลับไปทำ saga (`PendingProvisioning` state + write-DB / write-vault แยก +
verify + activate + compensation/idempotency key) ตาม ARCHITECTURE.md:94 ทันที. ADR นี้ใช้ไม่ได้อีก
ต่อไปในกรณีนั้น.
