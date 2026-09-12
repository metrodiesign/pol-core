# Database Connections และ Isolation Floor

เอกสารนี้อธิบาย connection, runtime contexts และ migration safety ของ source ปัจจุบัน. SQL RLS เป็น historical design ที่ถูกแทนที่ด้วย app-layer floor แล้ว.

## Connections

| Connection | ใช้ทำอะไร | Principal/ข้อจำกัด |
|---|---|---|
| `ConnectionStrings:App` | API และ in-process background runtime บน `VCentralPay` | `pol_app`; ไม่มี DDL |
| `ConnectionStrings:Migrator` / `POL_DESIGN_SQL` | explicit development/operator migration | `sa` หรือ DDL migrator; ไม่ใช่ request runtime |
| `SpDocument:MotorConnectionString` | upstream Motor document search | sim/source principal เช่น `hippo_app` ตาม environment |
| `SpDocument:NonMotorConnectionString` | upstream Non-Motor document search | sim/source principal เช่น `mammoth_app` ตาม environment |

Runtime secret มาจาก environment/user-secrets/secret manager; ห้าม hardcode หรือ log. SQL Server baseline ที่ local evidence ใช้คือ `17.0.4045.5` และ compatibility level `170`.

## Runtime contexts

| Context | ขอบเขต |
|---|---|
| `ControlPlaneDbContext` | `acct`, `access`, `admin`, `iam`, `oauth`, `cfg`, merchant identity/profile/branch/sale/originator/vault และ `txn` provider/routing/capability/approval-configuration rows |
| `CommerceDbContext` | `shop`, `checkout` และ `txn` เฉพาะ payment attempts, inbound webhook, idempotency, outbox, Transactions/events และ notification runtime |
| `PolDbContext` | full relational model สำหรับ migration/snapshot เท่านั้น; API ไม่ register |

ทุก runtime request ใช้ `pol_app` connection เดียวกัน แต่ context แยก ownership และ write floor. Old `MerchantUserDbContext`/`MerchantRuntimeDbContext` names เหลือใน compatibility tests/comments เท่านั้นและไม่ใช่ registered runtime contexts.

## Isolation floor

1. Query filter ใช้ `CurrentMerchant`; unbound actor ได้ `Guid.Empty` และเห็นศูนย์แถว merchant-scoped.
2. Merchant-user order/payment reads ตรวจ `InitiatingMerchantUserId` ผ่าน Order relationship เพิ่มจาก merchant filter.
3. Account/Access policy ตรวจ Account active, merchant context, `DataScope`, Agent `SaleId`, Employee BranchAccess หรือ System scope.
4. `GuardedRuntimeDbContext` และ `IWriteAuthorizer` ตรวจ tenant key immutable-after-insert, operation allowlist, concurrency และ unbound writes ก่อน commit.
5. Authorization lease ตรวจ `AuthorizationVersion` ใน transaction เดียวกับ sensitive write เพื่อกัน revoke-then-commit.

Cross-merchant admin reads ใช้ named port และ explicit accessible set. การเรียก `IgnoreQueryFilters`, raw SQL, `ExecuteUpdate` หรือ `ExecuteDelete` นอก allowlist ถูก architecture tests ปฏิเสธ. ไม่มี `SECURITY POLICY`, `SESSION_CONTEXT`, `EXECUTE AS` bypass procedure หรือ bypass principal เป็น runtime isolation mechanism.

## Schema ownership

| Schema | ตัวอย่างข้อมูล |
|---|---|
| `acct` | Accounts, Employees, Agents, SystemClients, BFF/registration sessions |
| `access` | Merchant/Platform access, branch/role/method grants |
| `admin` | Admin identity, governance, audit, provisioning, control delivery |
| `iam` | Permission catalog, roles, API clients, secret tickets |
| `merch` | Merchant master, sales/branches/originators, merchant users, vault |
| `shop` | Carts, Orders, OrderItems, reveal audit |
| `checkout` | PaymentLinks, replay pointers |
| `txn` | Control Plane: PSP connections, routing, payment capability mappings, approval execution. Commerce: PaymentSessions, Transactions/events, inbound webhooks, idempotency, outbox, Notifications |
| `cfg` | Payment capability catalog |
| `oauth` | OpenIddict state และ assertion replay |
| `dbo` | Data Protection keys และ `__EFMigrationsHistory` |

Physical mapping ให้ยึด `PolDbContextModelSnapshot.cs` และ EF configurations ที่มี `ToTable(name, schema)` เสมอ.

## Migration safety และ readiness

Migration chain ปัจจุบันมี 47 migrations และจบที่ `20260911163519_ReviewFixPaymentLinkNotificationIntent`. Task 9 เพิ่ม deterministic mapping/conflict report, target-owner backfill, transaction-scoped writer lease, pause/watermark recovery และ forward-safe rollback machinery.

Local migration parity, pending-model, schema drift, fresh scratch database และ static checks ผ่านตาม handoff. หลักฐานนั้นเป็น local machinery; sanitized backup, master identity mapping, live provider evidence และ production authorization ยังขาด จึงยังไม่ cutover-ready.

## Source of truth

- `src/Infrastructure/BuildingBlocks.Infrastructure/Persistence/PolDbContext.cs`
- `src/Infrastructure/Persistence/Persistence.ControlPlane/ControlPlaneDbContext.cs`
- `src/Infrastructure/Persistence/Persistence.MerchantRuntime/MerchantRuntimeDbContext.cs`
- `src/Infrastructure/BuildingBlocks.Infrastructure/Persistence/GuardedRuntimeDbContext.cs`
- `src/Infrastructure/BuildingBlocks.Infrastructure/Persistence/Migrations/`
- [`entity-fields.md`](entity-fields.md)
