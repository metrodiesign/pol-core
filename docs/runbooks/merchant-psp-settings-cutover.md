# Runbook — Cutover legacy payment remediation (merchant-psp-settings)

เอกสารนี้เป็น operator runbook สำหรับ cutover ของ feature `merchant-psp-settings`
(server-side routing authority + per-merchant payment environment) หลัง PR ถูก merge
เข้า `develop` ครอบคลุมสิ่งที่ระบบ **ยังไม่พร้อม** ณ วัน merge, ค่า production ที่ต้องจดไว้
เพราะเป็น one-way door, ผลกระทบระหว่างที่ยังไม่ cutover และขั้นตอน cutover ที่ ops ต้องทำ
รอบถัดไปพร้อมวิธี verify แต่ละขั้น

## สถานะ ณ วัน merge PR: ยังไม่พร้อม cutover

- `ILegacyPaymentRemediation`
  (`src/Pol.Application/Modules/Payments.Application/Capabilities/LegacyPaymentRemediation.cs`)
  implement และมี test ครบแล้ว 2 operation:
  - `BackfillMerchantEnvironmentsAsync(actorId, globalDefault, ct)` — ตั้ง
    `Merchant.PaymentEnvironment` ให้ merchant ที่ **ไม่เคยสลับ environment** ตามค่า global เดิม
  - `RemediateSessionsAsync(actorId, legacyEnvironment, ct)` — ยกระดับ legacy Session
    (`RoutingSnapshotVersion = 0`) เป็น snapshot v1 หรือคง v0 ถ้าพิสูจน์ historical secret ไม่ได้
  - implementation จริงอยู่ที่
    `src/Pol.Infrastructure/Persistence/Persistence.MerchantRuntime/Payments/LegacyPaymentRemediationService.cs`
- **ยังไม่มี entry point**: prod boot รัน migration ผ่าน `docker/migrate-entrypoint.sh` ซึ่งรัน
  SQL script ล้วน (`schema.sql` forward-only) ไม่สามารถเรียก scoped .NET service ได้ ทั้งสอง
  operation ข้างต้นจึงยังไม่มีจุดเรียกใน production path เป็นงาน ops รอบถัดไป
- **สรุป: ระบบยังไม่พร้อม cutover** จนกว่าจะมี entry point ที่เรียก 2 operation นี้ (ตัดสินใจใน
  rework รอบนี้ว่าไม่เพิ่ม entry point ในโค้ด — ให้ทำเป็นงาน ops แยก)

## ค่า production ที่ต้องจดไว้ (one-way door)

ค่า production `Psp:UseSandbox` ณ วันที่ merge PR นี้ = **`true` (sandbox)**
(ผู้ใช้ยืนยันเอง 2026-09-07)

- ต้องจดไว้เพราะ property `PspOptions.UseSandbox` ถูกลบออกจากโค้ดแล้ว (AC-9.5) หลัง merge จะ
  ไม่มีอะไรใน repo บอกค่าเดิมได้อีก
- ค่านี้คือ `legacyEnvironment` / `globalDefault` ที่ต้องส่งเข้า `BackfillMerchantEnvironmentsAsync`
  และ `RemediateSessionsAsync` ตอน cutover:
  - `UseSandbox = true` -> `PspEnvironment.Sandbox`
  - `UseSandbox = false` -> `PspEnvironment.Live`
- เนื่องจากค่าจริง = sandbox, backfill รอบแรกจะเป็น no-op สำหรับ merchant เดิม (ดูหัวข้อถัดไป)
  แต่ **ต้อง verify ค่านี้อีกครั้งกับ config production จริงก่อนสั่ง switch ครั้งแรก** ไม่ยึด
  เอกสารนี้เพียงอย่างเดียว

## ผลกระทบระหว่างที่ยังไม่ cutover

- merchant เดิมทุกรายค้างที่ `PaymentEnvironment = Sandbox` (default `1` จาก migration
  `20260906123608_MerchantPaymentEnvironment.cs:73`) ซึ่ง **ตรงกับค่า prod จริง** (`UseSandbox = true`)
  จึงไม่ใช่ label ผิดในกรณีนี้ แต่ต้อง verify ก่อนสั่ง switch ครั้งแรกเสมอ
- legacy Session ที่ `RoutingSnapshotVersion = 0` **และมี** `PspExternalChargeId` (มี external charge
  ที่ยังพิสูจน์ pinned secret ไม่ได้) ยัง block credential rotation และ environment activation ของ
  merchant นั้นด้วย 409 `legacy_snapshot_blocked` ตาม AC-7.5 / AC-9.3 จนกว่าจะ remediate
- legacy Session ที่ `RoutingSnapshotVersion = 0` แต่ **ไม่มี** external charge **ไม่** block
  (แก้ใน rework รอบนี้ MF-1) — remediation จะ upgrade เป็น v1 ให้เอง ตาม AC-9.2

## ขั้นตอน cutover (ops ทำรอบถัดไป)

ทำตามลำดับ หยุดทันทีถ้าเงื่อนไข verify ข้อใดไม่ผ่าน แล้วรายงานก่อนไปขั้นถัดไป

### ก่อนเริ่ม
1. ยืนยันค่า `Psp:UseSandbox` เดิมของ production จาก config/secret store จริง ไม่ยึดเอกสารนี้
   อย่างเดียว
   - verify: ค่าที่ได้ต้องตรงกับที่จดไว้ (`true`) ถ้าไม่ตรง หยุด สอบสวนก่อน
2. backup ตาราง `merch.Merchants`, `txn.PaymentSessions`, `<vault>.VaultSecretVersions`
   (snapshot/export) เก็บไว้ทั้งชุด
   - verify: backup อ่านกลับได้และมี row count ตรงกับ production ปัจจุบัน

### ขั้นที่ 1 — Backfill merchant environment (design step 3)
3. เรียก `BackfillMerchantEnvironmentsAsync(operatorActorId, globalDefault, ct)` โดย
   `globalDefault` = ค่าที่ verify ในข้อ 1 (`true` -> `Sandbox`)
   - operation แก้เฉพาะ merchant ที่ `PaymentEnvironmentUpdatedAt = CreatedAt` (ไม่เคยสลับ),
     ค่าเปลี่ยนจริง และไม่มี pending approval — re-run เป็น no-op
   - verify: จำนวนแถวที่ update = จำนวน merchant never-switched ที่ค่าต่างจาก globalDefault
     กรณี `globalDefault = Sandbox` และ merchant เดิม default Sandbox อยู่แล้ว จำนวน = 0 (no-op ปกติ)
   - หยุดถ้า: จำนวน update เกินคาด หรือ operation throw

### ขั้นที่ 2 — Remediate legacy sessions (design step 5-10)
4. เรียก `RemediateSessionsAsync(operatorActorId, legacyEnvironment, ct)` โดย `legacyEnvironment`
   = ค่าเดียวกับข้อ 3
   - Session v0 ไม่มี charge -> upgrade เป็น v1 ด้วย route ปัจจุบัน (AC-9.2)
   - Session v0 มี charge -> fetch-to-confirm read-only ทีละ secret version; ยืนยันได้หนึ่งค่า ->
     v1, พิสูจน์ไม่ได้/กำกวม -> คง v0 และเข้า remediation report (AC-9.3)
   - verify: อ่าน `LegacySessionRemediationReport` ที่คืนมา ตรวจจำนวน upgraded vs blocked
   - หยุดถ้า: มี blocked จำนวนมากผิดปกติ หรือ operation throw ระหว่างกลาง (transaction ต่อ merchant
     เป็น atomic แต่ให้ตรวจ report ก่อนไปต่อ)

### ขั้นที่ 3 — ตรวจ residual block
5. ตรวจว่า merchant ที่ยังมี Session v0 + external charge (ยัง block) เหลือกี่ราย
   - verify (read-only):
     ```sql
     SELECT MerchantId, COUNT(*)
     FROM txn.PaymentSessions
     WHERE RoutingSnapshotVersion = 0 AND PspExternalChargeId IS NOT NULL
     GROUP BY MerchantId;
     ```
   - merchant ในผลลัพธ์นี้ยังถูก 409 `legacy_snapshot_blocked` ตอน credential/environment change
     ต้องตามแก้เป็นรายกรณีก่อนเปิดให้ merchant นั้น switch
   - หยุดถ้า: ยังมี merchant ค้างและ business ต้องการให้ merchant นั้น switch ได้

### ขั้นที่ 4 — Contract (design step 11-13, ยังไม่อยู่ใน scope PR นี้)
6. design step 11-13 (deploy `pol-merchant` ไม่ส่ง PSP, ตัด legacy `Psp` field + compat branch,
   ตัด `DefaultPspSelection`) เป็นรอบ deploy ถัดไป รอ deprecation telemetry ยืนยันว่าไม่มี legacy
   caller ก่อน — **ห้ามทำในรอบ cutover นี้**

## Rollback

- rollback = **deploy compatibility build ตัวก่อน** ที่อ่าน snapshot version 0/1 และยังรับ legacy request ได้
- **ห้ามรัน EF `Down`**: prod apply schema ผ่าน `schema.sql` แบบ forward-only เท่านั้น และ `Down` ของ
  migration หลายตัวใน feature นี้ (`MerchantPaymentEnvironment`, `PaymentSessionRoutingSnapshot`,
  `InboundWebhookPendingMatch`, `MerchantPspSecurityFoundation`) มี `DropColumn`/`DropTable` การรัน `Down`
  จะลบ column/table และทำข้อมูลหาย
- **ห้ามลบ** table, snapshot columns หรือ vault versions ระหว่าง rollback window ด้วยมือเช่นกัน
  (เฉพาะ `LegacyVaultExpiryRemediation` ที่ `Down` เป็น no-op — ตัวอื่นไม่ใช่)

## อ้างอิง

- 12-step migration + rollback: `.ai/specs/merchant-psp-settings/design.md` บรรทัด 735-754
- AC-9.1 (migration ตาม 12 ขั้น), AC-9.2 (uncharged -> v1, charged -> คง v0),
  AC-9.3 (fetch-confirm ทีละ secret, พิสูจน์ไม่ได้ -> block): `.ai/specs/merchant-psp-settings/spec.md`
- guard `legacy_snapshot_blocked`:
  `src/Pol.Infrastructure/Persistence/Persistence.MerchantRuntime/Payments/AdminPaymentsControlStore.cs:669` (credential),
  `:834` (environment)
