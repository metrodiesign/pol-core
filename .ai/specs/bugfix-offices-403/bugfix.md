# Bugfix: Offices authorization after one-based migration

บันทึกรากเหตุและขอบเขตแก้ไข `GET /api/v1/offices` ที่คืน `403` ให้ bootstrap admin หลัง DB เปลี่ยน persisted enums เป็น one-based

> Status: approved 2026-08-09

## Current Behavior (Defect)

WHEN local DB มี migration `20260808161508_OneBasedPersistedEnumStorage` แล้ว แต่ Kestrel เก่ายังรัน binary ก่อน migration และ bootstrap admin `AEDA3369-394B-466B-9888-B51454486B7D` เรียก `GET /api/v1/offices?page=1&limit=25` THEN THE SYSTEM คืน `403 Forbidden`

หลักฐานก่อนแก้:

- DB migration history คืน `APPLIED`; IAM roles, permissions และ groups เป็น `Active=1` กับ `Platform=1`
- target admin เป็น stale `Tier=1, Status=0`, ไม่มี role assignment และไม่มี effective permission `user.manage`
- session ล่าสุดของ target ถูก Kestrel เก่าเขียน `Status=0`; current source กำหนด `SessionStatus.Active=1`
- endpoint gate ถูกตาม business policy: `admin` + `user.manage`; จุดคืน `403` อยู่ที่ permission filter เมื่อ effective set ไม่มี key

## Expected Behavior

- F-1 WHEN authenticated target bootstrap admin มี active `platform_admin` assignment THE SYSTEM SHALL resolve effective permission `user.manage` และ return `200 OK` จาก `GET /api/v1/offices?page=1&limit=25`.
- F-2 WHEN authenticated admin ไม่มี effective permission `user.manage` THE SYSTEM SHALL return `403 Forbidden` โดยไม่เรียก Offices store.
- F-3 WHEN request ไม่มี valid admin session THE SYSTEM SHALL return `401 Unauthorized`.
- F-4 WHEN authorized admin เรียก Offices ด้วย `GET` โดยไม่มี CSRF token THE SYSTEM SHALL process request โดยไม่เพิ่ม CSRF requirement.

## Unchanged Behavior

- B-1 WHEN master-data endpoint ถูกเรียก THE SYSTEM SHALL CONTINUE TO require policy `admin` และ permission `user.manage`.
- B-2 WHEN admin มี `Tier.Super` แต่ไม่มี `user.manage` THE SYSTEM SHALL CONTINUE TO deny action; Tier SHALL NOT bypass permission.
- B-3 WHEN master-data mutation ใช้ unsafe HTTP method THE SYSTEM SHALL CONTINUE TO require valid admin CSRF token.
- B-4 WHEN sibling master-data list endpointsถูก enumerate THE SYSTEM SHALL CONTINUE TO expose `user.manage` gate โดยไม่มี policy drift.
- B-5 WHEN active role, permission, group หรือ assignment เปลี่ยน THE SYSTEM SHALL CONTINUE TO resolve effective permissions fresh on next request.
- B-6 WHEN current-environment repair runs THE SYSTEM SHALL CONTINUE TO leave every admin account except `AEDA3369-394B-466B-9888-B51454486B7D` unchanged.
- B-7 WHEN Offices list receives `page=1&limit=25` THE SYSTEM SHALL CONTINUE TO pass those values to `IOfficeStore.ListAsync`.

## Hard Scope

- ห้ามแก้ `src/Hosts/Api/Program.cs`, permission vocabulary, role seed หรือ migration
- ห้ามแก้ frontend, authentication/session mechanism หรือ CSRF implementation
- ห้าม grant role ให้ admin อื่น และห้าม hardcode account ใน production code
