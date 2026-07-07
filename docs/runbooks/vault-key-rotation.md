# Runbook: หมุน Vault master key (self-host)

Vault เก็บ secret แบบ envelope: ต่อ secret มี DEK สุ่มเข้ารหัส plaintext, แล้ว KEK ต่อ tenant
(HKDF-SHA256 จาก master key) ห่อ DEK อีกชั้น. master key อยู่ใน "keyring" แบบมีเวอร์ชัน: แต่ละ
`VaultSecretBlob.KeyId` บอกว่า DEK ของมันถูกห่อด้วย key id ไหน, secret ใหม่ห่อด้วย key ที่เป็น `ActiveKeyId`.
การหมุน master key = เพิ่ม key ใหม่ -> ตั้งเป็น active -> re-wrap DEK ของ blob เก่าทั้งหมดให้ไปอยู่ key ใหม่
-> ปลด key เก่าออกเมื่อไม่มี blob อ้างอิงแล้ว.

re-wrap ถอดเฉพาะ DEK (ไม่เคยถอด plaintext ของ secret) -> plaintext ไม่เคย materialize ลง disk/log,
ciphertext ของ secret ไม่ถูกแตะ.

## ข้อควรรู้ก่อนเริ่ม (correctness gates)

- ห้ามลบ key id ออกจาก keyring ก่อน re-wrap blob ที่อ้าง id นั้นครบ. RevealAsync fail CLOSED บน key id
  ที่ไม่รู้จัก (ไม่มี fallback ไป active key) -> ปลด key เร็วไป = reveal ของ blob เก่าพังทั้งหมด (เก็บเงินไม่ได้).
- retire-gate เป็น GLOBAL ข้ามทุก tenant. ต้องยืนยัน `COUNT(*) WHERE KeyId = <old id> == 0` ข้าม **ทุก tenant**
  (รันด้วย principal ที่ bypass RLS เช่น `pol_admin`) ก่อนถอด key เก่า. ยืนยัน tenant เดียวแล้วถอด = strand
  blob ของ tenant อื่น.
- keyring build ครั้งเดียวตอน boot (Singleton). เปลี่ยน key/ไฟล์ secret = **ต้อง restart process** (ไม่มี hot reload).
- ถ้าไฟล์ secret หาย/ว่าง/ผิดตอน boot: Api host resolve keyring ตอน start
  -> factory throw -> host crash-loop (fail-fast). Worker build keyring แบบ lazy -> ความผิดโผล่ที่
  `/health/ready` = not_ready (และ reveal throw). gate การ deploy ที่ `/health/ready` = healthy เสมอ
  ไม่ใช่แค่ "process ขึ้น". mount secret ให้พร้อมก่อน start.
- master key เป็น 32 ไบต์ (AES-256) base64. ห้าม commit ลง repo. ไฟล์ key ถูก `.gitignore` (`*.key`, `secrets/`).

## ขั้นตอน

### 1. สร้าง key ใหม่ + mount เป็น secret file

```bash
head -c 32 /dev/urandom | base64 > vault_master_v2.key   # 32-byte AES key, base64
# mount ไฟล์นี้เข้า container เช่น /run/secrets/vault_master_v2 (Docker/K8s secret)
```

### 2. เพิ่ม key เข้า keyring + ตั้งเป็น active (config/env)

keyring bind จาก section `Vault`. `Keys` เป็น map ที่ key = key id (bind ด้วยชื่อ env ตาม id):

```
Vault__ActiveKeyId=v2
Vault__Keys__local-envelope-v1__KeyFile=/run/secrets/vault_master_v1
Vault__Keys__v2__KeyFile=/run/secrets/vault_master_v2
```

หมายเหตุ: `local-envelope-v1` คือ id ที่ blob ทุกตัวก่อนหมุนถือไว้ — ต้องคงอยู่ใน keyring จนกว่าจะ re-wrap ครบ.
(host ที่ยังใช้ legacy `Vault__MasterKeyBase64` จะถูก shim เป็น entry id `local-envelope-v1` ให้อัตโนมัติ —
เมื่อย้ายมา keyring ให้ย้ายค่าเดิมไปเป็น `Vault__Keys__local-envelope-v1__KeyFile/KeyBase64`.)

### 3. Deploy + restart

mount secret v2 ให้พร้อม -> redeploy -> restart. ตรวจ readiness:

```bash
curl -fsS http://<host>/health/ready    # ต้องได้ {"status":"healthy"} (keyring มี active key 32 ไบต์)
```

หลังขั้นนี้: secret ใหม่ห่อด้วย `v2`, blob เก่ายังถือ `local-envelope-v1` และ reveal ได้ปกติ (keyring มีทั้งคู่).

### 4. Re-wrap blob เก่าทุก tenant

รัน `IVaultMaintenance.RewrapTenantToActiveKeyAsync(tenantId)` ต่อ tenant (จาก admin/maintenance entrypoint
ที่ตั้ง ambient tenant ถูกต้อง ภายใต้ RLS scope ของ tenant นั้น). idempotent — ข้าม blob ที่ active อยู่แล้ว,
คืนจำนวน blob ที่ re-wrap. วน tenant ให้ครบทุกราย.

ตรวจหลัง re-wrap (รันด้วย `pol_admin` / bypass):

```sql
SELECT KeyId, COUNT(*) FROM VCentralPay.VaultSecrets GROUP BY KeyId;
-- คาดหวัง: เหลือเฉพาะ v2; ไม่มีแถว local-envelope-v1
```

### 5. ปลด key เก่า (เฉพาะเมื่อ retire-gate ผ่าน)

ยืนยัน GLOBAL ก่อน:

```sql
SELECT COUNT(*) FROM VCentralPay.VaultSecrets WHERE KeyId = 'local-envelope-v1';  -- ต้อง = 0 ข้ามทุก tenant
```

ได้ 0 แล้วจึงถอด entry `local-envelope-v1` ออกจาก config -> restart. unmount/destroy ไฟล์ secret เก่า.
ถ้ายังไม่ใช่ 0: ห้ามถอด — ย้อนไปข้อ 4 ทำ re-wrap tenant ที่ค้างให้ครบ.

## Rollback

ก่อนผ่าน retire-gate (ข้อ 5) การหมุนย้อนได้ปลอดภัย: ตั้ง `Vault__ActiveKeyId` กลับเป็น `local-envelope-v1`
แล้ว restart. key v2 ยังอยู่ใน keyring -> blob ที่ re-wrap ไป v2 แล้วยัง reveal ได้, secret ใหม่กลับไปห่อ v1.
**ห้ามถอด key id ที่ยังมี blob อ้างอิง** — นั่นคือจุดที่ rollback ไม่ได้ (ciphertext ห่อด้วย key นั้น).
