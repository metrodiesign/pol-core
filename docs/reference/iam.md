# Iam Module Reference

> As-built 2026-08-13. Source of truth: `src/Domain/Modules/Iam.Domain/Permissions/Keys.cs`.

## Catalog

Iam เป็น catalog กลางเดียวสำหรับ Platform (Tier 0), Merchant (Tier 1) และ Shared scope. ทุก permission สืบทอด scope
จาก group; role ฝั่ง Platform/Merchant ถือ key ฝั่งตนรวม Shared ได้, role Shared ถือ Shared เท่านั้น, grant ข้ามฝั่ง
ถูก reject ทั้ง domain, persistence และ boot parity guard.

Current seed (2026-09-06, migration `SharedRoleScope`):

| Scope | Groups | Permissions |
|---|---:|---:|
| Platform | 5 | 17 |
| Merchant | 1 | 5 |
| Shared | 1 | 3 |
| Total | 7 | 25 |

Groups:

- Platform: `txn`, `merchant`, `user`, `system`, `merchants.users`
- Merchant: `roles`
- Shared: `payment` (`payment.view`, `payment.create`, `payment.redirect`) — สิทธิ์ commerce ที่ Tier 0 และ Tier 1
  เรียกร่วมกัน; endpoint `dual-console` ทั้ง 15 จุด gate ด้วย key เดียวนี้ไม่ว่า session ระดับใด

Platform keys สำหรับ Admin control plane ได้แก่ `merchants.users.manage`, `merchants.roles.view` และ
`merchants.roles.manage`. Merchant keys ได้แก่ `users.view`, `users.manage`, `users.roles`, `roles.view`, `roles.manage`.

Retired: `txn.manage` (ฝาแฝดฝั่ง admin ของ `payment.create` — ไม่มี endpoint gate เมื่อ payment เป็น Shared),
catalog product writes, merchant/admin policy groups และทุก policy permission.

## Roles and grants

| Role | Scope | Grants | Anchor |
|---|---|---:|---|
| `platform_admin` | Platform | 20 | yes |
| `platform_auditor` | Platform | 5 | no |
| `merchant_manager` | Merchant | 8 | yes |
| `merchant_staff` | Shared | 3 | no |

รวม 36 seed grants. Anchor role ปิดหรือลบไม่ได้. Seed role มี `MerchantId = NULL`; merchant custom role
ต้องมี owner merchant และ visibility confined ด้วย `RoleVisibility`. Role scope Shared assign ได้ทั้ง
`admin.RoleAssignments` (Tier 0) และ `merch.RoleAssignments` (Tier 1) และปรากฏใน role list ของทั้งสอง console.

## Active-only resolution

Effective permission รวมเฉพาะ:

- user/assignment ที่ยัง valid
- role `Active`
- permission group `Active`
- permission `Active`

การ deactivate catalog itemหรือ roleมีผล request ถัดไป; permission ไม่ถูก cacheใน client claim.

## Persistence and authorization

- Tables: `iam.PermissionGroups`, `iam.Permissions`, `iam.Roles`, `iam.RolePermissions`, `iam.ApiClients`,
  `iam.OneTimeSecretTickets`
- Assignments: `admin.RoleAssignments`, `merch.RoleAssignments`
- Runtime principal: `pol_app`
- `RequirePermission` ตรวจ scope + resolved keys
- `PermissionParity.Assert` fail boot เมื่อ endpointใช้ unknown/wrong-scope key
- Admin และ merchant-user auth scheme แยกขาด

## API clients

Admin API client เป็น credential ของ merchant/originator ไม่ใช่ browser session. `ApiClients` เก็บเฉพาะ
`SecretHash` และ `SecretHint`; secret plaintext อยู่ใน one-time reveal flow เท่านั้น. การ create/update/revoke
ใช้ `Idempotency-Key` และ `If-Match` ตาม operation และการหมุน secret สร้าง maker-checker approval ก่อน activate.

Routes อยู่ใต้ `/api/v1/api-clients` และใช้ `apikey.manage`; รายละเอียด request/response อยู่ใน
[`admin-control-plane.md`](admin-control-plane.md).

Migration seed และ integration tests ต้องตรง 25 permissions / 7 groups / 4 roles / 36 grants.
