# Entity and Field Reference

เอกสารนี้คือ persisted schema ปัจจุบันของ `VCentralPay` ตาม EF model snapshot ล่าสุดใน source. ครอบคลุม 104 physical table mappings, field inventory, key, foreign key และ index ที่ระบบสร้างเอง; ไม่รวมข้อมูลจาก upstream product catalogue.

## Database baseline

| รายการ | ค่าปัจจุบัน |
|---|---|
| Engine | SQL Server 2025 build `17.0.4045.5` ขึ้นไป |
| Compatibility level | `170` |
| Collation | `Thai_100_CI_AS` |
| Migration chain | 47 migrations: `20260807042818_InitialSchema` ถึง `20260911163519_ReviewFixPaymentLinkNotificationIntent`; รายการเต็มอยู่ใน `src/Infrastructure/BuildingBlocks.Infrastructure/Persistence/Migrations/` และ snapshot |
| Runtime principal | `pol_app` |
| Runtime contexts | `ControlPlaneDbContext`, `CommerceDbContext` เท่านั้น |
| Migration context | `PolDbContext` เท่านั้น |
| Tenant isolation | app-layer query filter + guarded write; ไม่มี SQL RLS, `SESSION_CONTEXT` หรือ bypass principal |

สัญลักษณ์ในตาราง field: `PK` = primary key, `FK` = foreign key, `NN` = `NOT NULL`, `NULL` = nullable, `IDENTITY` = database-generated identity, `ROWVERSION` = SQL Server rowversion.

Migration `20260808161508_OneBasedPersistedEnumStorage` (`OneBasedPersistedEnumStorage`) แปลงค่า legacy แบบ 0-based เป็น mapping one-based ตามตารางด้านล่างด้วย `CASE` แบบ explicit
ครบทุก target field. Migration ตรวจ `NULL` และค่า legacy ที่อยู่นอกช่วงก่อนทำ data/schema change; ถ้า
`merch.Users.IdentityType` หรือ `merch.RegistrationAttempts.IdentityType` เป็น `NULL` จะหยุดทันทีโดยไม่ backfill และไม่
เปลี่ยน schema. `Down` ตรวจค่าปัจจุบันก่อนแปลงกลับเช่นเดียวกัน.

## Schema ownership

| Schema | Tables | Runtime owner |
|---|---|---|
| `acct` | business Accounts, LoginAccounts, Employees, Agents, SystemClients, BFF/registration state | `ControlPlaneDbContext` |
| `access` | Merchant/Platform access, roles, branch/method grants | `ControlPlaneDbContext` |
| `oauth` | OpenIddict state และ assertion replay | `ControlPlaneDbContext` |
| `admin` | platform users, sessions, access, role assignments, workforce binding, governance, audit, operation ledger, control delivery | `ControlPlaneDbContext` |
| `iam` | permission groups, permissions, roles, grants, API clients, one-time secret tickets | `ControlPlaneDbContext` |
| `cfg` | payment capability catalog และ migration conflicts | `ControlPlaneDbContext` |
| `merch` | merchants, branches, sales, originators, merchant users, invitations, sessions, registration, user outbox, vault | `ControlPlaneDbContext` |
| `shop` | carts, cart items, orders, order items, reveal audit | `CommerceDbContext` |
| `checkout` | PaymentLinks และ PaymentLinkReplays | `CommerceDbContext` |
| `txn` | split owner: Control Plane owns PSP connections, routing, payment capability mappings and approval execution; Commerce owns payment sessions, inbound webhooks, Transactions/events, notification runtime, admin operation, idempotency and outbox | `ControlPlaneDbContext` + `CommerceDbContext` |
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
| `Email` | `nvarchar(320)` | NN | email contact; unique |
| `Provider` | `nvarchar(64)` | NN | provider discriminator; current workforce provider is Microsoft |
| `TenantId` | `uniqueidentifier` | NULL | workforce tenant binding |
| `EmployeeId` | `nvarchar(128)` | NULL | normalized HR employee id |
| `FirstName` | `nvarchar(200)` | NULL | HR profile snapshot |
| `LastName` | `nvarchar(200)` | NULL | HR profile snapshot |
| `Tier` | `int` | NN | `Scoped=1`, `Super=2` |
| `Status` | `int` | NN | `Active=1`, `Suspended=2` |
| `AuthorizationVersion` | `bigint` | NN | invalidation version |
| `CreatedAt` | `datetime2` | NN | เวลาสร้าง |
| `UpdatedAt` | `datetime2` | NULL | เวลาแก้ไขล่าสุด |
| `Version` | `bigint` | NN | application-managed optimistic concurrency |

### `admin.ApprovalRequests`

| Field | SQL type | Null | ความหมาย |
|---|---|---|---|
| `Id` | `uniqueidentifier` | NN, PK | approval request id |
| `ScopeKind` | `int` | NN | `Platform=1`, `Merchant=2` |
| `MerchantId` | `uniqueidentifier` | NULL | tenant scope; platform scope ต้องเป็น `NULL` |
| `Action` | `varchar(120)` | NN | operation ที่รออนุมัติ |
| `RequiredPermission` | `varchar(120)` | NN | permission ที่ checker ต้องมี |
| `MakerId` | `uniqueidentifier` | NN | ผู้สร้างคำขอ |
| `TargetType` | `varchar(120)` | NN | ประเภท resource |
| `TargetId` | `nvarchar(200)` | NN | resource identifier |
| `TargetVersion` | `varchar(200)` | NN | version ที่ maker อ่านมา |
| `Status` | `int` | NN | pending/approved/rejected/executed state |
| `CheckerId` | `uniqueidentifier` | NULL | ผู้ตัดสินใจ |
| `DecisionReason` | `nvarchar(1000)` | NULL | เหตุผลการตัดสินใจ |
| `DecidedAt` | `datetime2` | NULL | เวลาตัดสินใจ |
| `ExecutionOutcome` | `nvarchar(1000)` | NULL | ผลการ execute |
| `ExecutedAt` | `datetime2` | NULL | เวลา execute |
| `CorrelationId` | `varchar(128)` | NN | request correlation |
| `CreatedAt` | `datetime2` | NN | เวลาสร้าง |
| `Version` | `bigint` | NN | optimistic concurrency |

### `admin.ApprovalEvents`

| Field | SQL type | Null | ความหมาย |
|---|---|---|---|
| `Id` | `uniqueidentifier` | NN, PK | event id |
| `SourceEventId` | `uniqueidentifier` | NN | source event; unique |
| `ApprovalId` | `uniqueidentifier` | NN, FK | อ้าง `admin.ApprovalRequests.Id` |
| `ScopeKind` | `int` | NN | scope ของ approval |
| `MerchantId` | `uniqueidentifier` | NULL | merchant scope |
| `Kind` | `varchar(40)` | NN | approval event kind |
| `ActorId` | `uniqueidentifier` | NULL | actor ถ้ามี |
| `Detail` | `nvarchar(1000)` | NULL | รายละเอียด |
| `CorrelationId` | `varchar(128)` | NN | request correlation |
| `OccurredAt` | `datetime2` | NN | เวลาเกิด event |

### `admin.AuditHeads`

| Field | SQL type | Null | ความหมาย |
|---|---|---|---|
| `ScopeKey` | `varchar(80)` | NN, PK | hash-chain scope key |
| `ScopeKind` | `int` | NN | scope ของ audit chain |
| `MerchantId` | `uniqueidentifier` | NULL | merchant scope |
| `LastSequence` | `bigint` | NN | sequence ล่าสุด |
| `LastHash` | `binary(32)` | NN | hash ล่าสุด |
| `UpdatedAt` | `datetime2` | NN | เวลาแก้ไขล่าสุด |

### `admin.AuditRecords`

| Field | SQL type | Null | ความหมาย |
|---|---|---|---|
| `Id` | `uniqueidentifier` | NN, PK | audit row id |
| `ScopeKey` | `varchar(80)` | NN, FK | อ้าง `admin.AuditHeads.ScopeKey` |
| `ScopeKind` | `int` | NN | scope ของ audit |
| `MerchantId` | `uniqueidentifier` | NULL | merchant scope |
| `Sequence` | `bigint` | NN | sequence ใน chain |
| `ActorId` | `uniqueidentifier` | NN | actor id |
| `Action` | `varchar(120)` | NN | action |
| `ResourceType` | `varchar(120)` | NN | resource type |
| `ResourceId` | `nvarchar(200)` | NN | resource id |
| `Result` | `varchar(80)` | NN | ผลลัพธ์ |
| `Changes` | `nvarchar(max)` | NN | structured change payload |
| `ApprovalId` | `uniqueidentifier` | NULL | approval ที่เกี่ยวข้อง |
| `ResourceVersion` | `varchar(200)` | NULL | resource version |
| `CorrelationId` | `varchar(128)` | NN | request correlation |
| `OccurredAt` | `datetime2` | NN | เวลาเกิด action |
| `PreviousHash` | `binary(32)` | NN | hash ก่อนหน้า |
| `Hash` | `binary(32)` | NN | hash ของ row |

### `admin.GovernanceOutboxMessages`

| Field | SQL type | Null | ความหมาย |
|---|---|---|---|
| `Id` | `uniqueidentifier` | NN, PK | message id |
| `ScopeKind` | `int` | NN | scope ของ governance event |
| `MerchantId` | `uniqueidentifier` | NULL | merchant scope |
| `Type` | `varchar(200)` | NN | event type |
| `SchemaVersion` | `varchar(16)` | NN | event schema version |
| `Payload` | `nvarchar(max)` | NN | serialized event payload |
| `OccurredAt` | `datetime2` | NN | เวลาเกิด event |
| `ProcessedAt` | `datetime2` | NULL | เวลาประมวลผลสำเร็จ |
| `Attempts` | `int` | NN | จำนวนครั้งที่พยายามส่ง |
| `Error` | `nvarchar(1000)` | NULL | error ล่าสุด |
| `LeaseExpiresAt` | `datetime2` | NULL | lease expiry |
| `LeaseOwner` | `nvarchar(200)` | NULL | worker ที่ถือ lease |

### `admin.OperationRecords`

| Field | SQL type | Null | ความหมาย |
|---|---|---|---|
| `Id` | `uniqueidentifier` | NN, PK | operation id |
| `ActorId` | `uniqueidentifier` | NN | actor id |
| `Operation` | `varchar(120)` | NN | operation name |
| `IdempotencyKey` | `varchar(200)` | NN | idempotency key |
| `RequestHash` | `varchar(64)` | NN | request hash |
| `ScopeKind` | `int` | NN | scope ของ operation |
| `MerchantId` | `uniqueidentifier` | NULL | merchant scope |
| `Status` | `int` | NN | operation state |
| `ResponseStatus` | `int` | NULL | HTTP status ที่ cache ไว้ |
| `ResponseBody` | `nvarchar(max)` | NULL | response ที่ cache ไว้ |
| `CreatedAt` | `datetime2` | NN | เวลาสร้าง |
| `ExpiresAt` | `datetime2` | NN | เวลา expiry |
| `CompletedAt` | `datetime2` | NULL | เวลาสำเร็จ/จบ |

### `admin.DeliverySecretVersions`

| Field | SQL type | Null | ความหมาย |
|---|---|---|---|
| `Id` | `uniqueidentifier` | NN, PK | secret version id |
| `OwnerId` | `uniqueidentifier` | NN | owner resource id |
| `MerchantId` | `uniqueidentifier` | NN | tenant boundary |
| `OwnerType` | `nvarchar(64)` | NN | owner discriminator |
| `ProtectedSecret` | `nvarchar(max)` | NN | protected secret; ไม่ใช่ plaintext contract |
| `State` | `int` | NN | pending/active/retired state |
| `CreatedAt` | `datetime2` | NN | เวลาสร้าง |
| `ActivatedAt` | `datetime2` | NULL | เวลา activate |
| `RetiredAt` | `datetime2` | NULL | เวลา retire |

### `admin.WebhookEndpoints`

| Field | SQL type | Null | ความหมาย |
|---|---|---|---|
| `Id` | `uniqueidentifier` | NN, PK | endpoint id |
| `MerchantId` | `uniqueidentifier` | NN | tenant boundary |
| `Name` | `nvarchar(160)` | NN | endpoint name |
| `Url` | `nvarchar(2048)` | NN | destination URL; ตรวจ SSRF ก่อนบันทึก |
| `EventsCsv` | `nvarchar(2000)` | NN | event subscriptions |
| `Enabled` | `bit` | NN | เปิดใช้งานหรือไม่ |
| `ActiveSecretVersionId` | `uniqueidentifier` | NN | active delivery secret |
| `SecretHint` | `nvarchar(32)` | NN | non-secret hint |
| `CreatedAt` | `datetime2` | NN | เวลาสร้าง |
| `UpdatedAt` | `datetime2` | NN | เวลาแก้ไขล่าสุด |
| `Version` | `bigint` | NN | optimistic concurrency |

### `admin.WebhookDeliveries`

| Field | SQL type | Null | ความหมาย |
|---|---|---|---|
| `Id` | `uniqueidentifier` | NN, PK | delivery id |
| `EndpointId` | `uniqueidentifier` | NN | endpoint เป้าหมาย |
| `MerchantId` | `uniqueidentifier` | NN | tenant boundary |
| `SourceEventId` | `uniqueidentifier` | NN | source event id |
| `OriginalDeliveryId` | `uniqueidentifier` | NULL | delivery เดิมเมื่อ replay |
| `ReplayKey` | `nvarchar(200)` | NULL | replay idempotency key |
| `EventType` | `nvarchar(160)` | NN | event type |
| `TransactionId` | `nvarchar(200)` | NULL | transaction reference |
| `Payload` | `nvarchar(max)` | NN | delivery payload |
| `Status` | `int` | NN | delivery state |
| `AttemptCount` | `int` | NN | จำนวน attempts |
| `NextAttemptAt` | `datetime2` | NN | retry schedule |
| `LastAttemptAt` | `datetime2` | NULL | attempt ล่าสุด |
| `LeaseExpiresAt` | `datetime2` | NULL | lease expiry |
| `LeaseOwner` | `nvarchar(200)` | NULL | worker ที่ถือ lease |
| `LatencyMs` | `int` | NULL | latency ล่าสุด |
| `FailureCode` | `nvarchar(120)` | NULL | failure code |
| `CreatedAt` | `datetime2` | NN | เวลาสร้าง |
| `CompletedAt` | `datetime2` | NULL | เวลาสำเร็จ/จบ |

### `admin.NotificationRules`

| Field | SQL type | Null | ความหมาย |
|---|---|---|---|
| `Id` | `uniqueidentifier` | NN, PK | rule id |
| `MerchantId` | `uniqueidentifier` | NN | tenant boundary |
| `EventType` | `nvarchar(160)` | NN | event ที่ trigger |
| `Channel` | `nvarchar(32)` | NN | notification channel |
| `Destination` | `nvarchar(2048)` | NN | destination |
| `Threshold` | `nvarchar(200)` | NULL | optional threshold |
| `Enabled` | `bit` | NN | เปิดใช้งานหรือไม่ |
| `CreatedAt` | `datetime2` | NN | เวลาสร้าง |
| `UpdatedAt` | `datetime2` | NN | เวลาแก้ไขล่าสุด |
| `Version` | `bigint` | NN | optimistic concurrency |

### `admin.NotificationDeliveries`

| Field | SQL type | Null | ความหมาย |
|---|---|---|---|
| `Id` | `uniqueidentifier` | NN, PK | delivery id |
| `RuleId` | `uniqueidentifier` | NN | rule เป้าหมาย |
| `MerchantId` | `uniqueidentifier` | NN | tenant boundary |
| `SourceEventId` | `uniqueidentifier` | NN | source event id |
| `EventType` | `nvarchar(160)` | NN | event type |
| `Channel` | `nvarchar(32)` | NN | channel |
| `DestinationMasked` | `nvarchar(256)` | NN | masked destination |
| `Status` | `int` | NN | delivery state |
| `FailureCode` | `nvarchar(120)` | NULL | failure code |
| `SentAt` | `datetime2` | NN | เวลาส่ง |

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
| `Version` | `bigint` | NN | optimistic concurrency |

Check constraint: `CK_Roles_ScopeMerchant` บังคับ `Scope=1` ต้องมี `MerchantId IS NULL`; `Scope=2` ต้องเป็น merchant role.

### `iam.ApiClients`

| Field | SQL type | Null | ความหมาย |
|---|---|---|---|
| `Id` | `uniqueidentifier` | NN, PK | API client id |
| `PublicClientId` | `nvarchar(80)` | NN | public client identifier; unique |
| `Name` | `nvarchar(160)` | NN | client name |
| `MerchantId` | `uniqueidentifier` | NN | tenant boundary |
| `OriginatorId` | `uniqueidentifier` | NULL | optional originator binding |
| `ScopesCsv` | `nvarchar(1000)` | NN | granted scopes |
| `IpPolicy` | `nvarchar(2000)` | NULL | optional IP policy |
| `SecretHash` | `varbinary(32)` | NN | hash ของ client secret |
| `SecretHint` | `nvarchar(32)` | NN | non-secret hint |
| `Status` | `int` | NN | client state |
| `PendingRotationApprovalId` | `uniqueidentifier` | NULL | pending rotation approval |
| `PendingRotationTicketId` | `uniqueidentifier` | NULL | pending one-time secret ticket |
| `LastUsedAt` | `datetime2` | NULL | เวลาใช้งานล่าสุด |
| `CreatedAt` | `datetime2` | NN | เวลาสร้าง |
| `UpdatedAt` | `datetime2` | NN | เวลาแก้ไขล่าสุด |
| `Version` | `bigint` | NN | optimistic concurrency |

### `iam.OneTimeSecretTickets`

| Field | SQL type | Null | ความหมาย |
|---|---|---|---|
| `Id` | `uniqueidentifier` | NN, PK | ticket id |
| `ApiClientId` | `uniqueidentifier` | NN | API client เจ้าของ ticket |
| `ApprovalId` | `uniqueidentifier` | NULL | approval ที่เกี่ยวข้อง |
| `TicketHash` | `varbinary(32)` | NN | hash ของ one-time ticket |
| `ProtectedSecret` | `nvarchar(max)` | NULL | protected secret ที่รอ reveal |
| `Status` | `int` | NN | ticket state |
| `ExpiresAt` | `datetime2` | NN | ticket expiry |
| `ConsumedAt` | `datetime2` | NULL | เวลาใช้ ticket |
| `CreatedAt` | `datetime2` | NN | เวลาสร้าง |
| `Version` | `bigint` | NN | optimistic concurrency |

## `cfg` schema

org reference tables (`cfg.Positions`/`cfg.Offices`/`cfg.Levels`/`cfg.Divisions`) ถูกลบเมื่อ 2026-09-05 —
ข้อมูลองค์กรของพนักงานอ่านตรงจาก HR mirror (`dbo.VibEmp`, `dbo.branch`). schema `cfg` ยังใช้อยู่สำหรับ
payment capability catalog.

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
| `Version` | `bigint` | NN | optimistic concurrency |

### `merch.MerchantUserInvitations`

| Field | SQL type | Null | ความหมาย |
|---|---|---|---|
| `Id` | `uniqueidentifier` | NN, PK | invitation id |
| `MerchantId` | `uniqueidentifier` | NN | tenant boundary |
| `Email` | `nvarchar(320)` | NN | invited email |
| `NormalizedEmail` | `nvarchar(320)` | NN | normalized email |
| `TokenHash` | `varchar(64)` | NN | hash ของ invitation token |
| `ExpiresAt` | `datetime2` | NN | invitation expiry |
| `AcceptedAt` | `datetime2` | NULL | เวลารับ invitation |
| `AcceptedByUserId` | `uniqueidentifier` | NULL | user ที่รับ invitation |
| `RevokedAt` | `datetime2` | NULL | เวลายกเลิก |
| `CreatedByUserId` | `uniqueidentifier` | NN | actor เดิมจาก merchant-user plane |
| `CreatedAt` | `datetime2` | NN | เวลาสร้าง |
| `RowVersion` | `rowversion` | NN, ROWVERSION | optimistic concurrency |
| `CreatedByAudience` | `nvarchar(32)` | NN | `MerchantUser` หรือ `Admin` |
| `IntendedRoleCodesJson` | `nvarchar(2000)` | NN | role codes ที่ตั้งใจมอบ |

### `merch.MerchantUserManagementAudits`

| Field | SQL type | Null | ความหมาย |
|---|---|---|---|
| `Id` | `uniqueidentifier` | NN, PK | audit row id |
| `MerchantId` | `uniqueidentifier` | NN | tenant boundary |
| `ActorUserId` | `uniqueidentifier` | NULL | merchant user actor ถ้ามี |
| `TargetUserId` | `uniqueidentifier` | NULL | target user ถ้ามี |
| `InvitationId` | `uniqueidentifier` | NULL | target invitation ถ้ามี |
| `Action` | `varchar(32)` | NN | management action |
| `CorrelationId` | `nvarchar(128)` | NN | request correlation |
| `OccurredAt` | `datetime2` | NN | เวลาเกิด action |

อย่างน้อยหนึ่งใน `TargetUserId` หรือ `InvitationId` ต้องไม่เป็น `NULL`.

### `merch.AdminUserOperationRecords`

| Field | SQL type | Null | ความหมาย |
|---|---|---|---|
| `Id` | `uniqueidentifier` | NN, PK | operation id |
| `MerchantId` | `uniqueidentifier` | NULL | merchant scope; platform operation อาจเป็น `NULL` |
| `ActorId` | `uniqueidentifier` | NN | admin actor |
| `Operation` | `nvarchar(120)` | NN | operation name |
| `IdempotencyKey` | `nvarchar(200)` | NN | idempotency key |
| `IntentHash` | `varchar(64)` | NN | request intent hash |
| `Result` | `nvarchar(max)` | NN | cached result |
| `HttpStatus` | `int` | NN | cached HTTP status |
| `CreatedAt` | `datetime2` | NN | เวลาสร้าง |
| `ExpiresAt` | `datetime2` | NN | เวลา expiry |

### `merch.Originators`

| Field | SQL type | Null | ความหมาย |
|---|---|---|---|
| `Id` | `uniqueidentifier` | NN, PK | originator id |
| `MerchantId` | `uniqueidentifier` | NN, FK | อ้าง `merch.Merchants.Id` |
| `Code` | `nvarchar(64)` | NN | immutable originator code |
| `Name` | `nvarchar(200)` | NN | display name |
| `Type` | `int` | NN | `branch`, `agent`, `broker`, `staff`, `app` domain value |
| `SaleCode` | `nvarchar(100)` | NULL | optional sale code |
| `ApiClientId` | `uniqueidentifier` | NULL | optional API client binding |
| `Status` | `int` | NN | enabled/disabled state |
| `CreatedAt` | `datetime2` | NN | เวลาสร้าง |
| `UpdatedAt` | `datetime2` | NN | เวลาแก้ไขล่าสุด |
| `Version` | `bigint` | NN | optimistic concurrency |

### `merch.VaultSecretVersions`

| Field | SQL type | Null | ความหมาย |
|---|---|---|---|
| `Id` | `uniqueidentifier` | NN, PK | secret version id |
| `MerchantId` | `uniqueidentifier` | NN | tenant boundary |
| `SecretName` | `nvarchar(128)` | NN | secret name |
| `Version` | `int` | NN | version number ต่อ secret |
| `SecretKey` | `nvarchar(64)` | NN | key reference |
| `EncryptedDek` | `varbinary(max)` | NN | encrypted data-encryption key |
| `EncryptedSecret` | `varbinary(max)` | NN | encrypted credential payload |
| `Hint` | `nvarchar(512)` | NN | non-secret hint |
| `State` | `int` | NN | pending/active/retired state |
| `CreatedAt` | `datetime2` | NN | เวลาสร้าง |
| `ExpiresAt` | `datetime2` | NULL | เวลา expiry |
| `ActivatedAt` | `datetime2` | NULL | เวลา activate |
| `RetiredAt` | `datetime2` | NULL | เวลา retire |

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
| `Version` | `bigint` | NN | optimistic concurrency |

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
| `OriginatorId` | `uniqueidentifier` | NULL | originator ที่สร้าง cart |
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
| `PaymentSessionId` | `uniqueidentifier` | NULL | compatibility PaymentSession reference |
| `SuccessfulTransactionId` | `uniqueidentifier` | NULL | first verified canonical Transaction success |
| `PaymentStatus` | `int` | NN | `Unpaid=1`, `Processing=2`, `Paid=3` |
| `Status` | `int` | NN | `Pending=1`, `Paid=2`, `Failed=3`, `Expired=4`, `Refunded=5`, `Cancelled=6`, `Draft=7`, `Open=8` |
| `BusinessType` | `nvarchar(64)` | NULL | canonical business source type |
| `CreatedByAccountId` | `uniqueidentifier` | NULL | verified business Account creator |
| `OwnerSaleId` | `uniqueidentifier` | NULL | trusted Sale owner snapshot |
| `OwnerBranchIdAtCreation` | `uniqueidentifier` | NULL | trusted Branch owner snapshot |
| `InitiatingAudience` | `int` | NULL | `User=1`, `PlatformAdmin=2` compatibility origin |
| `InitiatingMerchantUserId` | `uniqueidentifier` | NULL | merchant-user actor binding |
| `IsFrozen` | `bit` | NN | Draft changes until issue; issued Order frozen |
| `IssuedAt`, `FrozenAt` | `datetime2` | NULL | issue/freeze timestamps |
| `CreatedAt` | `datetime2` | NN | เวลาสร้าง order |
| `PaidAt` | `datetime2` | NULL | เวลายืนยันจ่ายสำเร็จ |
| `SummaryToken` | `nvarchar(64)` | NN | customer summary token; unique |
| `SummaryTokenExpiresAt` | `datetime2` | NN | token expiry |
| `NotificationRecipient` | `nvarchar(320)` | NULL | compatibility recipient |
| `NotifyOnIssue` | `bit` | NN | canonical issue notification intent |
| `NotificationEmail` | `nvarchar(320)` | NULL | canonical email intent |
| `NotificationPhoneNumber` | `varchar(32)` | NULL | canonical SMS intent |
| `PaymentChannel` | `varchar(20)` | NULL | payment channel snapshot |
| `OriginatorId` | `uniqueidentifier` | NULL | originator ที่สร้าง order |
| `UpdatedAt` | `datetime2` | NN | เวลาแก้ไขล่าสุด; default ใหม่ `SYSUTCDATETIME()`, legacy rows backfill จาก `CreatedAt` |
| `Version` | `bigint` | NN | optimistic concurrency |
| `CustomerName` | `nvarchar(200)` | NN | customer PII |
| `CustomerPhone` | `varchar(20)` | NN | customer PII |
| `CustomerEmail` | `nvarchar(320)` | NULL | customer PII |
| `AmountAmount`, `SubtotalAmount`, `OrderDiscountAmount`, `OrderChargeAmount` | `decimal(19,4)` | NN | Money complex values |
| `AmountCurrency`, `SubtotalCurrency`, `OrderDiscountCurrency`, `OrderChargeCurrency` | `char(3)` | NN | Money currency codes |

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
| `RequestMetadata` | `nvarchar(max)` | NULL | canonical versioned client metadata envelope |
| `DiscountAmount` | `decimal(19,4)` | NN | discount amount; ปัจจุบันสร้างเป็นศูนย์ |
| `DiscountCurrency` | `char(3)` | NN | discount currency |
| `TaxAmount` | `decimal(19,4)` | NN | trusted tax component |
| `TaxCurrency` | `char(3)` | NN | tax currency |
| `LineAmount` | `decimal(19,4)` | NN | trusted frozen line amount |
| `LineCurrency` | `char(3)` | NN | line currency |
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

`txn` เป็น schema ร่วมของสอง runtime contexts ไม่ใช่ ownership boundary เดียว: `PspConnections`, `RoutingRulesets`, `RoutingRules`, payment capability mapping tables และ `ApprovalExecutionRecords` อยู่ `ControlPlaneDbContext`; `PaymentSessions`, `Transactions`, `TransactionEvents`, `InboundWebhookEvents`, `IdempotencyRecords`, `AdminOperationRecords`, `OutboxMessages` และ notification runtime อยู่ `CommerceDbContext`.

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
| `PspConnectionId` | `uniqueidentifier` | NULL | provider routing snapshot |
| `SecretVersionId` | `uniqueidentifier` | NULL | pinned vault credential version |
| `PspEnvironment` | `int` | NULL | pinned sandbox/live environment |
| `RoutingSnapshotVersion` | `tinyint` | NN | `1` for new server-routed sessions; `0` legacy rows |
| `Status` | `int` | NN | `Created=1`, `Redirected=2`, `Paid=3`, `Failed=4`, `Expired=5` |
| `PspExternalChargeId` | `nvarchar(256)` | NULL | PSP charge id |
| `RedirectUrl` | `nvarchar(2048)` | NULL | hosted PSP redirect URL |
| `CreatedAt` | `datetime2` | NN | เวลาสร้าง session |
| `UpdatedAt` | `datetime2` | NN | เวลาแก้ไขล่าสุด |
| `RowVersion` | `rowversion` | NN, ROWVERSION | optimistic concurrency token |
| `AmountAmount` | `decimal(19,4)` | NN | session amount จาก order |
| `AmountCurrency` | `char(3)` | NN | session currency |
| `Version` | `bigint` | NN | application-managed optimistic concurrency |

Filtered unique index `(OrderId)` จำกัด open session ที่ `Status IN (1, 2)` เหลือไม่เกินหนึ่งรายการต่อ order.

การยืนยัน compatibility ต้อง fetch PSP นอก database transaction เมื่อมี `PspExternalChargeId` แล้วจึง lock/reload `Session` และตรวจ reference/state ก่อนบันทึก claim, transition และ outbox ใน transaction เดียว.

การหมดอายุแบบ offline ตาม TTL ทำได้เฉพาะ `Created` ที่ไม่มี charge reference และ state ไม่เปลี่ยนระหว่าง prepare/apply; session ที่มี charge reference ต้อง fetch-confirm ก่อนตัดสิน. `Redirected` ที่ไม่มี charge reference ถือเป็น `Pending` สำหรับ reconciliation และห้าม mint replacement จาก TTL.

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
| `ActiveSecretVersionId` | `uniqueidentifier` | NULL | active vault secret version |
| `Health` | `int` | NN | connection health state |
| `LastTestResult` | `nvarchar(500)` | NULL | ผลการ test ล่าสุด |
| `LastTestedAt` | `datetime2` | NULL | เวลา test ล่าสุด |
| `PendingApprovalId` | `uniqueidentifier` | NULL | pending credential/config approval |
| `PendingSecretVersionId` | `uniqueidentifier` | NULL | secret version ที่รอ activate |
| `Version` | `bigint` | NN | optimistic concurrency |

Unique index `(MerchantId, Psp)` จำกัดหนึ่ง connection ต่อ PSP ต่อ merchant.

### `txn.AdminOperationRecords`

| Field | SQL type | Null | ความหมาย |
|---|---|---|---|
| `Id` | `uniqueidentifier` | NN, PK | operation id |
| `MerchantId` | `uniqueidentifier` | NN | tenant boundary |
| `ActorId` | `uniqueidentifier` | NN | admin actor |
| `Operation` | `nvarchar(120)` | NN | operation name |
| `IdempotencyKey` | `nvarchar(200)` | NN | idempotency key |
| `IntentHash` | `nvarchar(64)` | NN | request intent hash |
| `State` | `int` | NN | operation state |
| `HttpStatus` | `int` | NULL | cached HTTP status |
| `Result` | `nvarchar(max)` | NULL | cached result |
| `ResourceId` | `nvarchar(200)` | NULL | affected resource id |
| `CreatedAt` | `datetime2` | NN | เวลาสร้าง |
| `ExpiresAt` | `datetime2` | NN | เวลา expiry |

### `txn.RoutingRulesets`

| Field | SQL type | Null | ความหมาย |
|---|---|---|---|
| `Id` | `uniqueidentifier` | NN, PK | ruleset id |
| `MerchantId` | `uniqueidentifier` | NN | tenant boundary |
| `Name` | `nvarchar(200)` | NN | ruleset name |
| `Status` | `int` | NN | draft/active/inactive state |
| `ApprovalId` | `uniqueidentifier` | NULL | activation approval |
| `CreatedAt` | `datetime2` | NN | เวลาสร้าง |
| `UpdatedAt` | `datetime2` | NN | เวลาแก้ไขล่าสุด |
| `Version` | `bigint` | NN | optimistic concurrency |

### `txn.RoutingRules`

| Field | SQL type | Null | ความหมาย |
|---|---|---|---|
| `Id` | `uniqueidentifier` | NN, PK | rule id |
| `MerchantId` | `uniqueidentifier` | NN | tenant boundary |
| `RulesetId` | `uniqueidentifier` | NN, FK | อ้าง `txn.RoutingRulesets` แบบ composite tenant FK |
| `Priority` | `int` | NN | ลำดับ rule |
| `Method` | `nvarchar(30)` | NN | payment method |
| `OriginatorId` | `uniqueidentifier` | NULL, FK | อ้าง `merch.Originators` แบบ composite tenant FK |
| `MinAmount` | `decimal(18,2)` | NULL | lower amount bound |
| `MaxAmount` | `decimal(18,2)` | NULL | upper amount bound |
| `TargetConnectionId` | `uniqueidentifier` | NN, FK | target PSP connection |
| `FallbackConnectionId` | `uniqueidentifier` | NULL, FK | fallback PSP connection |
| `Enabled` | `bit` | NN | เปิดใช้งานหรือไม่ |

### `txn.InboundWebhookEvents`

| Field | SQL type | Null | ความหมาย |
|---|---|---|---|
| `Id` | `uniqueidentifier` | NN, PK | inbound event id |
| `PspConnectionId` | `uniqueidentifier` | NN, FK | อ้าง `txn.PspConnections` แบบ composite tenant FK |
| `MerchantId` | `uniqueidentifier` | NN | tenant boundary |
| `PaymentSessionId` | `uniqueidentifier` | NULL | matched payment session |
| `OrderId` | `uniqueidentifier` | NULL | matched order |
| `PspCode` | `varchar(32)` | NN | PSP code |
| `ExternalEventId` | `nvarchar(256)` | NN | PSP event id |
| `PayloadFingerprint` | `varchar(64)` | NN | fingerprint ของ payload |
| `SignatureValid` | `bit` | NN | signature validation result |
| `Status` | `int` | NN | received/processed/failed state |
| `FailureCode` | `varchar(64)` | NULL | failure code |
| `ReceivedAt` | `datetime2` | NN | เวลารับ event |
| `ProcessedAt` | `datetime2` | NULL | เวลาประมวลผล |
| `Version` | `bigint` | NN | optimistic concurrency |

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

+## Current snapshot additions

หัวข้อต่อไปนี้เพิ่มจากเอกสารรุ่นก่อนและอ่านจาก `PolDbContextModelSnapshot.cs` ล่าสุด. ชนิดในรายการเป็น CLR mapping ที่ EF snapshot ประกาศ; nullability, length, keys และ indexes ให้ดู snapshot/configuration ที่อ้างใน Source of truth.

### `access.AccessRoles`

**Entity**: `Access.Domain.AccessRole`
**Owner**: `ControlPlaneDbContext`
**Fields**: `Id` (`Guid`), `MerchantAccessId` (`Guid`), `MerchantId` (`Guid`), `RoleId` (`Guid`).

### `access.BranchAccess`

**Entity**: `Access.Domain.BranchAccess`
**Owner**: `ControlPlaneDbContext`
**Fields**: `Id` (`Guid`), `BranchId` (`Guid`), `MerchantAccessId` (`Guid`), `MerchantId` (`Guid`).

### `access.MerchantAccess`

**Entity**: `Access.Domain.MerchantAccess`
**Owner**: `ControlPlaneDbContext`
**Fields**: `Id` (`Guid`), `AccountId` (`Guid`), `DataScope` (`int`), `MerchantId` (`Guid`), `Status` (`int`), `Version` (`long`).

### `access.MerchantAccessMethods`

**Entity**: `Access.Domain.MerchantAccessMethod`
**Owner**: `ControlPlaneDbContext`
**Fields**: `Id` (`Guid`), `MerchantAccessId` (`Guid`), `MethodCode` (`string`).

### `access.PlatformAccess`

**Entity**: `Access.Domain.PlatformAccess`
**Owner**: `ControlPlaneDbContext`
**Fields**: `Id` (`Guid`), `EmployeeAccountId` (`Guid`), `Status` (`int`), `Version` (`long`).

### `access.PlatformAccessRoles`

**Entity**: `Access.Domain.PlatformAccessRole`
**Owner**: `ControlPlaneDbContext`
**Fields**: `Id` (`Guid`), `PlatformAccessId` (`Guid`), `RoleId` (`Guid`), `RoleScope` (`int`).

### `access.SystemClientScopes`

**Entity**: `Access.Domain.SystemClientScope`
**Owner**: `ControlPlaneDbContext`
**Fields**: `Id` (`Guid`), `ScopeCode` (`string`), `SystemClientId` (`Guid`).

### `acct.Accounts`

**Entity**: `Accounts.Domain.Account`
**Owner**: `ControlPlaneDbContext`
**Fields**: `Id` (`Guid`), `AccountType` (`int`), `AuthorizationVersion` (`long`), `CreatedAt` (`DateTime`), `DisplayName` (`string`), `Status` (`int`), `UpdatedAt` (`DateTime`).

### `acct.Agents`

**Entity**: `Accounts.Domain.Agent`
**Owner**: `ControlPlaneDbContext`
**Fields**: `AccountId` (`Guid`), `Id` (`Guid`), `MerchantId` (`Guid`), `Metadata` (`string`), `SaleId` (`Guid`).

### `acct.AgentRegistrations`

**Entity**: `Accounts.Domain.AgentRegistration`
**Owner**: `ControlPlaneDbContext`
**Fields**: `Id` (`Guid`), `CreatedAt` (`DateTime`), `CurrentAttemptId` (`Guid?`), `CurrentAttemptNo` (`int`), `Email` (`string`), `ExternalUserId` (`string`), `MerchantId` (`Guid`), `PhoneNumber` (`string`), `ProfileJson` (`string`), `Provider` (`string`), `SaleCode` (`string`), `Status` (`int`), `TenantId` (`string`), `UpdatedAt` (`DateTime`), `Version` (`long`).

### `acct.AgentRegistrationAttempts`

**Entity**: `Accounts.Domain.AgentRegistrationAttempt`
**Owner**: `ControlPlaneDbContext`
**Fields**: `Id` (`Guid`), `AttemptNo` (`int`), `BranchId` (`Guid`), `BranchVersion` (`long`), `ContactEvidenceReference` (`string`), `ContactVerifiedAt` (`DateTime?`), `ContactVerifiedByAccountId` (`Guid?`), `DecidedAt` (`DateTime?`), `DecidedByAccountId` (`Guid?`), `DecisionIdempotencyKey` (`string`), `DecisionIntentHash` (`string`), `Email` (`string`), `ExternalUserId` (`string`), `IdempotencyKey` (`string`), `IntentHash` (`string`), `InternalReviewNote` (`string`), `MerchantId` (`Guid`), `PhoneNumber` (`string`), `ProfileJson` (`string`), `Provider` (`string`), `RegistrationId` (`Guid`), `RejectionReason` (`string`), `SaleCode` (`string`), `SaleId` (`Guid`), `SaleVersion` (`long`), `Status` (`int`), `SubmittedAt` (`DateTime`), `TenantId` (`string`), `Version` (`long`).

### `oauth.AssertionReplays`

**Entity**: `Accounts.Domain.AssertionReplay`
**Owner**: `ControlPlaneDbContext`
**Fields**: `Id` (`Guid`), `ApplicationId` (`string`), `ConsumedAt` (`DateTime`), `ExpiresAt` (`DateTime`), `Jti` (`string`).

### `acct.BffSessionTickets`

**Entity**: `Accounts.Domain.BffSessionTicket`
**Owner**: `ControlPlaneDbContext`
**Fields**: `Id` (`Guid`), `AccountId` (`Guid`), `AuthorizationVersion` (`long`), `ClientId` (`string`), `ExpiresAt` (`DateTime`), `IssuedAt` (`DateTime`), `ProtectedAuthenticationTicket` (`string`), `RevokedAt` (`DateTime?`), `TicketKeyHash` (`byte[]`).

### `acct.ClientKeyPolicies`

**Entity**: `Accounts.Domain.ClientKeyPolicy`
**Owner**: `ControlPlaneDbContext`
**Fields**: `Id` (`Guid`), `Algorithm` (`string`), `ApplicationId` (`string`), `AuditReference` (`string`), `KeyId` (`string`), `Status` (`int`), `SystemClientId` (`Guid`), `ValidFrom` (`DateTime`), `ValidUntil` (`DateTime?`).

### `acct.Employees`

**Entity**: `Accounts.Domain.Employee`
**Owner**: `ControlPlaneDbContext`
**Fields**: `AccountId` (`Guid`), `DepartmentCode` (`string`), `EmployeeCode` (`string`), `Id` (`Guid`), `Metadata` (`string`).

### `acct.LoginAccounts`

**Entity**: `Accounts.Domain.LoginAccount`
**Owner**: `ControlPlaneDbContext`
**Fields**: `Id` (`Guid`), `AccountId` (`Guid`), `DisplayName` (`string`), `Email` (`string`), `ExternalUserId` (`string`), `LastLoginAt` (`DateTime?`), `Provider` (`string`), `TenantId` (`string`).

### `acct.RegistrationSessions`

**Entity**: `Accounts.Domain.RegistrationSession`
**Owner**: `ControlPlaneDbContext`
**Fields**: `Id` (`Guid`), `ExpiresAt` (`DateTime`), `ExternalUserId` (`string`), `IssuedAt` (`DateTime`), `MerchantId` (`Guid`), `Provider` (`string`), `SessionReferenceHash` (`byte[]`), `Status` (`int`), `TenantId` (`string`).

### `acct.SystemClients`

**Entity**: `Accounts.Domain.SystemClient`
**Owner**: `ControlPlaneDbContext`
**Fields**: `Id` (`Guid`), `AccountId` (`Guid`), `AllowedGrantTypes` (`string`), `ClientId` (`string`), `CreatedAt` (`DateTime`), `Environment` (`string`), `MerchantId` (`Guid`), `Status` (`int`), `UpdatedAt` (`DateTime`).

### `admin.WorkforceTenantBindings`

**Entity**: `Admins.Domain.Users.WorkforceTenantBinding`
**Owner**: `ControlPlaneDbContext`
**Fields**: `Id` (`byte`), `TenantId` (`Guid`).

### `checkout.PaymentLinks`

**Entity**: `Checkouts.Domain.PaymentLink`
**Owner**: `CommerceDbContext`
**Fields**: `Id` (`Guid`), `CreatedAt` (`DateTime`), `ExpiresAt` (`DateTime`), `MerchantId` (`Guid`), `OrderId` (`Guid`), `RevokedAt` (`DateTime?`), `RotatedFromLinkId` (`Guid?`), `Status` (`int`), `TokenHash` (`byte[]`), `Version` (`long`).

### `checkout.PaymentLinkReplays`

**Entity**: `Checkouts.Domain.PaymentLinkReplay`
**Owner**: `CommerceDbContext`
**Fields**: `Id` (`Guid`), `CreatedAt` (`DateTime`), `ExpiresAt` (`DateTime`), `IdempotencyKey` (`string`), `LinkId` (`Guid?`), `MerchantId` (`Guid`), `Operation` (`string`), `OrderId` (`Guid`), `ProtectedRawToken` (`string`), `RequestHash` (`byte[]`).

### `merch.Branches`

**Entity**: `Merchants.Domain.Branch`
**Owner**: `ControlPlaneDbContext`
**Fields**: `Id` (`Guid`), `Code` (`string`), `CreatedAt` (`DateTime`), `MerchantId` (`Guid`), `Name` (`string`), `Status` (`int`), `UpdatedAt` (`DateTime`), `Version` (`long`).

### `merch.Sales`

**Entity**: `Merchants.Domain.Sale`
**Owner**: `ControlPlaneDbContext`
**Fields**: `Id` (`Guid`), `BranchId` (`Guid`), `Code` (`string`), `CreatedAt` (`DateTime`), `MerchantId` (`Guid`), `Name` (`string`), `Status` (`int`), `UpdatedAt` (`DateTime`), `Version` (`long`).

### `txn.Deliveries`

**Entity**: `Notifications.Domain.Delivery`
**Owner**: `CommerceDbContext`
**Fields**: `Id` (`Guid`), `AttemptCount` (`int`), `Channel` (`string`), `CompletedAt` (`DateTime?`), `EndpointUrlSnapshot` (`string`), `FailureCode` (`string`), `LastAttemptAt` (`DateTime?`), `LeaseExpiresAt` (`DateTime?`), `LeaseOwner` (`string`), `MerchantId` (`Guid`), `NextAttemptAt` (`DateTime`), `NotificationId` (`Guid`), `PayloadSnapshot` (`string`), `ProtectedEndpointSecretSnapshot` (`string`), `ProviderMessageId` (`string`), `RecipientFingerprint` (`string`), `RecipientSnapshot` (`string`), `SourceEventId` (`Guid`), `Status` (`int`), `TemplateContentSnapshot` (`string`), `TemplateLocale` (`string`), `TemplateSubjectSnapshot` (`string`), `TemplateVersion` (`string`), `TemplateVersionId` (`Guid`).

### `txn.DeliveryAttempts`

**Entity**: `Notifications.Domain.DeliveryAttempt`
**Owner**: `CommerceDbContext`
**Fields**: `Id` (`Guid`), `AttemptNo` (`int`), `CompletedAt` (`DateTime`), `DeliveryId` (`Guid`), `FailureCode` (`string`), `LatencyMs` (`int?`), `MerchantId` (`Guid`), `Outcome` (`string`), `ProviderMessageId` (`string`), `StartedAt` (`DateTime`).

### `txn.Notifications`

**Entity**: `Notifications.Domain.Notification`
**Owner**: `CommerceDbContext`
**Fields**: `Id` (`Guid`), `CorrelationId` (`string`), `CreatedAt` (`DateTime`), `EventType` (`string`), `MerchantId` (`Guid`), `OccurredAt` (`DateTime`), `OrderId` (`Guid?`), `OrderNo` (`string`), `PayloadSnapshot` (`string`), `RegistrationAttemptId` (`Guid?`), `RegistrationId` (`Guid?`), `SourceEventId` (`Guid`), `TransactionId` (`Guid?`), `TransactionNo` (`string`).

### `txn.NotificationInboxMessages`

**Entity**: `Notifications.Domain.NotificationInboxMessage`
**Owner**: `CommerceDbContext`
**Fields**: `Id` (`Guid`), `EventType` (`string`), `MerchantId` (`Guid`), `PayloadSnapshot` (`string`), `ProcessedAt` (`DateTime?`), `ReceivedAt` (`DateTime`), `SourceEventId` (`Guid`).

### `txn.NotificationReviewNotes`

**Entity**: `Notifications.Domain.NotificationReviewNote`
**Owner**: `CommerceDbContext`
**Fields**: `Id` (`Guid`), `ActorId` (`Guid`), `CorrelationId` (`string`), `CreatedAt` (`DateTime`), `DeliveryId` (`Guid?`), `MerchantId` (`Guid`), `Note` (`string`), `NotificationId` (`Guid?`).

### `txn.TemplateVersions`

**Entity**: `Notifications.Domain.TemplateVersion`
**Owner**: `CommerceDbContext`
**Fields**: `Id` (`Guid`), `Channel` (`string`), `Content` (`string`), `EventType` (`string`), `Locale` (`string`), `ReleasedAt` (`DateTime`), `Subject` (`string`), `Version` (`string`).

### `oauth.OpenIddictApplications`

**Entity**: `OpenIddict.EntityFrameworkCore.Models.OpenIddictEntityFrameworkCoreApplication`
**Owner**: `ControlPlaneDbContext`
**Fields**: `Id` (`string`), `ApplicationType` (`string`), `ClientId` (`string`), `ClientSecret` (`string`), `ClientType` (`string`), `ConcurrencyToken` (`string`), `ConsentType` (`string`), `DisplayName` (`string`), `DisplayNames` (`string`), `JsonWebKeySet` (`string`), `Permissions` (`string`), `PostLogoutRedirectUris` (`string`), `Properties` (`string`), `RedirectUris` (`string`), `Requirements` (`string`), `Settings` (`string`).

### `oauth.OpenIddictAuthorizations`

**Entity**: `OpenIddict.EntityFrameworkCore.Models.OpenIddictEntityFrameworkCoreAuthorization`
**Owner**: `ControlPlaneDbContext`
**Fields**: `Id` (`string`), `ApplicationId` (`string`), `ConcurrencyToken` (`string`), `CreationDate` (`DateTime?`), `Properties` (`string`), `Scopes` (`string`), `Status` (`string`), `Subject` (`string`), `Type` (`string`).

### `oauth.OpenIddictScopes`

**Entity**: `OpenIddict.EntityFrameworkCore.Models.OpenIddictEntityFrameworkCoreScope`
**Owner**: `ControlPlaneDbContext`
**Fields**: `Id` (`string`), `ConcurrencyToken` (`string`), `Description` (`string`), `Descriptions` (`string`), `DisplayName` (`string`), `DisplayNames` (`string`), `Name` (`string`), `Properties` (`string`), `Resources` (`string`).

### `oauth.OpenIddictTokens`

**Entity**: `OpenIddict.EntityFrameworkCore.Models.OpenIddictEntityFrameworkCoreToken`
**Owner**: `ControlPlaneDbContext`
**Fields**: `Id` (`string`), `ApplicationId` (`string`), `AuthorizationId` (`string`), `ConcurrencyToken` (`string`), `CreationDate` (`DateTime?`), `ExpirationDate` (`DateTime?`), `Payload` (`string`), `Properties` (`string`), `RedemptionDate` (`DateTime?`), `ReferenceId` (`string`), `Status` (`string`), `Subject` (`string`), `Type` (`string`).

### `txn.ApprovalExecutionRecords`

**Entity**: `Payments.Domain.ApprovalExecutionRecord`
**Owner**: `ControlPlaneDbContext`
**Fields**: `EventId` (`Guid`), `ApprovalId` (`Guid`), `CompletedAt` (`DateTime?`), `CreatedAt` (`DateTime`), `Decision` (`string`), `MerchantId` (`Guid`), `Outcome` (`string`), `State` (`int`), `TargetId` (`string`), `TargetType` (`string`).

### `txn.MerchantPaymentMethods`

**Entity**: `Payments.Domain.Capabilities.MerchantPaymentMethod`
**Owner**: `ControlPlaneDbContext`
**Fields**: `Id` (`Guid`), `CreatedAt` (`DateTime`), `CreatedBy` (`Guid`), `IsEnabled` (`bool`), `MerchantId` (`Guid`), `PaymentMethodId` (`Guid`), `UpdatedAt` (`DateTime?`), `UpdatedBy` (`Guid?`), `Version` (`long`).

### `txn.MerchantProviderAccountMethods`

**Entity**: `Payments.Domain.Capabilities.MerchantProviderAccountMethod`
**Owner**: `ControlPlaneDbContext`
**Fields**: `Id` (`Guid`), `CreatedAt` (`DateTime`), `CreatedBy` (`Guid`), `IsEnabled` (`bool`), `MerchantId` (`Guid`), `PaymentMethodId` (`Guid`), `PaymentProviderId` (`Guid`), `PaymentProviderMethodId` (`Guid`), `PspConnectionId` (`Guid`), `UpdatedAt` (`DateTime?`), `UpdatedBy` (`Guid?`), `Version` (`long`).

### `txn.MerchantProviderAccountMethodOptions`

**Entity**: `Payments.Domain.Capabilities.MerchantProviderAccountMethodOption`
**Owner**: `ControlPlaneDbContext`
**Fields**: `Id` (`Guid`), `CreatedAt` (`DateTime`), `CreatedBy` (`Guid`), `IsEnabled` (`bool`), `MerchantId` (`Guid`), `MerchantProviderAccountMethodId` (`Guid`), `PaymentMethodId` (`Guid`), `PaymentMethodOptionId` (`Guid`), `PaymentProviderId` (`Guid`), `PaymentProviderMethodId` (`Guid`), `PaymentProviderMethodOptionId` (`Guid`), `PspConnectionId` (`Guid`), `UpdatedAt` (`DateTime?`), `UpdatedBy` (`Guid?`), `Version` (`long`).

### `txn.MerchantUserPaymentMethods`

**Entity**: `Payments.Domain.Capabilities.MerchantUserPaymentMethod`
**Owner**: `ControlPlaneDbContext`
**Fields**: `Id` (`Guid`), `CreatedAt` (`DateTime`), `CreatedBy` (`Guid`), `IsEnabled` (`bool`), `MerchantId` (`Guid`), `MerchantUserId` (`Guid`), `PaymentMethodId` (`Guid`), `UpdatedAt` (`DateTime?`), `UpdatedBy` (`Guid?`), `Version` (`long`).

### `cfg.PaymentAuthorizationStates`

**Entity**: `Payments.Domain.Capabilities.PaymentAuthorizationState`
**Owner**: `ControlPlaneDbContext`
**Fields**: `Id` (`Guid`), `CutoffAt` (`DateTime?`), `Mode` (`int`), `Version` (`long`).

### `cfg.PaymentCapabilityMigrationConflicts`

**Entity**: `Payments.Domain.Capabilities.PaymentCapabilityMigrationConflict`
**Owner**: `ControlPlaneDbContext`
**Fields**: `Id` (`Guid`), `Detail` (`string`), `DetectedAt` (`DateTime`), `EntityId` (`Guid?`), `Kind` (`string`), `MerchantId` (`Guid?`), `ResolvedAt` (`DateTime?`), `ResolvedBy` (`Guid?`).

### `cfg.PaymentMethods`

**Entity**: `Payments.Domain.Capabilities.PaymentMethod`
**Owner**: `ControlPlaneDbContext`
**Fields**: `Id` (`Guid`), `Code` (`string`), `IsActive` (`bool`), `Name` (`string`), `UpdatedAt` (`DateTime?`), `UpdatedBy` (`Guid?`), `Version` (`long`).

### `cfg.PaymentMethodOptions`

**Entity**: `Payments.Domain.Capabilities.PaymentMethodOption`
**Owner**: `ControlPlaneDbContext`
**Fields**: `Id` (`Guid`), `Code` (`string`), `Name` (`string`), `OptionGroupId` (`Guid`), `PaymentMethodId` (`Guid`).

### `cfg.PaymentMethodOptionGroups`

**Entity**: `Payments.Domain.Capabilities.PaymentMethodOptionGroup`
**Owner**: `ControlPlaneDbContext`
**Fields**: `Id` (`Guid`), `Code` (`string`), `Name` (`string`), `PaymentMethodId` (`Guid`).

### `cfg.PaymentProviders`

**Entity**: `Payments.Domain.Capabilities.PaymentProvider`
**Owner**: `ControlPlaneDbContext`
**Fields**: `Id` (`Guid`), `AdapterCode` (`int`), `Code` (`string`), `IsEnabled` (`bool`), `Name` (`string`), `UpdatedAt` (`DateTime?`), `UpdatedBy` (`Guid?`), `Version` (`long`).

### `cfg.PaymentProviderMethods`

**Entity**: `Payments.Domain.Capabilities.PaymentProviderMethod`
**Owner**: `ControlPlaneDbContext`
**Fields**: `Id` (`Guid`), `CreatedAt` (`DateTime`), `CreatedBy` (`Guid`), `IsActive` (`bool`), `PaymentMethodId` (`Guid`), `PaymentProviderId` (`Guid`), `UpdatedAt` (`DateTime?`), `UpdatedBy` (`Guid?`), `Version` (`long`).

### `cfg.PaymentProviderMethodOptions`

**Entity**: `Payments.Domain.Capabilities.PaymentProviderMethodOption`
**Owner**: `ControlPlaneDbContext`
**Fields**: `Id` (`Guid`), `CreatedAt` (`DateTime`), `CreatedBy` (`Guid`), `IsActive` (`bool`), `PaymentMethodId` (`Guid`), `PaymentMethodOptionId` (`Guid`), `PaymentProviderMethodId` (`Guid`), `UpdatedAt` (`DateTime?`), `UpdatedBy` (`Guid?`), `Version` (`long`).

### `txn.Transactions`

**Entity**: `Payments.Domain.Transaction`
**Owner**: `CommerceDbContext`
**Fields**: `Id` (`Guid`), `AttemptNo` (`int`), `ConfigurationVersion` (`long`), `CreatedAt` (`DateTime`), `CredentialVersionId` (`Guid`), `Environment` (`int`), `InquiryAttempts` (`int`), `LastInquiryAt` (`DateTime?`), `MerchantId` (`Guid`), `NeedsReview` (`bool`), `NextInquiryAt` (`DateTime?`), `OrderId` (`Guid`), `OrderSnapshot` (`string`), `PaymentMethod` (`string`), `Provider` (`int`), `ProviderAccountId` (`Guid`), `ProviderReference` (`string`), `ProviderRequestReference` (`string`), `ProviderStatus` (`string`), `RedirectUrl` (`string`), `ReturnBinding` (`string`), `ReviewCode` (`string`), `SafeProviderMetadata` (`string`), `Status` (`int`), `SucceededAt` (`DateTime?`), `TransactionNo` (`string`), `UpdatedAt` (`DateTime`), `Version` (`long`).

### `txn.TransactionEvents`

**Entity**: `Payments.Domain.TransactionEvent`
**Owner**: `CommerceDbContext`
**Fields**: `Id` (`Guid`), `EventReference` (`string`), `EvidenceCode` (`string`), `MerchantId` (`Guid`), `OccurredAt` (`DateTime`), `ProviderStatus` (`string`), `ReceivedAt` (`DateTime`), `SafeDetails` (`string`), `Source` (`string`), `Status` (`int?`), `TransactionId` (`Guid`).

## Keys, foreign keys and indexes

### Foreign keys

| Constraint | Child | Parent | Delete behavior |
|---|---|---|---|
| `FK_CartItems_Carts_CartId_MerchantId` | `shop.CartItems (CartId, MerchantId)` | `shop.Carts (Id, MerchantId)` | `CASCADE` |
| `FK_OrderItems_Orders_OrderId_MerchantId` | `shop.OrderItems (OrderId, MerchantId)` | `shop.Orders (Id, MerchantId)` | `CASCADE` |
| `FK_Permissions_PermissionGroups_GroupKey` | `iam.Permissions.GroupKey` | `iam.PermissionGroups.Key` | `RESTRICT` |
| `FK_RoleAssignments_Roles_RoleId` | `admin.RoleAssignments.RoleId` | `iam.Roles.Id` | `RESTRICT` |
| `FK_RoleAssignments_Roles_RoleId` | `merch.RoleAssignments.RoleId` | `iam.Roles.Id` | `RESTRICT` |
| `FK_RegistrationAttempts_Users_UserId` | `merch.RegistrationAttempts.UserId` | `merch.Users.Id` | `RESTRICT` |
| `FK_RolePermissions_Permissions_PermissionKey` | `iam.RolePermissions.PermissionKey` | `iam.Permissions.Key` | `RESTRICT` |
| `FK_RolePermissions_Roles_RoleId` | `iam.RolePermissions.RoleId` | `iam.Roles.Id` | `CASCADE` |
| `FK_ApprovalEvents_ApprovalRequests_ApprovalId` | `admin.ApprovalEvents.ApprovalId` | `admin.ApprovalRequests.Id` | `RESTRICT` |
| `FK_AuditRecords_AuditHeads_ScopeKey` | `admin.AuditRecords.ScopeKey` | `admin.AuditHeads.ScopeKey` | `RESTRICT` |
| `FK_Originators_Merchants_MerchantId` | `merch.Originators.MerchantId` | `merch.Merchants.Id` | `RESTRICT` |
| `FK_PspConnections_VaultSecretVersions_MerchantId_ActiveSecretVersionId` | `txn.PspConnections (MerchantId, ActiveSecretVersionId)` | `merch.VaultSecretVersions (MerchantId, Id)` | `RESTRICT` |
| `FK_PspConnections_VaultSecretVersions_MerchantId_PendingSecretVersionId` | `txn.PspConnections (MerchantId, PendingSecretVersionId)` | `merch.VaultSecretVersions (MerchantId, Id)` | `RESTRICT` |
| `FK_RoutingRules_Originators_MerchantId_OriginatorId` | `txn.RoutingRules (MerchantId, OriginatorId)` | `merch.Originators (MerchantId, Id)` | `RESTRICT` |
| `FK_RoutingRules_PspConnections_MerchantId_FallbackConnectionId` | `txn.RoutingRules (MerchantId, FallbackConnectionId)` | `txn.PspConnections (MerchantId, Id)` | `RESTRICT` |
| `FK_RoutingRules_PspConnections_MerchantId_TargetConnectionId` | `txn.RoutingRules (MerchantId, TargetConnectionId)` | `txn.PspConnections (MerchantId, Id)` | `RESTRICT` |
| `FK_RoutingRules_RoutingRulesets_MerchantId_RulesetId` | `txn.RoutingRules (MerchantId, RulesetId)` | `txn.RoutingRulesets (MerchantId, Id)` | `CASCADE` |
| `FK_InboundWebhookEvents_PspConnections_MerchantId_PspConnectionId` | `txn.InboundWebhookEvents (MerchantId, PspConnectionId)` | `txn.PspConnections (MerchantId, Id)` | `RESTRICT` |

`MerchantId`, `UserId`, `OrderId`, `PaymentSessionId`, `PspConnectionId`, `TransactionId` และ audit references ที่ไม่มีรายการด้านบนเป็น scalar/application relationships ไม่ใช่ physical FK. Transaction ตรวจ parent Order ผ่าน owner repository/merchant scope ก่อนอ่านหรือเปลี่ยน state.

### Unique constraints and indexes

| Table | Name | Columns / filter |
|---|---|---|
| `admin.MerchantAccess` | `IX_MerchantAccess_AdminUserId_MerchantId` | `(AdminUserId, MerchantId)` unique |
| `admin.ProvisioningOperations` | `UX_ProvisioningOperations_Key` | `OperationKey` unique |
| `admin.RoleAssignments` | `IX_RoleAssignments_AdminUserId_RoleId` | `(AdminUserId, RoleId)` unique |
| `admin.Sessions` | `IX_Sessions_TokenHash` | `TokenHash` unique |
| `admin.Users` | `IX_Users_Email` | `Email` unique |
| `admin.Users` | `IX_Users_Subject` | `Subject` unique, filter `Subject IS NOT NULL` |
| `iam.RolePermissions` | `IX_RolePermissions_RoleId_PermissionKey` | `(RoleId, PermissionKey)` unique |
| `iam.Roles` | `IX_Roles_MerchantId_Code` | `(MerchantId, Code)` unique |
| `merch.ExternalLogins` | `IX_ExternalLogins_Provider_Subject` | `(Provider, Subject)` unique |
| `merch.Merchants` | `IX_Merchants_Code` | `Code` unique |
| `merch.RegistrationAttempts` | `IX_RegistrationAttempts_UserId_AttemptNo` | `(UserId, AttemptNo)` unique |
| `merch.RegistrationNotices` | `IX_RegistrationNotices_UserId` | `UserId` unique |
| `merch.RoleAssignments` | `IX_RoleAssignments_UserId_RoleId` | `(UserId, RoleId)` unique |
| `merch.Sessions` | `IX_Sessions_TokenHash` | `TokenHash` unique |
| `merch.VaultRevealAudits` | `IX_VaultRevealAudits_MerchantId_Seq` | `(MerchantId, Seq)` unique |
| `admin.ApprovalEvents` | `IX_ApprovalEvents_SourceEventId` | `SourceEventId` unique |
| `admin.AuditHeads` | `IX_AuditHeads_ScopeKind_MerchantId` | `(ScopeKind, MerchantId)` unique, filter merchant scope |
| `admin.AuditHeads` | `UX_AuditHeads_PlatformScope` | `ScopeKind` unique, filter platform scope |
| `admin.AuditRecords` | `IX_AuditRecords_ScopeKey_PreviousHash` | `(ScopeKey, PreviousHash)` unique |
| `admin.AuditRecords` | `IX_AuditRecords_ScopeKey_Sequence` | `(ScopeKey, Sequence)` unique |
| `admin.OperationRecords` | `IX_OperationRecords_ActorId_Operation_IdempotencyKey` | `(ActorId, Operation, IdempotencyKey)` unique |
| `iam.ApiClients` | `IX_ApiClients_PendingRotationApprovalId` | `PendingRotationApprovalId` unique, filter non-null |
| `iam.ApiClients` | `IX_ApiClients_PublicClientId` | `PublicClientId` unique |
| `iam.OneTimeSecretTickets` | `IX_OneTimeSecretTickets_ApprovalId` | `ApprovalId` unique, filter non-null |
| `iam.OneTimeSecretTickets` | `IX_OneTimeSecretTickets_TicketHash` | `TicketHash` unique |
| `merch.Originators` | `IX_Originators_MerchantId_Code` | `(MerchantId, Code)` unique |
| `merch.VaultSecretVersions` | `IX_VaultSecretVersions_MerchantId_SecretName_State` | `(MerchantId, SecretName, State)` unique, filter active |
| `merch.VaultSecretVersions` | `IX_VaultSecretVersions_MerchantId_SecretName_Version` | `(MerchantId, SecretName, Version)` unique |
| `txn.AdminOperationRecords` | `IX_AdminOperationRecords_MerchantId_ActorId_Operation_IdempotencyKey` | `(MerchantId, ActorId, Operation, IdempotencyKey)` unique |
| `txn.RoutingRules` | `IX_RoutingRules_MerchantId_RulesetId_Priority` | `(MerchantId, RulesetId, Priority)` unique |
| `txn.RoutingRulesets` | `IX_RoutingRulesets_MerchantId_Status` | `(MerchantId, Status)` unique, filter active |
| `txn.InboundWebhookEvents` | `IX_InboundWebhookEvents_PspConnectionId_ExternalEventId` | `(PspConnectionId, ExternalEventId)` unique |
| `admin.NotificationDeliveries` | `IX_NotificationDeliveries_RuleId_SourceEventId` | `(RuleId, SourceEventId)` unique |
| `admin.WebhookDeliveries` | `IX_WebhookDeliveries_EndpointId_SourceEventId` | `(EndpointId, SourceEventId)` unique, filter original delivery |
| `admin.WebhookDeliveries` | `IX_WebhookDeliveries_OriginalDeliveryId_ReplayKey` | `(OriginalDeliveryId, ReplayKey)` unique, filter replay |
| `shop.Carts` | `AK_Carts_Id_MerchantId` | `(Id, MerchantId)` alternate key |
| `shop.Orders` | `AK_Orders_Id_MerchantId` | `(Id, MerchantId)` alternate key |
| `shop.Orders` | `IX_Orders_OrderNo` | `OrderNo` unique |
| `shop.Orders` | `IX_Orders_PaymentSessionId` | `PaymentSessionId`, filter non-null |
| `shop.Orders` | `IX_Orders_SummaryToken` | `SummaryToken` unique |
| `txn.PaymentSessions` | `IX_PaymentSessions_OrderId_Open` | `OrderId` unique, filter `Status IN (1, 2)` |
| `txn.PaymentSessions` | `IX_PaymentSessions_Psp_PspExternalChargeId` | `(Psp, PspExternalChargeId)` unique, filter non-null |
| `txn.PspConnections` | `IX_PspConnections_MerchantId_Psp` | `(MerchantId, Psp)` unique |
| `txn.Transactions` | `IX_Transactions_OrderId_AttemptNo` | `(OrderId, AttemptNo)` unique |
| `txn.Transactions` | `IX_Transactions_ProviderAccountId_Environment_ProviderReference` | provider reference unique, filter non-null |
| `txn.Transactions` | `IX_Transactions_ProviderAccountId_Environment_ProviderRequestReference` | provider request reference unique |
| `txn.Transactions` | `IX_Transactions_OrderId_Potential` | `OrderId` unique, filter `Status IN (1, 2)` |
| `txn.TransactionEvents` | `IX_TransactionEvents_TransactionId_EventReference_Source` | `(TransactionId, EventReference, Source)` unique |

Non-unique lookup indexes:

| Table | Indexes |
|---|---|
| `admin.AuthAudits` | `IX_AuthAudits_AdminUserId` |
| `admin.RoleAssignments` | `IX_RoleAssignments_RoleId` |
| `admin.Sessions` | `IX_Sessions_AbsoluteExpiresAt`, `IX_Sessions_AdminUserId`, `IX_Sessions_FamilyId` |
| `admin.Users` | employee identity/profile indexes from current Admin configuration |
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
| `txn.PaymentSessions` | `IX_PaymentSessions_OrderId`, `IX_PaymentSessions_OrderId_Open` |
| `txn.Transactions` | provider request/reference, order/merchant and status lookup indexes from `TransactionConfiguration` |
| `txn.TransactionEvents` | transaction/merchant/occurred-at lookup indexes from `TransactionEventConfiguration` |
| `txn.Deliveries` | merchant/status/next-at/lease and notification/source-event indexes |
| `txn.NotificationInboxMessages` | source-event and merchant/received-at indexes |
| `txn.NotificationReviewNotes` | delivery/notification/merchant/created-at indexes |
| `admin.ApprovalEvents` | `IX_ApprovalEvents_ApprovalId_OccurredAt` |
| `admin.ApprovalRequests` | `IX_ApprovalRequests_MerchantId_CreatedAt`, `IX_ApprovalRequests_Status_CreatedAt` |
| `admin.AuditRecords` | `IX_AuditRecords_Action_OccurredAt`, `IX_AuditRecords_ActorId_OccurredAt` |
| `admin.GovernanceOutboxMessages` | `IX_GovernanceOutboxMessages_ProcessedAt_LeaseExpiresAt` |
| `admin.OperationRecords` | `IX_OperationRecords_ExpiresAt` |
| `admin.WebhookEndpoints` | `IX_WebhookEndpoints_MerchantId_Enabled` |
| `admin.WebhookDeliveries` | `IX_WebhookDeliveries_MerchantId_Status_CreatedAt`, `IX_WebhookDeliveries_Status_NextAttemptAt_LeaseExpiresAt` |
| `iam.ApiClients` | `IX_ApiClients_MerchantId_Status` |
| `iam.OneTimeSecretTickets` | `IX_OneTimeSecretTickets_ExpiresAt` |
| `admin.DeliverySecretVersions` | `IX_DeliverySecretVersions_OwnerType_OwnerId_State` |
| `admin.NotificationDeliveries` | `IX_NotificationDeliveries_MerchantId_SentAt` |
| `admin.NotificationRules` | `IX_NotificationRules_MerchantId_Enabled` |
| `merch.AdminUserOperationRecords` | `IX_AdminUserOperationRecords_ActorId_Operation_IdempotencyKey`, `IX_AdminUserOperationRecords_ExpiresAt`, `IX_AdminUserOperationRecords_MerchantId_ActorId_Operation_IdempotencyKey` |
| `txn.AdminOperationRecords` | `IX_AdminOperationRecords_ExpiresAt` |
| `txn.InboundWebhookEvents` | `IX_InboundWebhookEvents_MerchantId_PspConnectionId`, `IX_InboundWebhookEvents_MerchantId_ReceivedAt`, `IX_InboundWebhookEvents_Status_ReceivedAt` |
| `txn.PspConnections` | `IX_PspConnections_MerchantId_ActiveSecretVersionId`, `IX_PspConnections_MerchantId_PendingSecretVersionId` |
| `txn.RoutingRules` | `IX_RoutingRules_MerchantId_FallbackConnectionId`, `IX_RoutingRules_MerchantId_OriginatorId`, `IX_RoutingRules_MerchantId_TargetConnectionId` |

## Native JSON and retired surfaces

Native SQL Server `json` columns มี 11 จุดตาม `PolDbContextModelSnapshot.cs`:

| Column | Contract |
|---|---|
| `acct.Agents.Metadata` | bounded Agent metadata |
| `acct.AgentRegistrations.ProfileJson` | registration draft profile |
| `acct.AgentRegistrationAttempts.ProfileJson` | immutable submission profile |
| `acct.Employees.Metadata` | Employee extension metadata |
| `merch.UserOutbox.Payload` | closed registration/KYC lifecycle event |
| `admin.ProvisioningOperations.Result` | closed provisioning result |
| `merch.Merchants.Metadata` | typed merchant extension |
| `shop.CartItems.Metadata` | typed cart item snapshot |
| `shop.OrderItems.Metadata` | immutable order item snapshot |
| `shop.OrderItems.RequestMetadata` | `VersionedMetadata` client envelope |
| `shop.Orders.Metadata` | `VersionedMetadata` order envelope |

`txn.OutboxMessages.Payload` และ `txn.PspConnections.Metadata` เป็น `nvarchar(max)`, ไม่ใช่ native `json`.

ไม่มี persisted/API surface ปัจจุบันสำหรับ legacy `CheckoutSession`, `CheckoutSessionItems`, `CheckoutConfirmed`, `shop.Products`, policy entity, policy audit/report, policy route, SQL RLS หรือ legacy product catalogue persistence. Current `checkout` schema เป็น `PaymentLinks`/replay และ current `txn` schema มี `Transactions`/`TransactionEvents` กับ notification runtime. Products อ่านจาก upstream ผ่าน `GET /api/v1/products`.

## Source of truth

1. `src/Infrastructure/BuildingBlocks.Infrastructure/Persistence/Migrations/` — 47 tracked migrations ถึง `20260911163519_ReviewFixPaymentLinkNotificationIntent`
2. `src/Infrastructure/BuildingBlocks.Infrastructure/Persistence/Migrations/PolDbContextModelSnapshot.cs`
3. EF configurations ใต้ `src/Infrastructure/Persistence/` และ `src/Infrastructure/Modules/`
4. Runtime context ownership ใน `src/Infrastructure/Persistence/Persistence.ControlPlane/ControlPlaneDbContext.cs` และ `src/Infrastructure/Persistence/Persistence.MerchantRuntime/MerchantRuntimeDbContext.cs`

เมื่อ schema เปลี่ยน ต้องอัปเดต migration, model snapshot และเอกสารนี้พร้อมกัน.
