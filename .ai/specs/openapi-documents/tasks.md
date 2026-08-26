# Tasks: OpenAPI Documents by Audience

> Status: unknown

- [x] 1. เพิ่ม audience classifier, register `v1`/`merchant`/`admin`/`integration`, จำกัด security scheme
  และสร้าง active `x-tagGroups`; ตั้ง Scalar selector เป็นสาม named documentsโดย `merchant` เป็น default.
  Satisfies: REQ-1.1, REQ-1.2, REQ-1.3, REQ-1.4, REQ-1.5, REQ-2.1, REQ-2.2, REQ-2.3,
  REQ-2.4, REQ-2.5, REQ-2.6, REQ-2.7, REQ-2.8, REQ-2.9, REQ-3.1, REQ-3.2, REQ-3.3,
  REQ-3.4, REQ-3.5, REQ-3.6.
  Verify: `dotnet test tests/Hosts.Tests/Hosts.Tests.csproj --filter FullyQualifiedName~AudienceOpenApiDocumentTests`,
  `dotnet build pol-core.slnx -warnaserror`, `scripts/spec-trace.sh openapi-documents`, และ `git diff --check`.

  Evidence:
  - Contract: `dotnet test tests/Hosts.Tests/Hosts.Tests.csproj --no-build --nologo --filter 'FullyQualifiedName~AudienceOpenApiDocumentTests|FullyQualifiedName~OpenApiDocumentTests|FullyQualifiedName~MerchantUserScalarSecurityTests|FullyQualifiedName~SfsOpenApiTests'` -> Passed 19, Failed 0
  - Final host regression: `dotnet test tests/Hosts.Tests/Hosts.Tests.csproj --no-build --nologo` -> Passed 491, Failed 0
  - Offline solution regressionก่อน final named-DTO refinement: `dotnet test pol-core.slnx --no-build --nologo --filter 'Category!=Integration'` -> Passed 1,718, Failed 0; final solution buildและ affected testsเขียว
  - Build: `dotnet build pol-core.slnx --no-restore --nologo -warnaserror` -> 0 warnings, 0 errors
  - Trace: `scripts/spec-trace.sh openapi-documents` -> 20 criteria covered, EARS lint passed
  - Format: scoped `dotnet format ... whitespace --verify-no-changes` และ whitespace checks -> passed
  - Review: code/security reviewพบ audience DTO leak ระหว่าง named documents; แก้ transformer ให้ generate เฉพาะ DTO ของ document แล้ว targeted contract testผ่าน
  - Live database integration: ไม่รัน เพราะ change อยู่ที่ Development-only OpenAPI generation; ไม่มี database path หรือ schema change

- [x] 2. เติม summary/description ของ operation ให้ครบ และแก้ session security text ให้ตรง provider-scoped OIDC ปัจจุบัน โดยไม่เปลี่ยน API contract หรือ runtime behavior.
  Satisfies: REQ-4.1, REQ-4.2, REQ-4.3, REQ-4.4.
  Verify: `dotnet test tests/Hosts.Tests/Hosts.Tests.csproj --filter FullyQualifiedName~AudienceOpenApiDocumentTests`,
  `scripts/spec-trace.sh openapi-documents`, และ `git diff --check`.

  Evidence:
  - Contract: focused OpenAPI/Scalar suite -> Passed 19, Failed 0
  - Host regression: `dotnet test tests/Hosts.Tests/Hosts.Tests.csproj --no-build --nologo` -> Passed 491, Failed 0; final wording covered by focused suite
  - Build: `dotnet build pol-core.slnx --no-restore --nologo -warnaserror` -> 0 warnings, 0 errors
  - Trace: `scripts/spec-trace.sh openapi-documents` -> 24 criteria covered, EARS lint passed
  - Live: named documentsและ `/scalar/` -> HTTP 200; combined documentมี 163 operations, missing operationId/summary/description = 0/0/0
  - Accuracy: retired auth routes, task IDs, Bearer text และ retired permission keys -> 0 matches
  - Compatibility: OpenAPI ก่อนและหลังเมื่อตัดเฉพาะ summary/description -> identical
  - Browser: Admin API selector, endpoint groups และคำอธิบาย `CreateApiClient` แสดง metadata ล่าสุด
  - Database integration: ไม่รัน เพราะเปลี่ยนเฉพาะ OpenAPI metadata; ไม่มี database path หรือ schema change
