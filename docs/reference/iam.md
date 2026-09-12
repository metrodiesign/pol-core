# Iam และ Account/Access Reference

เอกสารนี้รวม IAM catalog ที่ใช้กับ console และ canonical business identity ที่ใช้กับ Order authorization. สองแกนนี้แยกหน้าที่กัน: IAM บอก action permission ส่วน Account/Access บอก actor, merchant visibility และ owner scope.

## Canonical identity: Employee, Agent, System

`Accounts.Domain` มี account type 3 ค่า:

| Type | Identity/การใช้งาน | Access anchor |
|---|---|---|
| `Employee` | workforce human ของ platform | `access.PlatformAccess` และ Platform/Shared roles |
| `Agent` | ตัวแทนที่ผูก `MerchantId` และ `SaleId` เดียว | `access.MerchantAccess.DataScope=Self` หรือ scope ที่ policy อนุญาต |
| `System` | client credentials สำหรับ integration | `acct.SystemClients` + `access.SystemClientScopes` |

`LoginAccount` เก็บ stable tuple `(Provider, TenantId, ExternalUserId)`; email เป็น contact ที่เปลี่ยนได้. Human identity policy รับ Microsoft provider, issuer/tenant/audience, signature, lifetime, state และ nonce ที่ผ่านแล้ว. Employee ใช้ workforce eligibility; Agent registration ใช้ CIAM identity ที่ยังไม่มี approved Account; System ใช้ signed client assertion, key validity และ JTI replay store.

`AccessEvaluator` เป็น shared policy สำหรับทุก protected path:

- `Merchant` เห็น merchant scope ทั้งหมด
- `Self` ใช้ Agent `SaleId` ของตน
- `Branch` ต้องมี BranchAccess เดียวที่ตรงกับ order owner branch
- `AssignedBranches` ต้องมี branch อยู่ในชุดที่ assign
- access/role/branch/sale ทุกตัวต้องอยู่ merchant เดียวกัน; account inactive, merchant mismatch, stale authorization หรือ owner mismatch เป็น deny

Legacy `Admins.Domain.Users.User`/`MerchantUser` ยังคงเป็น console session adapters (`AdminSession`, `MerchantUser` BFF) และไม่ควรใช้แทน business `Account` ใน canonical `/orders` path.

## IAM catalog

`src/Domain/Modules/Iam.Domain/Permissions/Keys.cs` เป็น source ของ key/group vocabulary. Seed ปัจจุบันหลัง `SharedRoleScope` มี 7 groups, 25 keys, 4 roles และ 36 role grants:

| Scope | Groups | Keys | กลุ่ม |
|---|---:|---:|---|
| Platform | 5 | 17 | `txn`, `merchant`, `user`, `system`, `merchants.users` |
| Merchant | 1 | 5 | `roles` |
| Shared | 1 | 3 | `payment` |

Shared keys คือ `payment.view`, `payment.create`, `payment.redirect` และ role `merchant_staff` assign ได้ทั้ง Platform/merchant-side assignment. `txn.manage` เป็น legacy key ที่ถูก retire; endpoint commerce ใช้ `payment.*` ร่วมกัน.

## Roles และการ resolve

Seed roles คือ `platform_admin`, `platform_auditor`, `merchant_manager`, `merchant_staff`. `platform_admin` และ `merchant_manager` เป็น anchors ที่ลบ/ปิดไม่ได้. Seed role มี `MerchantId = NULL`; custom merchant role ต้องมี owner merchant และ visibility ต้องอยู่ merchant เดียวกัน.

Effective permissions คำนวณจาก account/session assignments และเฉพาะ role/group/permission ที่ `Active`. Catalog status หรือ role status เปลี่ยนแล้วมีผล request ถัดไป; permission ไม่ถูกเชื่อจาก client claim. Boot parity guard ตรวจ endpoint gate กับ key catalog และ scope ฝั่งที่ถูกต้อง.

## Persisted model และ routes

| Owner | Tables |
|---|---|
| `iam` | `PermissionGroups`, `Permissions`, `Roles`, `RolePermissions`, `ApiClients`, `OneTimeSecretTickets` |
| `admin` | `RoleAssignments` สำหรับ Platform/Admin console |
| `merch` | `RoleAssignments` สำหรับ Merchant-user console |
| `acct`/`access` | `Accounts`, `LoginAccounts`, `Agents`, `Employees`, `SystemClients`, `MerchantAccess`, `PlatformAccess`, branch/role/method grants |

Canonical admin identity/access routes อยู่ใต้ `/api/v1/accounts...`, `/api/v1/accounts/{accountId}/merchant-access...`, `/api/v1/accounts/{accountId}/platform-access...` และ registration routes อยู่ `/api/v1/agent-registration...`/`/api/v1/agent-registrations...`. Legacy catalog routes `/api/v1/admins/permissions`, `/roles` และ merchant-user `/api/v1/merchants/users/permissions`, `/roles` ยังคงสำหรับ console adapters.

Mutation ของ account/access/role ใช้ CSRF, `If-Match` และ `Idempotency-Key` ตาม endpoint metadata; session revoke จะ bump `AuthorizationVersion` และ lease ที่เปิดอยู่ต้อง recheck ก่อน business write.

## API clients และ assertion security

Admin API clients เป็น system credentials ไม่ใช่ browser session. `ApiClients` เก็บ secret hash/hint; plaintext เปิดครั้งเดียวผ่าน one-time ticket. Client key policy pin application/key/algorithm/validity; assertion validation ตรวจ active account/client/key, audience, lifetime และ JTI replay.

## Source of truth

- `src/Domain/Modules/Accounts.Domain/AccountModels.cs`
- `src/Domain/Modules/Accounts.Domain/RegistrationModels.cs`
- `src/Domain/Modules/Access.Domain/AccessModels.cs`
- `src/Application/Modules/Accounts.Application/IdentityAccessContracts.cs`
- `src/Api/Api/IdentityAccess/CanonicalAccessEndpoints.cs`
- `src/Domain/Modules/Iam.Domain/Permissions/Keys.cs`
- `src/Application/Modules/Iam.Application/Permissions/PermissionCatalog.cs`
- `src/Infrastructure/BuildingBlocks.Infrastructure/Persistence/Migrations/20260906151900_SharedRoleScope.cs`
