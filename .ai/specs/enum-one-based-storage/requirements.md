# One-Based Persisted Enum Storage

> Status: approved 2026-08-08
> Date: 2026-08-08

ปรับค่า enum ที่ persist เป็น `int` ใน current database จากชุด 0-based เป็น 1-based โดยคง wire contract ที่ใช้ชื่อ enum เดิม และเพิ่ม data migration ที่ปลอดภัยสำหรับข้อมูลเดิม.

## Scope

- เปลี่ยนเฉพาะ persisted enum fields ที่ระบุใน request นี้
- อัปเดต domain enum, EF mappings, SQL filters, migration, tests และ current reference docs
- คงค่า enum ของ surface ที่ไม่ได้ระบุ เช่น `shop.Carts.Status` และ product enums
- ไม่แก้ historical spec/handoff ที่บันทึกสถานะของงานเก่า

## REQ-1: Persisted enum mapping

**User Story:** As a maintainer, I want persisted enum values to use a consistent 1-based contract, so that database values are unambiguous and match the current data dictionary.

**Acceptance Criteria (EARS):**

- 1.1 THE SYSTEM SHALL persist `admin.Sessions.Status` as `Active=1`, `Superseded=2`, `Revoked=3`.
- 1.2 THE SYSTEM SHALL persist `admin.Users.Tier` as `Scoped=1`, `Super=2`.
- 1.3 THE SYSTEM SHALL persist `admin.Users.Status` as `Active=1`, `Suspended=2`.
- 1.4 THE SYSTEM SHALL persist `iam.PermissionGroups.Scope` as `Platform=1`, `Merchant=2`.
- 1.5 THE SYSTEM SHALL persist `iam.PermissionGroups.Status` as `Active=1`, `Inactive=2`.
- 1.6 THE SYSTEM SHALL persist `iam.Permissions.Status` as `Active=1`, `Inactive=2`.
- 1.7 THE SYSTEM SHALL persist `iam.Roles.Status` as `Active=1`, `Inactive=2`.
- 1.8 THE SYSTEM SHALL persist `iam.Roles.Scope` as `Platform=1`, `Merchant=2`.
- 1.9 THE SYSTEM SHALL persist `cfg.Positions.Status` as `Active=1`, `Inactive=2`.
- 1.10 THE SYSTEM SHALL persist `cfg.Offices.Status` as `Active=1`, `Inactive=2`.
- 1.11 THE SYSTEM SHALL persist `cfg.Levels.Status` as `Active=1`, `Inactive=2`.
- 1.12 THE SYSTEM SHALL persist `cfg.Divisions.Status` as `Active=1`, `Inactive=2`.
- 1.13 THE SYSTEM SHALL persist `merch.Merchants.Status` as `Active=1`, `Inactive=2`.
- 1.14 THE SYSTEM SHALL persist `merch.RegistrationAttempts.Purpose` as `Registration=1`, `Correction=2`.
- 1.15 THE SYSTEM SHALL persist `merch.RegistrationAttempts.IdentityType` as `Individual=1`, `Juristic=2`.
- 1.16 THE SYSTEM SHALL persist `merch.Sessions.Status` as `Active=1`, `Superseded=2`, `Revoked=3`.
- 1.17 THE SYSTEM SHALL persist `merch.Users.Status` as `PendingApproval=1`, `Active=2`, `Rejected=3`, `Suspended=4`.
- 1.18 THE SYSTEM SHALL persist `merch.Users.IdentityType` as `Individual=1`, `Juristic=2`.
- 1.19 THE SYSTEM SHALL persist `shop.Orders.Status` as `Pending=1`, `Paid=2`, `Failed=3`, `Expired=4`, `Refunded=5`, `Cancelled=6`.
- 1.20 THE SYSTEM SHALL persist `txn.PaymentSessions.Psp` as `TwoCTwoP=1`, `Omise=2`.
- 1.21 THE SYSTEM SHALL persist `txn.PaymentSessions.Status` as `Created=1`, `Redirected=2`, `Paid=3`, `Failed=4`, `Expired=5`.
- 1.22 THE SYSTEM SHALL persist `txn.PspConnections.Psp` as `TwoCTwoP=1`, `Omise=2`.

## REQ-2: Runtime behavior

**User Story:** As an operator, I want existing domain behavior to remain name-based while storage values change, so that status transitions and authorization decisions do not regress.

**Acceptance Criteria (EARS):**

- 2.1 THE SYSTEM SHALL map each persisted enum using the updated domain enum values in every migration-owner and runtime `DbContext` configuration.
- 2.2 THE SYSTEM SHALL preserve existing status, authorization, payment, order and PSP behavior when callers use enum names rather than raw integers.
- 2.3 THE SYSTEM SHALL leave non-target enum surfaces unchanged.
- 2.4 IF a SQL query or filtered index depends on persisted enum integers THEN THE SYSTEM SHALL use the new 1-based values.

## REQ-3: Required identity type

**User Story:** As a registration operator, I want every persisted registration profile to identify person type, so that records cannot be stored with ambiguous identity classification.

**Acceptance Criteria (EARS):**

- 3.1 THE SYSTEM SHALL persist `merch.Users.IdentityType` as `NOT NULL`.
- 3.2 THE SYSTEM SHALL persist `merch.RegistrationAttempts.IdentityType` as `NOT NULL`.
- 3.3 IF a registration request omits or supplies an invalid identity type THEN THE SYSTEM SHALL reject the request before writing `merch.Users` or `merch.RegistrationAttempts`.
- 3.4 WHEN a valid registration or correction is persisted THE SYSTEM SHALL write `Individual=1` or `Juristic=2` to both applicable records.

## REQ-4: Forward migration safety

**User Story:** As a database operator, I want migration from the current 0-based database to be deterministic and fail-safe, so that existing rows are not silently misclassified.

**Acceptance Criteria (EARS):**

- 4.1 WHEN the new migration runs against the current schema THE SYSTEM SHALL convert every listed known 0-based persisted enum value to its corresponding 1-based value exactly once.
- 4.2 IF `merch.Users.IdentityType` contains `NULL` THEN THE SYSTEM SHALL abort before changing schema or data and report the blocking condition.
- 4.3 IF `merch.RegistrationAttempts.IdentityType` contains `NULL` THEN THE SYSTEM SHALL abort before changing schema or data and report the blocking condition.
- 4.4 WHEN migration succeeds THE SYSTEM SHALL enforce both identity-type columns as `NOT NULL`.
- 4.5 WHEN migration succeeds THE SYSTEM SHALL enforce the IAM role scope check using `Platform=1` and `Merchant=2`.
- 4.6 WHEN migration succeeds THE SYSTEM SHALL enforce the open payment-session uniqueness filter as `Status IN (1, 2)`.
- 4.7 WHEN migration is rolled back THE SYSTEM SHALL restore the prior 0-based values, nullable identity-type columns, prior IAM scope check and prior payment-session filter.

## REQ-5: Current documentation and verification

**User Story:** As a developer, I want current references and tests to expose the same numeric contract as the database, so that future changes do not reintroduce 0-based assumptions.

**Acceptance Criteria (EARS):**

- 5.1 THE SYSTEM SHALL document every mapping in REQ-1 with its SQL type, nullability and current numeric values.
- 5.2 THE SYSTEM SHALL document the migration preflight behavior for `NULL` identity types.
- 5.3 THE SYSTEM SHALL update current SQL examples, filtered-index descriptions and master-data references to the 1-based values.
- 5.4 THE SYSTEM SHALL verify the updated contract with unit/integration or architecture tests covering enum values, nullable-field rejection, migration shape and dependent SQL predicates.

## Non-goals

- ไม่เปลี่ยน enum ที่ไม่ได้อยู่ใน REQ-1
- ไม่เปลี่ยน JSON/wire value ที่ใช้ชื่อ เช่น `Active`, `Paid`, `2c2p`
- ไม่ backfill `NULL` identity type ด้วยค่าที่เดาเอง
- ไม่แก้ production database โดยตรงในงานพัฒนา; ใช้ migration ผ่าน release workflow
