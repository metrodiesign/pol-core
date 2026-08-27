# Implementation Tasks: Microsoft OIDC CIAM Alignment

> Status: approved 2026-08-16

> Each task is a cohesive, independently verifiable slice. Implement a whole task
> in one pass (it may touch many files). Decompose into sub-steps yourself at
> execution time — do NOT pre-split tasks here.

- [x] 1. Issuer validation ผ่าน framework default — ลบ `MicrosoftOidc.ValidateIssuer` + `IssuerValidator` assignment ทั้งสอง plane, ย้าย `AllowedTenants` gate ไป `OnTokenValidated` (ทำงานเฉพาะ allowlist ไม่ว่าง), เพิ่ม `tenant-not-allowed` ใน `MapFailureReason`, ส่ง provider slug เข้า `LoginService` ฝั่ง admin; rewrite `MicrosoftOidcTests` เป็น gate 3 เคสบังคับ; done = build เขียว + gate tests ผ่าน
     Satisfies: REQ-1.2, REQ-1.3, REQ-1.4, REQ-1.6, REQ-1.7, REQ-2.2, REQ-2.4, REQ-2.5. Verify: `dotnet test tests/Hosts.Tests --filter "FullyQualifiedName~MicrosoftOidc|FullyQualifiedName~AuthLoginRedirect"`.

- [x] 2. Config defaults + boot guard tenant-pinned — authority จริงทั้งสอง plane ใน `appsettings.json`, ตัดเงื่อนไข `sectionName == "AdminAuth"` ใน `RequireOidcProviders` (multi-tenant = throw นอก Development), env mapping `MerchantAuth__Providers__Microsoft__Authority` ใน `docker-compose.prod.yml` + `.env.prod.example` + render-check placeholders, แก้ test fixtures ที่ใช้ `/organizations`; done = guard tests สอง section ผ่าน
     Satisfies: REQ-1.1, REQ-2.1, REQ-3.1, REQ-3.2, REQ-3.3, REQ-3.4, REQ-6.6. Depends on: 1. Verify: `dotnet test tests/Hosts.Tests --filter "FullyQualifiedName~ProvisioningGuards"` + render-check CI script local.

- [x] 3. Provider discriminator + audit canonical identity — migration เดียว: `Provider` column สอง plane (unique `(Provider, Subject)`, admin filtered) + `RegistrationAudits.TargetUserId` (backfill → THROW unmatched → NOT NULL + FK) + `ActorAdminId` + `Down()` guard THROW; ripple ครบตาม design (resolve queries/ports/จุดเขียน, EF config 4 ไฟล์), allowlist `provider:subject` + provider check, ลบ dead seams (`AdminResolveLoginBySubject`, `IUserRepository.FindBySubjectAsync`); upgrade test (seed เดิม → migrate) + rollback tests (`Up → Down → Up`, duplicate-block); done = fresh-DB + upgrade + rollback tests ผ่าน, login เดิมไม่หลุด
     Satisfies: REQ-4.1, REQ-4.2, REQ-4.3, REQ-4.4, REQ-4.5, REQ-4.6, REQ-4.8, REQ-4.9, REQ-6.7. Depends on: 1. Verify: `dotnet test tests/Hosts.Tests tests/Admins.Tests tests/Merchants.Tests` + fresh-DB `ef database update`.

- [x] 4. Route contract `{merchantUserId:guid}` — 3 route (approve/reject/registrations) เปลี่ยนจาก `{subject}`, commands เป็น `Guid MerchantUserId`, `FindByIdAsync` เท่านั้น, อัพเดท docs (`merchants.md`, `admins.md`) + contract tests (`AdminTask5ContractTests`, `PermissionGateSitesTests`); deployment note: FE ต้องส่ง `merchantUserId` ก่อน deploy backend (phase 1 นอก repo นี้); done = contract tests ใหม่ผ่าน + subject เดิม non-GUID ได้ 404
     Satisfies: REQ-4.7. Depends on: 3. Verify: `dotnet test tests/Hosts.Tests --filter "FullyQualifiedName~AdminTask5Contract|FullyQualifiedName~PermissionGateSites"`.

- [x] 5. Invitation verified-email allowlist — `/invitations/start` รับ form field `provider` (normalize lowercase, default `google`), จำกัด verified-email allowlist (`google` ตัวเดียว), `microsoft`/ไม่ config = 404; done = tests 5 เคสผ่าน (slug ถูก, default, ไม่ config 404, microsoft 404, case normalize)
     Satisfies: REQ-5.1, REQ-5.2, REQ-5.3, REQ-5.4, REQ-5.5. Verify: `dotnet test tests/Hosts.Tests --filter "FullyQualifiedName~Invitation"`.

- [x] 6. E2E callback + error path test suite — WebApplicationFactory + `StaticConfigurationManager` (Issuer literal) + fake backchannel + id_token เซ็น test key: callback 4 เส้น (provider × plane) ครอบ subject oid vs sub / `emailVerified` / tid gate / email หาย → `missing-identity`; error path `OnAccessDenied`, state mismatch, `MapFailureReason` ทุก branch; issuer: CIAM ผ่านฝั่ง merchant, ต่าง tenant reject, workforce ผ่านฝั่ง admin; done = suite ทั้งหมดเขียว
     Satisfies: REQ-6.1, REQ-6.2, REQ-6.3. Depends on: 1, 2. Verify: `dotnet test tests/Hosts.Tests --filter "FullyQualifiedName~OidcCallback|FullyQualifiedName~OidcError|FullyQualifiedName~Issuer"`.

- [x] 7. Cross-plane + authz convention tests — E2E cookie ข้าม plane 401 สองทิศ; convention test iterate `EndpointDataSource` ทุก endpoint ต้องมี `IAuthorizeData`/`IAllowAnonymous`, baseline key `(HTTP method, route pattern)` ผ่าน `IHttpMethodMetadata` พร้อมเหตุผลต่อรายการ; done = ทั้งสอง test เขียว + baseline บันทึก endpoint เดิมที่ยกเว้นครบ
     Satisfies: REQ-6.4, REQ-6.5. Verify: `dotnet test tests/Hosts.Tests --filter "FullyQualifiedName~CrossPlane|FullyQualifiedName~AuthzConvention"`.
- ไม่ tag `Batch:` — ทุก task ใหญ่พอเป็น slice ของตัวเอง
