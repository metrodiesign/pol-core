> Status: unknown
# Design: production-hardening — PR1 (DB foundation + RLS security floor)

Status: REVISED after Codex round 2 + live-SQL spike. Scope = PR1.
Satisfies REQ (foundation-scaffold): G1, G2, F2, REQ-3.6, REQ-4.4, REQ-5.1, REQ-5.2, REQ-5.5.

## Empirical ground truth (spike บน pol-db / SQL Server 2025, 2026-06-21)
ยืนยันด้วยการรันจริง ไม่ใช่สมมติ:
1. **RLS apply กับทุก principal รวม sysadmin/dbo** — sa ไม่มี context + ไม่อยู่ bypass role -> เห็น **0 row**.
   ไม่มี auto-bypass. predicate = authority เดียว. (ดี: แม้ sa ก็รั่วข้าม tenant ไม่ได้.)
2. **Ownership chaining ไม่ bypass RLS** (Codex ถูก). **`EXECUTE AS OWNER` (dbo) ก็ไม่ bypass** เพราะ dbo
   โดน RLS เช่นกัน.
3. **ทาง bypass เดียว = membership ใน bypass role.** `EXECUTE AS '<user ที่เป็น member ของ bypass role>'`
   proc -> bypass ได้เฉพาะ query ใน proc: app_user (ไม่ bypass, context=tenantA) เรียก proc คืน TenantId
   ของ connection tenantB ได้ ขณะ direct table access ยัง 0.
4. **Block predicate ทำงาน**: app context=tenantA insert row tenantB -> ถูก block.
5. **Syntax**: `ADD BLOCK PREDICATE ... AFTER INSERT, AFTER UPDATE` ใน clause เดียว = error. ต้องแยก
   `ADD BLOCK PREDICATE ... AFTER INSERT,` + `ADD BLOCK PREDICATE ... AFTER UPDATE`.

## การตัดสินใจหลัก

### D1. DB topology = หนึ่ง database `VCentralPay`, schema `producer`/`admin` (canon PLAN #3)
ทิ้ง 2-database (`pol_core_producer/admin` = scaffold artifact): RLS + SESSION_CONTEXT per-database;
admin อ่าน producer cross-tenant ใน DB เดียว.

### D2. Principal model (least privilege; bypass ผูกกับ role membership — ground truth #3)
| principal | ใช้โดย | สิทธิ์ |
|-----------|--------|--------|
| `pol_migrator` (dev=`sa`) | migrations | DDL, owns objects. ไม่ใช้ runtime |
| `pol_app` | TenantConsole | CRUD tenant tables (RLS กรอง); **INSERT-only** Outbox; CRUD IdempotencyRecords (RLS, มี TenantId); EXECUTE `producer.usp_resolve_webhook_tenant`. **ไม่อยู่ bypass role** |
| `pol_admin` | **(dormant)** — AdminConsole host ถูกถอด; เหลือไว้ใน bootstrap + ใช้โดย `RlsIsolationTests` (ต่อ DB ตรงพิสูจน์ bypass) | member `pol_rls_bypass`; SELECT producer cross-tenant |
| `pol_worker` | OutboxDispatcher | SELECT/UPDATE Outbox; CRUD เฉพาะ tenant table ที่ consumer แตะ (`Orders`) **แบบ RLS-scoped** (set context ต่อ message); **ไม่อยู่ bypass role** |
| `pol_webhook_resolver` (no login) | EXECUTE AS context ของ resolve proc เท่านั้น | member `pol_rls_bypass`; SELECT `PspConnections` |

`pol_rls_bypass` member = `pol_admin`, `pol_webhook_resolver`. pol_app/pol_worker ไม่ใช่.
pol_app **ไม่มี SELECT บน OutboxMessages** -> อ่าน payload (มี amount) ข้าม tenant ไม่ได้.

### D2a. Login-per-host + startup guard (ปิด Codex C3 escalation)
> ปรับ: AdminConsole host ถูกถอด (consolidation เป็น API เดียว). public API = TenantConsole เท่านั้น ต่อด้วย
> pol_app (ไม่ bypass) -> ไม่มี public host ไหนถือ bypass connection อีก (security floor ดีขึ้น).

ทุก connection ใน TenantConsole (API เดียว) = pol_app; Worker = pol_worker. Startup guard:
query `SELECT SUSER_SNAME(), IS_ROLEMEMBER('pol_rls_bypass')` ตอน boot -> assert pol_app/bypass=0
(Worker pol_worker/bypass=0); ไม่ตรง fail fast. Arch test ban raw `SqlConnection` นอก Persistence —
ครอบ **ทุก runtime principal รวม pol_worker** (ปิด Codex#4, r3#3); residual: cred รั่ว = inherent
SESSION_CONTEXT model (mitigate ด้วย cred ใน host เท่านั้น).

> สถานะ PR1: arch test (ban SqlConnection) = ลงแล้ว. **runtime startup principal guard = DEFERRED** —
> integration test `Host_principals_have_the_expected_bypass_membership` พิสูจน์ identity ของแต่ละ login
> แล้ว; การ query DB ตอน boot จะผูก host เข้ากับ live DB (ชน Hosts.Tests ที่ validate container แบบไม่มี DB).
> ทำเป็น follow-up เป็น IHostedService ที่ gate ด้วย config flag.

### D3. RLS predicate + SECURITY POLICY (syntax ตาม ground truth #5)
```sql
CREATE FUNCTION producer.fn_tenant_predicate(@TenantId uniqueidentifier)
RETURNS TABLE WITH SCHEMABINDING AS
RETURN SELECT 1 AS allowed
WHERE @TenantId = CAST(SESSION_CONTEXT(N'TenantId') AS uniqueidentifier)
   OR IS_ROLEMEMBER(N'pol_rls_bypass') = 1;
```
POLICY: FILTER + BLOCK (AFTER INSERT, AFTER UPDATE แยก clause) ทุก producer table ที่มี TenantId:
`PaymentSessions, PspConnections, Products, CheckoutSessions, Carts, Orders, VaultSecrets, IdempotencyRecords`.
`CartItems` (ไม่มี TenantId) ใช้ predicate แยก join `producer.Carts` (FK+index `CartItems.CartId` ต้องมีก่อน — ปิด Codex#7).

### D4. Webhook system path (ปิด Codex C1; mechanism พิสูจน์โดย spike #3)
```sql
CREATE PROCEDURE producer.usp_resolve_webhook_tenant @PspConnectionId uniqueidentifier
WITH EXECUTE AS 'pol_webhook_resolver' AS
BEGIN SET NOCOUNT ON;
  SELECT TOP 1 TenantId FROM producer.PspConnections WHERE Id = @PspConnectionId;
END
```
- EXECUTE AS user เป็น member ของ bypass role -> proc bypass RLS เฉพาะ lookup นี้, คืนแค่ TenantId.
  pol_app = EXECUTE only (direct PspConnections ยัง RLS-blocked).
- Webhook handler: เรียก proc -> ได้ TenantId -> set ผ่าน **`IWebhookTenantScope`** (internal, webhook-only):
  assert ยังไม่มี tenant binding (กัน confused-deputy — Codex r2#5), set, เปิด DbContext scope (interceptor
  stamp SESSION_CONTEXT) -> อ่าน/verify/MarkPaid/Outbox **RLS-scoped ปกติ**, clear ใน `finally`.

### D5. Outbox / Idempotency
- **OutboxMessages**: เพิ่ม column `TenantId` (เขียนตอน enqueue). Security policy = **BLOCK PREDICATE
  AFTER INSERT เท่านั้น** (ไม่มี FILTER, ไม่มี AFTER UPDATE) — spike พิสูจน์: pol_app insert ได้เฉพาะ
  `TenantId = SESSION_CONTEXT` (forge TenantId=B -> BLOCKED, ปิด Codex r3 Critical 1); pol_worker
  (ไม่มี filter) SELECT/UPDATE drain ข้าม tenant ได้. grant: pol_app INSERT-only; pol_worker SELECT/UPDATE.
- **Dispatcher re-scope lifecycle** (ปิด Codex r3#2): lease batch ใน scope ของตัวเอง (no tenant); ต่อ message
  เปิด **DI scope + DbContext + physical connection ใหม่** หลัง set `SESSION_CONTEXT` = `message.TenantId`
  (interceptor stamp ตอน open), publish -> `OrderPaidConsumer` เขียน `Orders` RLS-scoped, dispose ก่อน
  message ถัดไป. TenantId เชื่อถือได้เพราะ block-on-insert การันตี = inserter's context. pol_worker grant
  CRUD `Orders` (RLS กรอง). test `Max Pool Size=1` batch A+B (test 9/10).
- **IdempotencyRecords**: เพิ่ม `TenantId` + ผูก RLS (ใช้แค่ใน HandlePspWebhook หลัง resolve tenant แล้ว —
  ยืนยันจาก call-site; ปิด Codex r2#3, ตัด direct cross-tenant read). key เพิ่ม `PspConnectionId` dimension
  (`psp:{connId}:event:{id}`) กัน event-id ชนข้าม tenant (Codex r1#8). pol_app CRUD (RLS กรอง).

### D6. Admin interceptor — ไม่แก้ (bypass จาก role ไม่ใช่ session flag)

### D7. Migration / bootstrap ordering (ปิด Codex r2#4, r1#6/#9) — hard gate
1. `docker/bootstrap/01-principals.sql` (รันด้วย sa, ก่อน table): logins + users + role `pol_rls_bypass`
   + role membership. **ไม่มี table-level grant** (table ยังไม่เกิด). Password จาก env, ไม่ commit, idempotent.
2. EF migration G1 (รันด้วย pol_migrator/sa): schema + tables + FK + index + `OutboxMessages.TenantId` +
   `IdempotencyRecords.TenantId`.
3. EF migration G2 (raw SQL `migrationBuilder.Sql`): predicate functions + resolve proc +
   `CREATE SECURITY POLICY` (producer tables + Outbox block-on-insert) + **object-level grant/deny ตาราง D2**.
   `Down()` ลำดับย้อน: DROP POLICY ก่อน -> DROP proc/functions -> REVOKE grants (principals คงอยู่ใน bootstrap, ปิด Codex r3#5).
4. integration test assert `sys.security_policies.is_enabled = 1`.
pol_app/admin/worker ไม่มี DDL -> deploy ใช้ pol_migrator identity แยก.

### D8. Connection string + secret
`Trusted_Connection=True` -> SQL auth:
`Server=localhost,11433;Database=VCentralPay;User Id=pol_app;Password=${POL_APP_PASSWORD};Encrypt=True;TrustServerCertificate=True`.
Password ไม่ commit (`${ENV}`/user-secrets/.env). `.env.example` placeholder. residual trust (SESSION_CONTEXT):
mitigate ด้วย arch test ban raw SqlConnection + cred ใน host เท่านั้น; **future** (captive 3 tenant):
per-tenant login + `ORIGINAL_LOGIN()` predicate = DB-enforce เต็ม (นอก scope PR1, canon เลือก SESSION_CONTEXT).
VaultSecrets: pol_app SELECT ได้แต่ ciphertext (KEK แยก) + RLS กรอง cross-tenant; proc-isolation = future (Codex r2#6).

## Integration tests (F2, `tests/Integration.Tests`, `[Trait("Category","Integration")]`)
กับ live pol-db (bootstrap + migrations ก่อน):
1. RLS read isolation: ctx A insert; ctx B select ไม่เห็น.
2. RLS write block: ctx A insert row B -> BLOCK.
3. RLS admin bypass: pol_admin เห็นทุก tenant.
4. **RLS authority**: sa ไม่มี context + ไม่ bypass -> เห็น **0** (predicate = authority; ground truth #1).
5. CartItems predicate isolation.
6. pol_app SELECT OutboxMessages -> permission denied; pol_app insert outbox row TenantId≠context -> BLOCKED.
7. principal identity: pol_app/bypass=0 (API), pol_worker/bypass=0; pol_admin/bypass=1 (dormant principal —
   ใช้พิสูจน์ RLS bypass #3 ผ่าน connection ตรง, ไม่มี host แล้ว).
8. **webhook resolve proc**: pol_app (no context) เรียก proc -> ได้ TenantId; แล้ว set context อ่าน PspConnection สำเร็จ.
   grant-surface (Codex r3#4): `pol_webhook_resolver` login ไม่ได้ (WITHOUT LOGIN), proc คืนแค่ TenantId column,
   pol_app direct SELECT PspConnections ข้าม tenant = 0.
9. **dispatcher re-scope**: outbox message tenant A -> dispatcher set context -> OrderPaidConsumer เขียน Orders A สำเร็จ; ไม่รั่ว B.
10. SESSION_CONTEXT pooled reuse: `Max Pool Size=1` สลับ A/B/no-tenant -> ไม่ bleed.
11. Outbox lease (READPAST) ไม่ซ้ำ; idempotency unique-violation race; rowversion concurrency conflict.

## CI
เพิ่ม SQL Server 2025 service container; start SQL -> `01-principals.sql` -> `dotnet ef database update`
(migrator) -> `dotnet test --filter "Category=Integration"`. Password จาก GH secret. job unit เดิมคงไว้.

## สรุปตอบ Codex round 2
C1 webhook premise (ownership chaining ผิด): รับเต็ม -> spike พิสูจน์ `EXECUTE AS '<bypass member>'` proc (D4).
C2 pol_worker/consumer: รับ -> D5 (Outbox.TenantId + dispatcher re-scope + pol_worker grant Orders RLS-scoped).
#3 idempotency leak: รับ -> D5 (TenantId+RLS). #4 ordering: รับ -> D7 split. #5 AsyncLocal: รับ -> D4 IWebhookTenantScope.
#6 VaultSecrets: push back (ciphertext+RLS, proc-isolation future) -> D8. #7 test premise: รับ -> test 4/8/9.
