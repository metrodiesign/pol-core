# Entity and Field Reference

เอกสารนี้คือ persisted schema ปัจจุบันของ `VCentralPay` ตาม migration chain วันที่ 2026-08-08. ครอบคลุมทุก table, field, key, foreign key และ index ที่ระบบสร้างเอง; ไม่รวมข้อมูลจาก upstream product catalogue.

## Database baseline

| รายการ | ค่าปัจจุบัน |
|---|---|
| Engine | SQL Server 2025 build `17.0.4045.5` ขึ้นไป |
| Compatibility level | `170` |
| Collation | `Thai_100_CI_AS` |
| Migration chain | `20260807042818_InitialSchema` → `20260807042828_SecurityObjects` → `20260807042833_SeedData` → `20260808161508_OneBasedPersistedEnumStorage` |
| Runtime principal | `pol_app` |
| Runtime contexts | `ControlPlaneDbContext`, `MerchantUserDbContext`, `MerchantRuntimeDbContext` |
| Migration context | `PolDbContext` เท่านั้น |
| Tenant isolation | app-layer query filter + guarded write; ไม่มี SQL RLS, `SESSION_CONTEXT` หรือ bypass principal |

สัญลักษณ์ในตาราง field: `PK` = primary key, `FK` = foreign key, `NN` = `NOT NULL`, `NULL` = nullable, `IDENTITY` = database-generated identity, `ROWVERSION` = SQL Server rowversion.

`OneBasedPersistedEnumStorage` แปลงค่า legacy แบบ 0-based เป็น mapping one-based ตามตารางด้านล่างด้วย `CASE` แบบ explicit
ครบทุก target field. Migration ตรวจ `NULL` และค่า legacy ที่อยู่นอกช่วงก่อนทำ data/schema change; ถ้า
`merch.Users.IdentityType` หรือ `merch.RegistrationAttempts.IdentityType` เป็น `NULL` จะหยุดทันทีโดยไม่ backfill และไม่
เปลี่ยน schema. `Down` ตรวจค่าปัจจุบันก่อนแปลงกลับเช่นเดียวกัน.

## Schema ownership

| Schema | Tables | Runtime owner |
|---|---|---|
| `admin` | platform users, sessions, access, role assignments, audit, provisioning ledger | `ControlPlaneDbContext` |
| `iam` | permission groups, permissions, roles, grants | `ControlPlaneDbContext` |
| `cfg` | positions, offices, levels, divisions | `ControlPlaneDbContext` |
| `merch` | merchants, merchant users, registration, vault, user outbox | `MerchantUserDbContext` / `MerchantRuntimeDbContext` |
| `shop` | carts, cart items, orders, order items, reveal audit | `MerchantRuntimeDbContext` |
| `txn` | payment sessions, PSP connections, idempotency, outbox | `MerchantRuntimeDbContext` |
| `dbo` | ASP.NET Data Protection keys, EF migration history | framework / migration owner |

## `admin` schema

### `admin.AuthAudits`

| Field | SQL type | Null | ความหมาย |
|---|---|---|---|
| `Id` | `uniqueidentifier` | NN, PK | audit row id |
| `EventType` | `nvarchar(32)` | NN | authentication event |
| `AdminUserId` | `uniqueidentifier` | NULL | resolved admin, ถ้าไม่มี account จะว่าง |
| `Subject` | `nvarchar(256)` | NULL | external identity subject |
| `Reason` | `nvarchar(128)` | NULL | เหตุผลของผลลัพธ์ |
| `CorrelationId` | `nvarchar(128)` | NN | request correlation |
| `OccurredAt` | `datetime2` | NN | เวลาเกิด event |

### `admin.MerchantAccess`

| Field | SQL type | Null | ความหมาย |
|---|---|---|---|
| `Id` | `uniqueidentifier` | NN, PK | assignment id |
| `AdminUserId` | `uniqueidentifier` | NN | admin ที่ได้รับสิทธิ์ |
| `MerchantId` | `uniqueidentifier` | NN | merchant ที่เข้าถึงได้ |
| `AssignedByAdminId` | `uniqueidentifier` | NN | ผู้มอบสิทธิ์ |
| `AssignedAt` | `datetime2` | NN | เวลามอบสิทธิ์ |

### `admin.ProvisioningOperations`

| Field | SQL type | Null | ความหมาย |
|---|---|---|---|
| `Id` | `uniqueidentifier` | NN, PK | operation id |
| `OperationKey` | `nvarchar(200)` | NN | idempotency key ของ provisioning |
| `CallerAdminId` | `uniqueidentifier` | NN | admin ผู้เรียก |
| `ExpectedAuthorizationVersion` | `bigint` | NN | authorization version ที่ caller ยืนยัน |
| `RequestHash` | `nvarchar(64)` | NN | hash ของ request |
| `MerchantId` | `uniqueidentifier` | NN | merchant ที่กำลัง provision |
| `Result` | `json` | NULL | closed provisioning result payload |
| `CreatedAt` | `datetime2` | NN | เวลาสร้าง operation |

### `admin.RoleAssignments`

| Field | SQL type | Null | ความหมาย |
|---|---|---|---|
| `Id` | `uniqueidentifier` | NN, PK | assignment id |
| `AdminUserId` | `uniqueidentifier` | NN | admin ที่ได้รับ role |
| `RoleId` | `uniqueidentifier` | NN, FK | อ้าง `iam.Roles.Id` |
| `AssignedById` | `uniqueidentifier` | NN | ผู้มอบ role |
| `AssignedAt` | `datetime2` | NN | เวลามอบ role |

### `admin.Sessions`

| Field | SQL type | Null | ความหมาย |
|---|---|---|---|
| `Id` | `uniqueidentifier` | NN, PK | session id |
| `FamilyId` | `uniqueidentifier` | NN | session family สำหรับ revoke ทั้งชุด |
| `TokenHash` | `varbinary(32)` | NN | SHA-256 hash; ไม่เก็บ raw token |
| `AdminUserId` | `uniqueidentifier` | NN | owner admin |
| `Status` | `int` | NN | `Active=1`, `Superseded=2`, `Revoked=3` |
| `IssuedAt` | `datetime2` | NN | เวลาออก session |
| `IdleExpiresAt` | `datetime2` | NN | idle expiry |
| `AbsoluteExpiresAt` | `datetime2` | NN | absolute expiry |
| `SupersededAt` | `datetime2` | NULL | เวลาที่ถูกแทนที่ |
| `SupersededBySessionId` | `uniqueidentifier` | NULL | session ใหม่ที่แทนที่ |
| `IpAddress` | `nvarchar(45)` | NULL | client IP |
| `UserAgent` | `nvarchar(256)` | NULL | client user agent |

### `admin.UserAudits`

| Field | SQL type | Null | ความหมาย |
|---|---|---|---|
| `Id` | `uniqueidentifier` | NN, PK | audit row id |
| `Action` | `nvarchar(64)` | NN | action ที่เกิดขึ้น |
| `ActorType` | `nvarchar(16)` | NN | ประเภท actor |
| `ActorId` | `uniqueidentifier` | NN | actor id |
| `TargetAdminId` | `uniqueidentifier` | NULL | admin เป้าหมาย |
| `MerchantId` | `uniqueidentifier` | NULL | merchant context ถ้ามี |
| `TargetRoleId` | `uniqueidentifier` | NULL | role เป้าหมายถ้ามี |
| `CorrelationId` | `nvarchar(128)` | NN | request correlation |
| `OccurredAt` | `datetime2` | NN | เวลาเกิด action |

### `admin.Users`

| Field | SQL type | Null | ความหมาย |
|---|---|---|---|
| `Id` | `uniqueidentifier` | NN, PK | admin user id |
| `Subject` | `nvarchar(256)` | NULL | external identity subject; unique เมื่อมีค่า |
| `Email` | `nvarchar(320)` | NN | email; unique |
| `Tier` | `int` | NN | `Scoped=1`, `Super=2` |
| `Status` | `int` | NN | `Active=1`, `Suspended=2` |
| `AuthorizationVersion` | `bigint` | NN | invalidation version |
| `CreatedAt` | `datetime2` | NN | เวลาสร้าง |
| `UpdatedAt` | `datetime2` | NULL | เวลาแก้ไขล่าสุด |
| `PositionId` | `uniqueidentifier` | NULL, FK | อ้าง `cfg.Positions.Id` |
| `OfficeId` | `uniqueidentifier` | NULL, FK | อ้าง `cfg.Offices.Id` |
| `LevelId` | `uniqueidentifier` | NULL, FK | อ้าง `cfg.Levels.Id` |
| `DivisionId` | `uniqueidentifier` | NULL, FK | อ้าง `cfg.Divisions.Id` |

## `iam` schema

### `iam.PermissionGroups`

| Field | SQL type | Null | ความหมาย |
|---|---|---|---|
| `Key` | `nvarchar(32)` | NN, PK | group key |
| `Scope` | `int` | NN | `Platform=1`, `Merchant=2` |
| `Name` | `nvarchar(128)` | NN | display name |
| `Status` | `int` | NN | `Active=1`, `Inactive=2` |
| `SortOrder` | `int` | NN | ลำดับแสดงผล |

### `iam.Permissions`

| Field | SQL type | Null | ความหมาย |
|---|---|---|---|
| `Key` | `nvarchar(64)` | NN, PK | permission key |
| `GroupKey` | `nvarchar(32)` | NN, FK | อ้าง `iam.PermissionGroups.Key` |
| `Name` | `nvarchar(160)` | NN | display name |
| `Status` | `int` | NN | `Active=1`, `Inactive=2` |
| `SortOrder` | `int` | NN | ลำดับแสดงผล |

### `iam.RolePermissions`

| Field | SQL type | Null | ความหมาย |
|---|---|---|---|
| `Id` | `uniqueidentifier` | NN, PK | grant id |
| `RoleId` | `uniqueidentifier` | NN, FK | อ้าง `iam.Roles.Id` |
| `PermissionKey` | `nvarchar(64)` | NN, FK | อ้าง `iam.Permissions.Key` |

### `iam.Roles`

| Field | SQL type | Null | ความหมาย |
|---|---|---|---|
| `Id` | `uniqueidentifier` | NN, PK | role id |
| `Code` | `nvarchar(64)` | NN | role code |
| `Name` | `nvarchar(128)` | NN | display name |
| `Description` | `nvarchar(256)` | NULL | คำอธิบาย |
| `Color` | `nvarchar(16)` | NULL | display color |
| `Status` | `int` | NN | `Active=1`, `Inactive=2` |
| `Scope` | `int` | NN | `Platform=1`, `Merchant=2` |
| `MerchantId` | `uniqueidentifier` | NULL | owner merchant; platform role ต้องเป็น `NULL` |

Check constraint: `CK_Roles_ScopeMerchant` บังคับ `Scope=1` ต้องมี `MerchantId IS NULL`; `Scope=2` ต้องเป็น merchant role.

## `cfg` schema

`cfg.Positions`, `cfg.Offices`, `cfg.Levels` และ `cfg.Divisions` มี field shape เดียวกัน.

### `cfg.Positions`

| Field | SQL type | Null | ความหมาย |
|---|---|---|---|
| `Id` | `uniqueidentifier` | NN, PK | reference row id |
| `Code` | `nvarchar(64)` | NN | code; unique ต่อ table |
| `Name` | `nvarchar(200)` | NN | display name |
| `Status` | `int` | NN | `Active=1`, `Inactive=2` |

### `cfg.Offices`

| Field | SQL type | Null | ความหมาย |
|---|---|---|---|
| `Id` | `uniqueidentifier` | NN, PK | reference row id |
| `Code` | `nvarchar(64)` | NN | code; unique ต่อ table |
| `Name` | `nvarchar(200)` | NN | display name |
| `Status` | `int` | NN | `Active=1`, `Inactive=2` |

### `cfg.Levels`

| Field | SQL type | Null | ความหมาย |
|---|---|---|---|
| `Id` | `uniqueidentifier` | NN, PK | reference row id |
| `Code` | `nvarchar(64)` | NN | code; unique ต่อ table |
| `Name` | `nvarchar(200)` | NN | display name |
| `Status` | `int` | NN | `Active=1`, `Inactive=2` |

### `cfg.Divisions`

| Field | SQL type | Null | ความหมาย |
|---|---|---|---|
| `Id` | `uniqueidentifier` | NN, PK | reference row id |
| `Code` | `nvarchar(64)` | NN | code; unique ต่อ table |
| `Name` | `nvarchar(200)` | NN | display name |
| `Status` | `int` | NN | `Active=1`, `Inactive=2` |

## `merch` schema

### `merch.AuthAudits`

| Field | SQL type | Null | ความหมาย |
|---|---|---|---|
| `Id` | `uniqueidentifier` | NN, PK | audit row id |
| `EventType` | `nvarchar(32)` | NN | authentication event |
| `UserId` | `uniqueidentifier` | NULL | resolved merchant user |
| `Subject` | `nvarchar(256)` | NULL | external identity subject |
| `Reason` | `nvarchar(128)` | NULL | เหตุผลของผลลัพธ์ |
| `CorrelationId` | `nvarchar(128)` | NN | request correlation |
| `OccurredAt` | `datetime2` | NN | เวลาเกิด event |

### `merch.ExternalLogins`

| Field | SQL type | Null | ความหมาย |
|---|---|---|---|
| `Id` | `uniqueidentifier` | NN, PK | external login id |
| `Provider` | `nvarchar(32)` | NN | identity provider |
| `Subject` | `nvarchar(256)` | NN | provider subject |
| `UserId` | `uniqueidentifier` | NN | merchant user owner |

### `merch.Merchants`

| Field | SQL type | Null | ความหมาย |
|---|---|---|---|
| `Id` | `uniqueidentifier` | NN, PK | merchant id |
| `Code` | `nvarchar(64)` | NN | merchant code; unique |
| `Name` | `nvarchar(200)` | NN | merchant name |
| `Note` | `nvarchar(max)` | NULL | internal note |
| `Status` | `int` | NN | `Active=1`, `Inactive=2` |
| `Country` | `nvarchar(2)` | NN | ISO country code |
| `Currency` | `nvarchar(3)` | NN | currency code |
| `EnabledChannels` | `nvarchar(256)` | NN | enabled payment channels |
| `CreatedAt` | `datetime2` | NN | เวลาสร้าง |
| `Metadata` | `json` | NN | typed merchant extension; ห้ามเก็บ secret/PII ที่ไม่จำเป็น |

### `merch.ProvisioningAudits`

| Field | SQL type | Null | ความหมาย |
|---|---|---|---|
| `Id` | `uniqueidentifier` | NN, PK | audit row id |
| `MerchantId` | `uniqueidentifier` | NN | merchant ที่ provision |
| `MerchantCode` | `nvarchar(64)` | NN | merchant code snapshot |
| `AdminSubject` | `nvarchar(256)` | NN | admin subject |
| `CorrelationId` | `nvarchar(128)` | NN | request correlation |
| `OccurredAt` | `datetime2` | NN | เวลาเกิด action |

### `merch.RegistrationAudits`

| Field | SQL type | Null | ความหมาย |
|---|---|---|---|
| `Id` | `uniqueidentifier` | NN, PK | audit row id |
| `Action` | `nvarchar(64)` | NN | registration action |
| `ActorSubject` | `nvarchar(256)` | NULL | subject ของผู้ทำ action |
| `TargetSubject` | `nvarchar(256)` | NN | subject ของผู้สมัคร |
| `Role` | `nvarchar(64)` | NULL | role ที่เกี่ยวข้อง |
| `Reason` | `nvarchar(1024)` | NULL | เหตุผล |
| `MerchantId` | `uniqueidentifier` | NULL | merchant context; ว่างก่อน approval |
| `CorrelationId` | `nvarchar(128)` | NN | request correlation |
| `OccurredAt` | `datetime2` | NN | เวลาเกิด action |

### `merch.RegistrationAttempts`

| Field | SQL type | Null | ความหมาย |
|---|---|---|---|
| `Id` | `uniqueidentifier` | NN, PK | attempt id |
| `UserId` | `uniqueidentifier` | NN, FK | อ้าง `merch.Users.Id` |
| `AttemptNo` | `int` | NN | ลำดับ attempt ต่อ user |
| `Purpose` | `int` | NN | `Registration=1`, `Correction=2` |
| `FirstName` | `nvarchar(200)` | NN | snapshot ชื่อ |
| `LastName` | `nvarchar(200)` | NN | snapshot นามสกุล |
| `IdentityType` | `int` | NN | `Individual=1`, `Juristic=2` |
| `IdentityNumber` | `nvarchar(64)` | NULL | เลขระบุตัวตน |
| `SaleCode` | `varchar(20)` | NULL | sales code |
| `LicenseNumber` | `nvarchar(64)` | NULL | ใบอนุญาต |
| `Phone` | `nvarchar(32)` | NULL | เบอร์โทรศัพท์ |
| `Email` | `nvarchar(320)` | NN | email snapshot |
| `PhotoObjectKey` | `nvarchar(256)` | NULL | opaque profile photo key |
| `PhotoContentType` | `nvarchar(128)` | NULL | validated media type |
| `SubmittedAt` | `datetime2` | NN | เวลาส่ง attempt |

เป็น append-only history; unique index `(UserId, AttemptNo)`.

### `merch.RegistrationNotices`

สร้างด้วย raw SQL ใน `SecurityObjects` และ exclude จาก EF migration model ของ runtime context.

| Field | SQL type | Null | ความหมาย |
|---|---|---|---|
| `Id` | `uniqueidentifier` | NN, PK | notice id |
| `UserId` | `uniqueidentifier` | NN | applicant user id |
| `Subject` | `nvarchar(256)` | NN | external identity subject |
| `Email` | `nvarchar(320)` | NN | email |
| `DisplayName` | `nvarchar(200)` | NN | display name |
| `HostedDomain` | `nvarchar(256)` | NULL | identity hosted domain |
| `OccurredAt` | `datetime2` | NN | เวลาเกิด registration event |
| `CreatedAt` | `datetime2` | NN | เวลา persist notice |

### `merch.RoleAssignments`

| Field | SQL type | Null | ความหมาย |
|---|---|---|---|
| `Id` | `uniqueidentifier` | NN, PK | assignment id |
| `UserId` | `uniqueidentifier` | NN | merchant user |
| `RoleId` | `uniqueidentifier` | NN, FK | อ้าง `iam.Roles.Id` |
| `MerchantId` | `uniqueidentifier` | NN | tenant boundary |
| `AssignedById` | `uniqueidentifier` | NN | ผู้มอบ role |
| `AssignedAt` | `datetime2` | NN | เวลามอบ role |

### `merch.Sessions`

| Field | SQL type | Null | ความหมาย |
|---|---|---|---|
| `Id` | `uniqueidentifier` | NN, PK | session id |
| `FamilyId` | `uniqueidentifier` | NN | session family |
| `TokenHash` | `varbinary(32)` | NN | SHA-256 hash; ไม่เก็บ raw token |
| `UserId` | `uniqueidentifier` | NN | merchant user owner |
| `Status` | `int` | NN | `Active=1`, `Superseded=2`, `Revoked=3` |
| `IssuedAt` | `datetime2` | NN | เวลาออก session |
| `IdleExpiresAt` | `datetime2` | NN | idle expiry |
| `AbsoluteExpiresAt` | `datetime2` | NN | absolute expiry |
| `SupersededAt` | `datetime2` | NULL | เวลาถูกแทนที่ |
| `SupersededBySessionId` | `uniqueidentifier` | NULL | session ใหม่ |
| `IpAddress` | `nvarchar(45)` | NULL | client IP |
| `UserAgent` | `nvarchar(256)` | NULL | client user agent |

### `merch.UserOutbox`

| Field | SQL type | Null | ความหมาย |
|---|---|---|---|
| `Id` | `uniqueidentifier` | NN, PK | message id |
| `MerchantId` | `uniqueidentifier` | NN | tenant boundary |
| `Type` | `nvarchar(256)` | NN | event type |
| `Payload` | `json` | NN | closed registration/KYC lifecycle payload |
| `OccurredAt` | `datetime2` | NN | เวลาเกิด event |
| `ProcessedAt` | `datetime2` | NULL | เวลาประมวลผลสำเร็จ |
| `Attempts` | `int` | NN | จำนวนครั้งที่พยายามส่ง |
| `Error` | `nvarchar(2048)` | NULL | error ล่าสุด |
| `LeaseExpiresAt` | `datetime2` | NULL | lease expiry |
| `LeaseOwner` | `nvarchar(256)` | NULL | worker ที่ถือ lease |

### `merch.Users`

| Field | SQL type | Null | ความหมาย |
|---|---|---|---|
| `Id` | `uniqueidentifier` | NN, PK | merchant user id |
| `Subject` | `nvarchar(256)` | NN | external identity subject; unique |
| `Email` | `nvarchar(320)` | NN | email |
| `Status` | `int` | NN | `PendingApproval=1`, `Active=2`, `Rejected=3`, `Suspended=4` |
| `MerchantId` | `uniqueidentifier` | NULL | merchant binding; pending user อาจเป็น `NULL` |
| `CreatedAt` | `datetime2` | NN | เวลาสร้าง |
| `DisplayName` | `nvarchar(200)` | NN | ชื่อแสดงผลที่ server คำนวณ |
| `FirstName` | `nvarchar(200)` | NN | ชื่อ |
| `LastName` | `nvarchar(200)` | NN | นามสกุล |
| `IdentityType` | `int` | NN | `Individual=1`, `Juristic=2` |
| `IdentityNumber` | `nvarchar(64)` | NULL | เลขระบุตัวตน |
| `SaleCode` | `varchar(20)` | NULL | sales code |
| `LicenseNumber` | `nvarchar(64)` | NULL | ใบอนุญาต |
| `Phone` | `nvarchar(32)` | NULL | เบอร์โทรศัพท์ |
| `PhotoObjectKey` | `nvarchar(256)` | NULL | opaque profile photo key; ไม่ใช่ binary/path |
| `PhotoContentType` | `nvarchar(128)` | NULL | validated media type |
| `KycPhotoObjectKey` | `nvarchar(256)` | NULL | opaque KYC photo key; binary อยู่ object store |

### `merch.VaultRevealAudits`

| Field | SQL type | Null | ความหมาย |
|---|---|---|---|
| `Id` | `bigint` | NN, PK, IDENTITY | audit sequence id |
| `MerchantId` | `uniqueidentifier` | NN | merchant boundary |
| `SecretName` | `nvarchar(128)` | NN | secret ที่ถูก reveal |
| `Seq` | `bigint` | NN | per-merchant audit sequence |
| `PrevHash` | `varbinary(32)` | NN | hash ก่อนหน้า |
| `Hash` | `varbinary(32)` | NN | hash ของ audit row |
| `RevealedAt` | `datetime2` | NN | เวลา reveal |

### `merch.VaultSecrets`

| Field | SQL type | Null | ความหมาย |
|---|---|---|---|
| `MerchantId` | `uniqueidentifier` | NN, PK | composite key ส่วน merchant |
| `SecretName` | `nvarchar(128)` | NN, PK | composite key ส่วนชื่อ secret |
| `SecretKey` | `nvarchar(64)` | NN | key reference |
| `EncryptedDek` | `varbinary(max)` | NN | encrypted data-encryption key |
| `EncryptedSecret` | `varbinary(max)` | NN | encrypted credential payload |
| `Hint` | `nvarchar(16)` | NN | non-secret hint |
| `CreatedAt` | `datetime2` | NN | เวลาสร้าง |
| `UpdatedAt` | `datetime2` | NN | เวลาแก้ไขล่าสุด |

## `shop` schema

### `shop.Carts`

| Field | SQL type | Null | ความหมาย |
|---|---|---|---|
| `Id` | `uniqueidentifier` | NN, PK | cart id |
| `MerchantId` | `uniqueidentifier` | NN | tenant boundary |
| `SaleCode` | `varchar(20)` | NULL | actor/server sale code |
| `Status` | `nvarchar(16)` | NN | `Open` หรือ `CheckedOut` |
| `CreatedAt` | `datetime2` | NN | เวลาสร้าง |
| `Version` | `int` | NN | application-managed optimistic concurrency |

Alternate key: `(Id, MerchantId)` สำหรับ composite child foreign key.

### `shop.CartItems`

| Field | SQL type | Null | ความหมาย |
|---|---|---|---|
| `Id` | `uniqueidentifier` | NN, PK | client-minted mutation handle |
| `CartId` | `uniqueidentifier` | NN, FK | อ้าง `shop.Carts.Id` ร่วมกับ `MerchantId` |
| `MerchantId` | `uniqueidentifier` | NN, FK-part | tenant boundary และ FK-part |
| `ProductCode` | `nvarchar(150)` | NN | upstream document/product code |
| `SaleCode` | `varchar(20)` | NN | server-owned sale code |
| `VariantCode` | `varchar(64)` | NN | upstream product-group code |
| `VariantName` | `nvarchar(128)` | NULL | display snapshot |
| `Quantity` | `int` | NN | จำนวนสินค้า; ต้องมากกว่า 0 ที่ domain |
| `Metadata` | `json` | NULL | typed PII-free item snapshot |
| `UnitPriceAmount` | `decimal(19,4)` | NN | server-owned unit price |
| `UnitPriceCurrency` | `char(3)` | NN | currency code |

### `shop.Orders`

| Field | SQL type | Null | ความหมาย |
|---|---|---|---|
| `Id` | `uniqueidentifier` | NN, PK | order id |
| `MerchantId` | `uniqueidentifier` | NN | tenant boundary |
| `OrderNo` | `varchar(13)` | NN | human-facing order number; unique |
| `SaleCode` | `varchar(20)` | NULL | sale code snapshot |
| `PaymentSessionId` | `uniqueidentifier` | NULL | current payment session reference |
| `Status` | `int` | NN | `Pending=1`, `Paid=2`, `Failed=3`, `Expired=4`, `Refunded=5`, `Cancelled=6` |
| `CreatedAt` | `datetime2` | NN | เวลาสร้าง order |
| `PaidAt` | `datetime2` | NULL | เวลายืนยันจ่ายสำเร็จ |
| `SummaryToken` | `nvarchar(64)` | NN | customer summary token; unique |
| `SummaryTokenExpiresAt` | `datetime2` | NN | token expiry |
| `NotificationRecipient` | `nvarchar(320)` | NULL | recipient สำหรับ notification |
| `PaymentChannel` | `varchar(20)` | NULL | payment channel snapshot |
| `CustomerName` | `nvarchar(200)` | NN | customer PII |
| `CustomerPhone` | `varchar(20)` | NN | customer PII |
| `CustomerEmail` | `nvarchar(320)` | NULL | customer PII |
| `AmountAmount` | `decimal(19,4)` | NN | order amount |
| `AmountCurrency` | `char(3)` | NN | order currency |

Alternate key: `(Id, MerchantId)` สำหรับ composite child foreign key.

### `shop.OrderItems`

| Field | SQL type | Null | ความหมาย |
|---|---|---|---|
| `Id` | `uniqueidentifier` | NN, PK | order item id |
| `OrderId` | `uniqueidentifier` | NN, FK | อ้าง `shop.Orders.Id` ร่วมกับ `MerchantId` |
| `MerchantId` | `uniqueidentifier` | NN, FK-part | tenant boundary และ FK-part |
| `Quantity` | `int` | NN | จำนวน item |
| `ProductCode` | `nvarchar(150)` | NN | product/document snapshot |
| `VariantCode` | `varchar(64)` | NN | variant snapshot |
| `VariantName` | `nvarchar(128)` | NULL | display snapshot |
| `Metadata` | `json` | NULL | immutable typed item snapshot |
| `DiscountAmount` | `decimal(19,4)` | NN | discount amount; ปัจจุบันสร้างเป็นศูนย์ |
| `DiscountCurrency` | `char(3)` | NN | discount currency |
| `UnitPriceAmount` | `decimal(19,4)` | NN | immutable unit price |
| `UnitPriceCurrency` | `char(3)` | NN | unit price currency |

### `shop.OrderItemRevealAudits`

| Field | SQL type | Null | ความหมาย |
|---|---|---|---|
| `Id` | `uniqueidentifier` | NN, PK | audit row id |
| `OrderItemId` | `uniqueidentifier` | NN | item ที่ถูก reveal |
| `MerchantId` | `uniqueidentifier` | NN | tenant boundary |
| `ActorType` | `nvarchar(32)` | NN | ประเภท actor |
| `ActorId` | `nvarchar(200)` | NN | actor identifier |
| `CorrelationId` | `nvarchar(200)` | NN | request correlation |
| `RevealedAt` | `datetime2` | NN | เวลา reveal |

เป็น append-only audit; ไม่มี FK เพื่อคง context boundary และการเขียนผ่าน narrow audit port.

### `shop.OrderNoSeq`

| รายการ | SQL definition | ความหมาย |
|---|---|---|
| Object | `SEQUENCE` | database sequence สำหรับ `OrderNo` |
| Type | `bigint` | ค่าที่คืนจาก sequence |
| Start / increment | `1 / 1` | เริ่มที่ 1 เพิ่มทีละ 1 |
| Cycle | `NO CYCLE` | ไม่วนกลับ |

## `txn` schema

### `txn.IdempotencyRecords`

| Field | SQL type | Null | ความหมาย |
|---|---|---|---|
| `Key` | `nvarchar(400)` | NN, PK | idempotency key |
| `MerchantId` | `uniqueidentifier` | NN | tenant boundary |
| `Context` | `nvarchar(256)` | NN | operation context |
| `CreatedAt` | `datetime2` | NN | เวลาสร้าง record |

### `txn.OutboxMessages`

| Field | SQL type | Null | ความหมาย |
|---|---|---|---|
| `Id` | `uniqueidentifier` | NN, PK | message id |
| `MerchantId` | `uniqueidentifier` | NN | tenant boundary |
| `Type` | `nvarchar(256)` | NN | event type |
| `SchemaVersion` | `varchar(16)` | NN | event schema version |
| `Payload` | `nvarchar(max)` | NN | serialized event; ไม่ใช่ native SQL `json` column |
| `OccurredAt` | `datetime2` | NN | เวลาเกิด event |
| `ProcessedAt` | `datetime2` | NULL | เวลาประมวลผลสำเร็จ |
| `Attempts` | `int` | NN | จำนวนครั้งที่พยายามส่ง |
| `Error` | `nvarchar(2048)` | NULL | error ล่าสุด |
| `LeaseExpiresAt` | `datetime2` | NULL | lease expiry |
| `LeaseOwner` | `nvarchar(256)` | NULL | worker ที่ถือ lease |

### `txn.PaymentSessions`

| Field | SQL type | Null | ความหมาย |
|---|---|---|---|
| `Id` | `uniqueidentifier` | NN, PK | payment session id |
| `MerchantId` | `uniqueidentifier` | NN | tenant boundary |
| `OrderId` | `uniqueidentifier` | NN | order ที่กำลังจ่าย |
| `Method` | `nvarchar(32)` | NN | canonical payment method |
| `Psp` | `int` | NN | `TwoCTwoP=1`, `Omise=2` |
| `Status` | `int` | NN | `Created=1`, `Redirected=2`, `Paid=3`, `Failed=4`, `Expired=5` |
| `PspExternalChargeId` | `nvarchar(256)` | NULL | PSP charge id |
| `RedirectUrl` | `nvarchar(2048)` | NULL | hosted PSP redirect URL |
| `CreatedAt` | `datetime2` | NN | เวลาสร้าง session |
| `UpdatedAt` | `datetime2` | NN | เวลาแก้ไขล่าสุด |
| `RowVersion` | `rowversion` | NN, ROWVERSION | optimistic concurrency token |
| `AmountAmount` | `decimal(19,4)` | NN | session amount จาก order |
| `AmountCurrency` | `char(3)` | NN | session currency |

Filtered unique index `(OrderId)` จำกัด open session ที่ `Status IN (1, 2)` เหลือไม่เกินหนึ่งรายการต่อ order.

### `txn.PspConnections`

| Field | SQL type | Null | ความหมาย |
|---|---|---|---|
| `Id` | `uniqueidentifier` | NN, PK | PSP connection id |
| `MerchantId` | `uniqueidentifier` | NN | tenant boundary |
| `Psp` | `int` | NN | `TwoCTwoP=1`, `Omise=2` |
| `EnabledMethods` | `nvarchar(256)` | NN | methods ที่ connection รองรับ |
| `SecretRefName` | `nvarchar(128)` | NN | reference ไป vault; ไม่ใช่ secret value |
| `Metadata` | `nvarchar(max)` | NULL | PSP-specific metadata; ไม่ใช่ native JSON contract |
| `IsEnabled` | `bit` | NN | เปิดใช้งานหรือไม่ |
| `CreatedAt` | `datetime2` | NN | เวลาสร้าง |

Unique index `(MerchantId, Psp)` จำกัดหนึ่ง connection ต่อ PSP ต่อ merchant.

## `dbo` schema

### `dbo.DataProtectionKeys`

| Field | SQL type | Null | ความหมาย |
|---|---|---|---|
| `Id` | `int` | NN, PK, IDENTITY `(1,1)` | ASP.NET key id |
| `SecretKey` | `nvarchar(256)` | NULL | key label จาก Data Protection |
| `Xml` | `nvarchar(max)` | NN | serialized key material |

### `dbo.__EFMigrationsHistory`

EF Core สร้างและดูแล table นี้นอก `InitialSchema` migration.

| Field | SQL type | Null | ความหมาย |
|---|---|---|---|
| `MigrationId` | `nvarchar(150)` | NN, PK | migration identifier |
| `ProductVersion` | `nvarchar(32)` | NN | EF Core product version |

## Keys, foreign keys and indexes

### Foreign keys

| Constraint | Child | Parent | Delete behavior |
|---|---|---|---|
| `FK_CartItems_Carts_CartId_MerchantId` | `shop.CartItems (CartId, MerchantId)` | `shop.Carts (Id, MerchantId)` | `CASCADE` |
| `FK_OrderItems_Orders_OrderId_MerchantId` | `shop.OrderItems (OrderId, MerchantId)` | `shop.Orders (Id, MerchantId)` | `CASCADE` |
| `FK_Permissions_PermissionGroups_GroupKey` | `iam.Permissions.GroupKey` | `iam.PermissionGroups.Key` | `RESTRICT` |
| `FK_Users_Divisions_DivisionId` | `admin.Users.DivisionId` | `cfg.Divisions.Id` | `RESTRICT` |
| `FK_Users_Levels_LevelId` | `admin.Users.LevelId` | `cfg.Levels.Id` | `RESTRICT` |
| `FK_Users_Offices_OfficeId` | `admin.Users.OfficeId` | `cfg.Offices.Id` | `RESTRICT` |
| `FK_Users_Positions_PositionId` | `admin.Users.PositionId` | `cfg.Positions.Id` | `RESTRICT` |
| `FK_RoleAssignments_Roles_RoleId` | `admin.RoleAssignments.RoleId` | `iam.Roles.Id` | `RESTRICT` |
| `FK_RoleAssignments_Roles_RoleId` | `merch.RoleAssignments.RoleId` | `iam.Roles.Id` | `RESTRICT` |
| `FK_RegistrationAttempts_Users_UserId` | `merch.RegistrationAttempts.UserId` | `merch.Users.Id` | `RESTRICT` |
| `FK_RolePermissions_Permissions_PermissionKey` | `iam.RolePermissions.PermissionKey` | `iam.Permissions.Key` | `RESTRICT` |
| `FK_RolePermissions_Roles_RoleId` | `iam.RolePermissions.RoleId` | `iam.Roles.Id` | `CASCADE` |

`MerchantId`, `UserId`, `OrderId`, `PaymentSessionId`, `PspConnectionId` และ audit references ที่ไม่มีรายการด้านบนเป็น scalar/application relationships ไม่ใช่ physical FK.

### Unique constraints and indexes

| Table | Name | Columns / filter |
|---|---|---|
| `admin.MerchantAccess` | `IX_MerchantAccess_AdminUserId_MerchantId` | `(AdminUserId, MerchantId)` unique |
| `admin.ProvisioningOperations` | `UX_ProvisioningOperations_Key` | `OperationKey` unique |
| `admin.RoleAssignments` | `IX_RoleAssignments_AdminUserId_RoleId` | `(AdminUserId, RoleId)` unique |
| `admin.Sessions` | `IX_Sessions_TokenHash` | `TokenHash` unique |
| `admin.Users` | `IX_Users_Email` | `Email` unique |
| `admin.Users` | `IX_Users_Subject` | `Subject` unique, filter `Subject IS NOT NULL` |
| `cfg.Divisions` | `IX_Divisions_Code` | `Code` unique |
| `cfg.Levels` | `IX_Levels_Code` | `Code` unique |
| `cfg.Offices` | `IX_Offices_Code` | `Code` unique |
| `cfg.Positions` | `IX_Positions_Code` | `Code` unique |
| `iam.RolePermissions` | `IX_RolePermissions_RoleId_PermissionKey` | `(RoleId, PermissionKey)` unique |
| `iam.Roles` | `IX_Roles_MerchantId_Code` | `(MerchantId, Code)` unique |
| `merch.ExternalLogins` | `IX_ExternalLogins_Provider_Subject` | `(Provider, Subject)` unique |
| `merch.Merchants` | `IX_Merchants_Code` | `Code` unique |
| `merch.RegistrationAttempts` | `IX_RegistrationAttempts_UserId_AttemptNo` | `(UserId, AttemptNo)` unique |
| `merch.RegistrationNotices` | `IX_RegistrationNotices_UserId` | `UserId` unique |
| `merch.RoleAssignments` | `IX_RoleAssignments_UserId_RoleId` | `(UserId, RoleId)` unique |
| `merch.Sessions` | `IX_Sessions_TokenHash` | `TokenHash` unique |
| `merch.VaultRevealAudits` | `IX_VaultRevealAudits_MerchantId_Seq` | `(MerchantId, Seq)` unique |
| `shop.Carts` | `AK_Carts_Id_MerchantId` | `(Id, MerchantId)` alternate key |
| `shop.Orders` | `AK_Orders_Id_MerchantId` | `(Id, MerchantId)` alternate key |
| `shop.Orders` | `IX_Orders_OrderNo` | `OrderNo` unique |
| `shop.Orders` | `IX_Orders_PaymentSessionId` | `PaymentSessionId`, filter non-null |
| `shop.Orders` | `IX_Orders_SummaryToken` | `SummaryToken` unique |
| `txn.PaymentSessions` | `IX_PaymentSessions_OrderId_Open` | `OrderId` unique, filter `Status IN (1, 2)` |
| `txn.PaymentSessions` | `IX_PaymentSessions_Psp_PspExternalChargeId` | `(Psp, PspExternalChargeId)` unique, filter non-null |
| `txn.PspConnections` | `IX_PspConnections_MerchantId_Psp` | `(MerchantId, Psp)` unique |

Non-unique lookup indexes:

| Table | Indexes |
|---|---|
| `admin.AuthAudits` | `IX_AuthAudits_AdminUserId` |
| `admin.RoleAssignments` | `IX_RoleAssignments_RoleId` |
| `admin.Sessions` | `IX_Sessions_AbsoluteExpiresAt`, `IX_Sessions_AdminUserId`, `IX_Sessions_FamilyId` |
| `admin.Users` | `IX_Users_DivisionId`, `IX_Users_LevelId`, `IX_Users_OfficeId`, `IX_Users_PositionId` |
| `iam.Permissions` | `IX_Permissions_GroupKey` |
| `iam.RolePermissions` | `IX_RolePermissions_PermissionKey` |
| `merch.AuthAudits` | `IX_AuthAudits_UserId` |
| `merch.RegistrationAudits` | `IX_RegistrationAudits_TargetSubject` |
| `merch.Sessions` | `IX_Sessions_AbsoluteExpiresAt`, `IX_Sessions_FamilyId`, `IX_Sessions_UserId` |
| `merch.RoleAssignments` | `IX_RoleAssignments_RoleId`, `IX_RoleAssignments_UserId_MerchantId` — role lookup; `(UserId, MerchantId)` lookup |
| `merch.UserOutbox` | `IX_UserOutbox_ProcessedAt_LeaseExpiresAt` |
| `merch.VaultRevealAudits` | `IX_VaultRevealAudits_MerchantId_Id` |
| `shop.CartItems` | `IX_CartItems_CartId_MerchantId` |
| `shop.OrderItemRevealAudits` | `IX_OrderItemRevealAudits_MerchantId_RevealedAt`, `IX_OrderItemRevealAudits_OrderItemId` |
| `shop.OrderItems` | `IX_OrderItems_OrderId_MerchantId`, `IX_OrderItems_ProductCode` including `(OrderId, VariantCode)` |
| `shop.Orders` | `IX_Orders_MerchantId` |
| `txn.OutboxMessages` | `IX_OutboxMessages_ProcessedAt_LeaseExpiresAt` |
| `txn.PaymentSessions` | `IX_PaymentSessions_OrderId` |

## Native JSON and retired surfaces

Native SQL Server `json` columns มี exactly 5 จุด:

| Column | Contract |
|---|---|
| `admin.ProvisioningOperations.Result` | closed provisioning result |
| `merch.Merchants.Metadata` | typed merchant extension |
| `merch.UserOutbox.Payload` | closed registration/KYC lifecycle event |
| `shop.CartItems.Metadata` | typed cart item snapshot |
| `shop.OrderItems.Metadata` | immutable order item snapshot |

`txn.OutboxMessages.Payload` และ `txn.PspConnections.Metadata` เป็น `nvarchar(max)`, ไม่ใช่ native `json`.

ไม่มี persisted/API surface ปัจจุบันสำหรับ `CheckoutSession`, `CheckoutSessionItems`, `CheckoutConfirmed`, `shop.Products`, policy entity, policy audit/report, policy route, SQL RLS หรือ legacy product catalogue persistence. Products อ่านจาก upstream ผ่าน `GET /api/v1/products`.

## Source of truth

1. `src/BuildingBlocks/BuildingBlocks.Infrastructure/Persistence/Migrations/20260807042818_InitialSchema.cs`
2. `src/BuildingBlocks/BuildingBlocks.Infrastructure/Persistence/Migrations/20260807042828_SecurityObjects.cs`
3. `src/BuildingBlocks/BuildingBlocks.Infrastructure/Persistence/Migrations/20260807042833_SeedData.cs`
4. `src/BuildingBlocks/BuildingBlocks.Infrastructure/Persistence/Migrations/20260808161508_OneBasedPersistedEnumStorage.cs`
5. `src/BuildingBlocks/BuildingBlocks.Infrastructure/Persistence/Migrations/PolDbContextModelSnapshot.cs`
6. EF configurations ใต้ `src/Persistence/` และ module infrastructure

เมื่อ schema เปลี่ยน ต้องอัปเดต migration, model snapshot และเอกสารนี้พร้อมกัน.
