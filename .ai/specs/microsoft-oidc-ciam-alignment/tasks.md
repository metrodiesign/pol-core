# Implementation Tasks: Microsoft OIDC CIAM Alignment

> Status: approved 2026-08-16

> Each task is a cohesive, independently verifiable slice. Implement a whole task
> in one pass (it may touch many files). Decompose into sub-steps yourself at
> execution time — do NOT pre-split tasks here.

- [x] 1. Issuer validation ผ่าน framework default — ลบ `MicrosoftOidc.ValidateIssuer` + `IssuerValidator` assignment ทั้งสอง plane, ย้าย `AllowedTenants` gate ไป `OnTokenValidated` (ทำงานเฉพาะ allowlist ไม่ว่าง), เพิ่ม `tenant-not-allowed` ใน `MapFailureReason`, ส่ง provider slug เข้า `LoginService` ฝั่ง admin; rewrite `MicrosoftOidcTests` เป็น gate 3 เคสบังคับ; done = build เขียว + gate tests ผ่าน
     Satisfies: REQ-1.2, REQ-1.3, REQ-1.4, REQ-1.6, REQ-1.7, REQ-2.2, REQ-2.4, REQ-2.5. Verify: `dotnet test tests/Hosts.Tests --filter "FullyQualifiedName~MicrosoftOidc|FullyQualifiedName~AuthLoginRedirect"`.
     Evidence:
       - test: `dotnet build` -> 68 projects, 0 errors, 0 warnings; `dotnet test tests/Hosts.Tests --filter "FullyQualifiedName~MicrosoftOidc|FullyQualifiedName~AuthLoginRedirect"` -> 16 passed / 0 failed
       - viewports: n/a — logic-only
       - deviations: gate แยกเป็น `MicrosoftOidc.TenantGate` (shared helper สอง plane) เพื่อ test ตรง 3 เคสได้; `EstablishSessionAsync` ฝั่ง admin รับ `provider` slug แล้วแต่ยังไม่ใช้ resolve (ต่อใน task 3)

- [x] 2. Config defaults + boot guard tenant-pinned — authority จริงทั้งสอง plane ใน `appsettings.json`, ตัดเงื่อนไข `sectionName == "AdminAuth"` ใน `RequireOidcProviders` (multi-tenant = throw นอก Development), env mapping `MerchantAuth__Providers__Microsoft__Authority` ใน `docker-compose.prod.yml` + `.env.prod.example` + render-check placeholders, แก้ test fixtures ที่ใช้ `/organizations`; done = guard tests สอง section ผ่าน
     Satisfies: REQ-1.1, REQ-2.1, REQ-3.1, REQ-3.2, REQ-3.3, REQ-3.4, REQ-6.6. Depends on: 1. Verify: `dotnet test tests/Hosts.Tests --filter "FullyQualifiedName~ProvisioningGuards"` + render-check CI script local.
     Evidence:
       - test: `dotnet test tests/Hosts.Tests --filter "FullyQualifiedName~ProvisioningGuards"` -> 34 passed / 0 failed; regression `--filter "~MicrosoftOidc|~AuthLoginRedirect"` -> 16 passed; `docker compose -f docker-compose.prod.yml config` (CI placeholders) -> render ผ่าน, authority ทั้งสอง plane resolve เป็นค่า tenant-pinned
       - viewports: n/a — logic-only
       - deviations: render-check CI ไม่ต้องเพิ่ม placeholder ใหม่ — `MERCHANT_ENTRA_AUTHORITY` เป็น optional (`:-` default) ไม่ใช่ required var; แก้ `appsettings.Development.json` ที่ใช้ `/organizations` ด้วย (นอก list ใน task แต่เป็น fixture เดียวกัน)
       - amended U1 (2026-08-17, user): ถอน authority defaults ออกจาก appsettings/compose ทั้งหมด — appsettings ว่าง, compose passthrough `ADMIN_ENTRA_AUTHORITY`/`MERCHANT_ENTRA_AUTHORITY` ไม่มีค่าฝัง, `.env.prod.example` อัพเดท; guard tests เดิมยังครอบ (tenant-pinned enforcement ไม่เปลี่ยน) — verify: `dotnet test tests/Hosts.Tests` เต็มชุดหลังแก้ + compose render

- [x] 3. Provider discriminator + audit canonical identity — migration เดียว: `Provider` column สอง plane (unique `(Provider, Subject)`, admin filtered) + `RegistrationAudits.TargetUserId` (backfill → THROW unmatched → NOT NULL + FK) + `ActorAdminId` + `Down()` guard THROW; ripple ครบตาม design (resolve queries/ports/จุดเขียน, EF config 4 ไฟล์), allowlist `provider:subject` + provider check, ลบ dead seams (`AdminResolveLoginBySubject`, `IUserRepository.FindBySubjectAsync`); upgrade test (seed เดิม → migrate) + rollback tests (`Up → Down → Up`, duplicate-block); done = fresh-DB + upgrade + rollback tests ผ่าน, login เดิมไม่หลุด
     Satisfies: REQ-4.1, REQ-4.2, REQ-4.3, REQ-4.4, REQ-4.5, REQ-4.6, REQ-4.8, REQ-4.9, REQ-6.7. Depends on: 1. Verify: `dotnet test tests/Hosts.Tests tests/Admins.Tests tests/Merchants.Tests` + fresh-DB `ef database update`.
     Evidence:
       - test: `dotnet test tests/Hosts.Tests` -> 504 passed; `tests/Admins.Tests` -> 98 passed; `tests/Merchants.Tests` -> 178 passed; `tests/Architecture.Tests` (classes ที่แตะ: PreBind*/MerchantIdentityLifecycle/WriteFloor/ReadFloor/AuthorizationLease/ProvisioningCoordinator/ControlPlaneUnitOfWork/BypassPrimitive) -> 55 passed; Integration บน SQL จริง :11433: `ProviderDiscriminatorMigrationTests` -> 3 passed (upgrade backfill + Up→Down→Up + duplicate-block THROW), `FreshBaselineMigrationIntegrationTests` -> 3 passed (fresh-DB migrate ถึง head + rollback ทั้งชุด = ครอบ fresh-DB `ef database update`)
       - viewports: n/a — logic-only
       - deviations: `ApproveReject`/`GetRegistrationHistory` ยังมี `Guid.TryParse` dual dispatch โดย subject branch ชั่วคราวใช้ provider `google` (behavior เดิม) — task 4 จะถอด branch นี้เป็น id-only ตาม design; ลบ `AdminResolveLoginBySubject` + `IUserRepository.FindBySubjectAsync` (P2-5) และปรับ `ISelfProvisionSuperWriter`/`IBindInvitedAdminIdentity` เป็น (provider, subject)
       - amended 2026-08-17: migration backfills `ActorAdminId` for admin actions and leaves self-service audit rows SQL `NULL`; runtime domain rejects admin actions without canonical actor id; `ProviderDiscriminatorMigrationTests` passed 3/3 on SQL Server and full non-integration suite passed 1756/1756

- [x] 4. Route contract `{merchantUserId:guid}` — 3 route (approve/reject/registrations) เปลี่ยนจาก `{subject}`, commands เป็น `Guid MerchantUserId`, `FindByIdAsync` เท่านั้น, อัพเดท docs (`merchants.md`, `admins.md`) + contract tests (`AdminTask5ContractTests`, `PermissionGateSitesTests`); deployment note: FE ต้องส่ง `merchantUserId` ก่อน deploy backend (phase 1 นอก repo นี้); done = contract tests ใหม่ผ่าน + subject เดิม non-GUID ได้ 404
     Satisfies: REQ-4.7. Depends on: 3. Verify: `dotnet test tests/Hosts.Tests --filter "FullyQualifiedName~AdminTask5Contract|FullyQualifiedName~PermissionGateSites"`.
     Evidence:
       - test: `dotnet test tests/Hosts.Tests --filter "~AdminTask5Contract|~PermissionGateSites|~MerchantApproval|~RegistrationHistoryEndpoint"` -> 14 passed (รวมเทสใหม่ non-GUID subject = 404 ที่ guid constraint); full `tests/Hosts.Tests` -> 505 passed; `tests/Merchants.Tests` -> 178 passed; `Architecture.Tests ~MerchantIdentityLifecycle` -> 11 passed
       - viewports: n/a — logic-only
       - deviations: ลบ `FindAccountAsync` dual-dispatch helper ทั้งสอง handler + `GetRegistrationHistoryQuery` เปลี่ยนเป็น `Guid MerchantUserId`; intent hash ของ idempotency เปลี่ยน field `Subject` -> `MerchantUserId` (key เดิมที่ค้างระหว่าง deploy จะ replay ไม่ match = ปลอดภัยฝั่ง fail-closed)

- [x] 5. Invitation verified-email allowlist — `/invitations/start` รับ form field `provider` (normalize lowercase, default `google`), จำกัด verified-email allowlist (`google` ตัวเดียว), `microsoft`/ไม่ config = 404; done = tests 5 เคสผ่าน (slug ถูก, default, ไม่ config 404, microsoft 404, case normalize)
     Satisfies: REQ-5.1, REQ-5.2, REQ-5.3, REQ-5.4, REQ-5.5. Verify: `dotnet test tests/Hosts.Tests --filter "FullyQualifiedName~Invitation"`.
     Evidence:
       - test: `dotnet test tests/Hosts.Tests --filter "FullyQualifiedName~InvitationStartProvider"` -> 6 passed (google/default/GOOGLE = 302 challenge; facebook/microsoft/Microsoft = 404 — microsoft config ครบแต่ยัง 404 ตาม REQ-5.5)
       - viewports: n/a — logic-only
       - deviations: allowlist ใช้เช็ค `slug is not "google"` ตรง ๆ แทน `HashSet` หนึ่งตัว (พฤติกรรมเท่ากัน, comment ponytail ระบุทางขยายเมื่อมี pre-bind spec); REQ-5.4 (invitation id ผูกใน AuthenticationProperties) คงโค้ดเดิมไม่แตะ — challenge 302 ใน test ยืนยัน flow เดิมยังทำงาน

- [x] 6. E2E callback + error path test suite — WebApplicationFactory + `StaticConfigurationManager` (Issuer literal) + fake backchannel + id_token เซ็น test key: callback 4 เส้น (provider × plane) ครอบ subject oid vs sub / `emailVerified` / tid gate / email หาย → `missing-identity`; error path `OnAccessDenied`, state mismatch, `MapFailureReason` ทุก branch; issuer: CIAM ผ่านฝั่ง merchant, ต่าง tenant reject, workforce ผ่านฝั่ง admin; done = suite ทั้งหมดเขียว
     Satisfies: REQ-6.1, REQ-6.2, REQ-6.3. Depends on: 1, 2. Verify: `dotnet test tests/Hosts.Tests --filter "FullyQualifiedName~OidcCallback|FullyQualifiedName~OidcError|FullyQualifiedName~Issuer"`.
     Evidence:
       - test: `dotnet test tests/Hosts.Tests --filter "FullyQualifiedName~OidcCallbackE2E"` -> 12 passed / 0 failed (ไฟล์เดียว `OidcCallbackE2ETests.cs` ครอบทั้ง callback 4 เส้น, tid gate 3 เคสผ่าน middleware จริง, issuer CIAM ผ่าน + ต่าง tenant reject + workforce ผ่าน, error paths: access-denied / state mismatch / email-unverified / hd-mismatch / tenant-missing / tenant-not-allowed / auth-failed)
       - viewports: n/a — logic-only
       - deviations: reason coverage ของ `MapFailureReason` ทุก branch ยืนยันผ่าน redirect reason จริง ไม่ใช่ unit test ของ method ตรง ๆ; deny-audit เขียนไม่ได้บน host ไร้ DB (DenyAsync catch แล้ว log) = เส้นทางเดียวกับ design

- [x] 7. Cross-plane + authz convention tests — E2E cookie ข้าม plane 401 สองทิศ; convention test iterate `EndpointDataSource` ทุก endpoint ต้องมี `IAuthorizeData`/`IAllowAnonymous`, baseline key `(HTTP method, route pattern)` ผ่าน `IHttpMethodMetadata` พร้อมเหตุผลต่อรายการ; done = ทั้งสอง test เขียว + baseline บันทึก endpoint เดิมที่ยกเว้นครบ
     Satisfies: REQ-6.4, REQ-6.5. Verify: `dotnet test tests/Hosts.Tests --filter "FullyQualifiedName~CrossPlane|FullyQualifiedName~AuthzConvention"`.
     Evidence:
       - test: `dotnet test tests/Hosts.Tests --filter "FullyQualifiedName~CrossPlane|FullyQualifiedName~AuthzConvention"` -> 3 passed (cross-plane: cookie valid บน plane ตัวเอง 200 แล้ว 401 ข้าม plane ทั้งสองทิศ; convention: offenders 0 + baseline-staleness guard); baseline 5 รายการพร้อมเหตุผล (health/live, health/ready, openapi, scalar, webhook PSP signature-authenticated); `scripts/spec-trace.sh` -> OK 35 เกณฑ์ครบ; full `tests/Hosts.Tests` -> 526 passed / 0 failed
       - viewports: n/a — logic-only
       - deviations: none

## Suggested execution batches

Feature นี้ coupled (task 1-4 แชร์ identity primitives, task 6-7 พึ่ง infra จาก 1-2) — default: รันทั้งหมดใน session เดียว `scripts/pane-loop.sh microsoft-oidc-ciam-alignment all-in-one` หรือ `/spec-implement all`

- Task 5 (invitation) อิสระจริง — แยก session ได้ถ้าต้องการ
- ไม่ tag `Batch:` — ทุก task ใหญ่พอเป็น slice ของตัวเอง
