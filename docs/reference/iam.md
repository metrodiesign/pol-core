# Iam Module Reference

> As-built 2026-08-13. Source of truth: `src/Modules/Iam/Iam.Domain/Permissions/Keys.cs`.

## Catalog

Iam เป็น catalog กลางเดียวสำหรับ Platform และ Merchant scope. ทุก permission สืบทอด scope จาก group;
role grant ข้าม scope ถูก reject ทั้ง domain, persistence และ boot parity guard.

Current seed:

| Scope | Groups | Permissions |
|---|---:|---:|
| Platform | 5 | 18 |
| Merchant | 2 | 8 |
| Total | 7 | 26 |

Groups:

- Platform: `txn`, `merchant`, `user`, `system`, `merchants.users`
- Merchant: `payment`, `roles`

Platform keys เพิ่มสำหรับ Admin control plane ได้แก่ `txn.manage`, `merchants.users.manage`,
`merchants.roles.view` และ `merchants.roles.manage`. Merchant keys เพิ่ม `payment.view`, `users.view`,
`users.manage`, `users.roles` และ `roles.view`/`roles.manage` ตาม catalog ใน source.

Retired: catalog product writes, merchant/admin policy groupsและทุก policy permission.

## Roles and grants

| Role | Scope | Grants | Anchor |
|---|---|---:|---|
| `platform_admin` | Platform | 18 | yes |
| `platform_auditor` | Platform | 4 | no |
| `merchant_manager` | Merchant | 8 | yes |
| `merchant_staff` | Merchant | 3 | no |

รวม 33 seed grants. Anchor role ปิดหรือลบไม่ได้. Shared seed role มี `MerchantId = NULL`; merchant custom role
ต้องมี owner merchant และ visibility confined ด้วย `RoleVisibility`.

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

Migration seed และ integration tests ต้องตรง 26 permissions / 7 groups / 4 roles / 33 grants.
