# Iam Module Reference

> As-built 2026-08-07. Source of truth: `src/Modules/Iam/Iam.Domain/Permissions/Keys.cs`.

## Catalog

Iam เป็น catalog กลางเดียวสำหรับ Platform และ Merchant scope. ทุก permission สืบทอด scope จาก group;
role grant ข้าม scope ถูก reject ทั้ง domain, persistence และ boot parity guard.

Current seed:

| Scope | Groups | Permissions |
|---|---:|---:|
| Platform | 5 | 14 |
| Merchant | 2 | 5 |
| Total | 7 | 19 |

Groups:

- Platform: `txn`, `merchant`, `user`, `system`, `merchants.users`
- Merchant: `payment`, `roles`

Retired: catalog product writes, merchant/admin policy groupsและทุก policy permission.

## Roles and grants

| Role | Scope | Grants | Anchor |
|---|---|---:|---|
| `platform_admin` | Platform | 14 | yes |
| `platform_auditor` | Platform | 4 | no |
| `merchant_manager` | Merchant | 5 | yes |
| `merchant_staff` | Merchant | 2 | no |

รวม 25 seed grants. Anchor role ปิดหรือลบไม่ได้. Shared seed role มี `MerchantId = NULL`; merchant custom role
ต้องมี owner merchant และ visibility confined ด้วย `RoleVisibility`.

## Active-only resolution

Effective permission รวมเฉพาะ:

- user/assignment ที่ยัง valid
- role `Active`
- permission group `Active`
- permission `Active`

การ deactivate catalog itemหรือ roleมีผล request ถัดไป; permission ไม่ถูก cacheใน client claim.

## Persistence and authorization

- Tables: `iam.PermissionGroups`, `iam.Permissions`, `iam.Roles`, `iam.RolePermissions`
- Assignments: `admin.RoleAssignments`, `merch.RoleAssignments`
- Runtime principal: `pol_app`
- `RequirePermission` ตรวจ scope + resolved keys
- `PermissionParity.Assert` fail boot เมื่อ endpointใช้ unknown/wrong-scope key
- Admin และ merchant-user auth scheme แยกขาด

Migration seedและ integration testsต้องตรง 19/7/4/25 exact counts.
