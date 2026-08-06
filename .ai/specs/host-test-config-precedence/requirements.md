> Status: approved (2026-08-06) — ตัดสินแล้ว: D1 = A (gate สแกนทั้ง `tests/`), D2 = B (คง `Program.cs` บังคับฝั่งเทสต์ + gate ตาม REQ-2.4) ดังนั้น REQ-1.5 ต้องกวาด 21 ไฟล์ และ REQ-2.4 มีผล (REQ-2.5 ไม่ใช้)

# Requirements: config ที่เทสต์ตั้งให้ host ต้องเป็นค่าที่ host ใช้จริง

## Overview

เทสต์ที่ boot host จริงผ่าน `WebApplicationFactory` ฉีดค่า config เข้าไปสองช่องทาง และสองช่องทางนั้น
**ไม่เท่ากัน** ค่าที่ฉีดผ่าน `ConfigureAppConfiguration` มาถึงช้ากว่าจุดที่ `Program.cs` อ่าน config
บางตัว host จึงเงียบ ๆ ไปใช้ค่าจากแหล่งอื่นแทน โดยไม่มี error ใด ๆ ที่บอกว่าเกิดเรื่องนี้ขึ้น

จุดที่พังบน PR #184:

| ข้อเท็จจริง | หลักฐาน |
|---|---|
| `Program.cs` อ่าน `ConnectionStrings:App` ที่ build time | `src/Hosts/Api/Program.cs:137` |
| จุดอ่านนั้นเกิดก่อน `builder.Build()` | `src/Hosts/Api/Program.cs:476` |
| ค่าที่เทสต์ฉีดผ่าน `ConfigureAppConfiguration` apply หลังจุดอ่าน | `tests/Hosts.Tests/MerchantCatalogueLiveEndpointTests.cs:149-153` (คำอธิบาย + reproduce ใน `.pipeline/products-external-source-of-truth/tests-t5.md` §รอบ 6) |
| ค่าที่ฉีดผ่าน `UseSetting` ถึงจุดอ่านนั้นทัน | `tests/Hosts.Tests/MerchantCatalogueLiveEndpointTests.cs:154-155` (fix, commit `b575e26`) |
| แหล่ง config ของแอปเอง (appsettings, user-secrets) อยู่เหนือ in-memory source ของ factory | `tests/Hosts.Tests/WebHardeningTests.cs:30-33`, `:341-343` |

เมื่อค่าไม่ถึง host จะตกไปใช้แหล่งไหนขึ้นกับเครื่อง และนั่นคือเหตุผลที่ dev เขียวแต่ CI แดง:

| สภาพแวดล้อม | ค่าที่ host ได้จริง | ผล |
|---|---|---|
| เครื่อง dev | `appsettings.Development.json:11` — `pol_app` + `Dev_Local_P@ssw0rd_2026` ซึ่ง**บังเอิญ**ตรงกับรหัสจริงบนเครื่องนั้น | เขียวมาตลอด |
| CI | `appsettings.Development.json` ไม่มีในเครื่อง (`.gitignore:14` ignore `appsettings.*.json`) เหลือ `appsettings.json:11` ที่มี `Password=` ว่าง ขณะที่ job สร้าง `pol_app` ด้วยรหัสสุ่มต่อ run (`.github/workflows/ci.yml:204`) | `SqlException: Login failed for user 'pol_app'` (Error Number:18456, State:1, Class:14) โผล่เป็น **500 opaque** ที่ `GET /api/v1/products` |

สองด่านที่ควรจับได้แต่จับไม่ได้ ทั้งคู่มีเหตุผลของตัวเอง:

- `Program.cs:138` throw เมื่อ `ConnectionStrings:App` เป็น null — แต่ `appsettings.json:11` ให้ค่าที่
  syntax ถูกเสมอ (แค่รหัสผ่านว่าง) throw นี้จึงไม่มีวันทำงาน
- `ProvisioningGuards.RequireInjectedCredential` (`Program.cs:2602-2608`) throw เมื่อรหัสผ่านว่าง — แต่ถูก
  ครอบด้วย `if (!builder.Environment.IsDevelopment())` (`Program.cs:173`) และ factory ทุกตัวเรียก
  `UseEnvironment(Development)` ด่านนี้จึงปิดอยู่ในเทสต์ทุกครั้ง

**failure class นี้เคยเผา repo นี้มาก่อนแล้วหนึ่งรอบ** ไม่ใช่ครั้งแรก: `Program.cs:225-227` มีคอมเมนต์ว่า
การเรียก `VaultKeyringFactory.Build` แบบ eager ตรงนั้น "reads builder.Configuration BEFORE deferred
test/host config sources are applied, which is exactly the CI-only 'Vault is not configured' boot crash"
(อ่าน config ก่อนแหล่งของเทสต์จะถูก apply = boot crash ที่เกิดเฉพาะบน CI) กลไกเดียวกัน คีย์คนละตัว
แก้ไปแล้วครั้งหนึ่งโดยไม่มีอะไรกันไม่ให้กลับมา และมันก็กลับมาจริง

**สิ่งที่ยังค้างอยู่วันนี้:** เทสต์ **21 ไฟล์** ใน `tests/Hosts.Tests/` ยังตั้ง
`ConnectionStrings:App`/`ConnectionStrings:Admin` ผ่าน `ConfigureAppConfiguration` (รายชื่อเต็มใน REQ-1)
ทุกไฟล์ไม่พังวันนี้เพราะ fake ทุก service ที่แตะ DB ค่าที่ผิดจึงไม่เคยถูกใช้เปิด connection — เป็นกับดัก
ที่รอวันมีใครถอด fake ตัวใดตัวหนึ่งออก

---

## REQ-1: ค่าที่เทสต์ตั้งคือค่าที่ host ใช้

**User Story:** ในฐานะผู้พัฒนา ฉันอยากให้ค่า config ที่ตั้งให้เทสต์ มีผลจริงกับ host ที่เทสต์นั้น boot
เพื่อไม่ต้องรู้ว่า `Program.cs` อ่านคีย์ไหนที่บรรทัดไหนก่อนจะเขียนเทสต์ได้ถูก

**Acceptance Criteria (EARS):**

- 1.1 WHEN เทสต์ boot host ผ่าน `WebApplicationFactory` แล้วตั้งค่า config คีย์หนึ่ง THE SYSTEM SHALL ให้
  host ใช้ค่านั้นทุกจุดที่อ่านคีย์นั้น รวมถึงจุดที่อ่านก่อน `builder.Build()`
- 1.2 THE SYSTEM SHALL ให้ค่าที่เทสต์ตั้งมีลำดับความสำคัญเหนือทุกแหล่งที่มาจากเครื่องที่รัน
  (`appsettings.json`, `appsettings.<Environment>.json`, user-secrets, environment variable)
- 1.3 THE SYSTEM SHALL ให้ผลของ 1.1 และ 1.2 เหมือนกันบนเครื่อง dev และบน CI โดยไม่ขึ้นกับว่าเครื่องนั้น
  มีไฟล์ที่ `.gitignore:14` กันไว้อยู่หรือไม่
- 1.4 IF ค่าที่เทสต์ตั้งไปไม่ถึงจุดที่ host อ่าน THEN THE SYSTEM SHALL ทำให้ boot ล้มเหลวทันทีพร้อมระบุคีย์
  ที่มีปัญหา แทนการตกไปใช้ค่าจากแหล่งอื่นเงียบ ๆ
- 1.5 THE SYSTEM SHALL แก้ทั้ง failure class ในรอบเดียว โดยไม่เหลือเทสต์ที่ boot host ตัวใดยังใช้รูปแบบเดิม
- 1.6 THE SYSTEM SHALL ไม่เปลี่ยนพฤติกรรมที่เทสต์แต่ละตัวยืนยันอยู่เดิม — จำนวนเทสต์ที่ผ่านใน
  `tests/Hosts.Tests/` ต้องไม่ลดลงจาก 463 (462 non-integration + 1 integration)

**สมาชิกของ class ที่ 1.5 ต้องกวาด** (ตั้ง `ConnectionStrings:*` ผ่าน `ConfigureAppConfiguration`):

`AdminAccountManagementEndpointTests.cs:55`, `AdminAuthLoginRedirectTests.cs:36`,
`AdminCorsGuardTests.cs:37`, `AdminMerchantsEndpointControlsTests.cs:56`,
`AdminProvisioningAuthorizationTests.cs:61`, `BackgroundDispatchCompositionRootTests.cs:32`,
`CorsTests.cs:31`, `CreatePaymentSessionContractTests.cs:30`, `CustomerPaymentEndpointTests.cs:186`,
`HostContainerTests.cs:42`, `MerchantLifecycleEndpointTests.cs:147`,
`MerchantUserAuthLoginRedirectTests.cs:36`, `MerchantUserScalarSecurityTests.cs:25`,
`MicrosoftAuthLoginRedirectTests.cs:41`, `OrderCancelEndpointTests.cs:122`,
`OrderSummaryEndpointTests.cs:39`, `PermissionGateSitesTests.cs:37`,
`RegistrationHistoryEndpointTests.cs:111`, `RouteSchemeAuthPreservationTests.cs:27`,
`RouteSchemeConventionTests.cs:28`, `SfsOpenApiTests.cs:24` (ทั้งหมดอยู่ใน `tests/Hosts.Tests/`)

สองไฟล์ที่ทำถูกอยู่แล้วและเป็นต้นแบบ: `WebHardeningTests.cs:34-36`,
`MerchantCatalogueLiveEndpointTests.cs:154-155`

---

## REQ-2: กันรูปแบบผิดกลับเข้ามาด้วยกลไกที่รันเอง

**User Story:** ในฐานะผู้พัฒนา ฉันอยากให้รูปแบบที่เพิ่งพังกลับเข้ามาแล้วแดงทันที
เพราะรอบนี้พิสูจน์แล้วว่าการรีวิวด้วยตาไม่จับ — คนที่ใส่ `ConfigureAppConfiguration` ไม่ได้ทำผิดกฎที่เขียนไว้ที่ไหน

**Acceptance Criteria (EARS):**

- 2.1 THE SYSTEM SHALL มี gate อัตโนมัติที่แดงเมื่อรูปแบบที่ทำให้ค่า config ของเทสต์ไปไม่ถึง host
  ถูกเพิ่มกลับเข้ามาในไฟล์ใหม่หรือไฟล์เดิม
- 2.2 THE SYSTEM SHALL ให้ gate ตาม 2.1 รันในงาน CI ที่ไม่ต้องใช้ secret และไม่ต้องมี SQL Server จริง
  (`dotnet build + test`, `.github/workflows/ci.yml:102`)
- 2.3 WHEN gate ตาม 2.1 แดง THE SYSTEM SHALL ระบุไฟล์ที่ผิด และบอกรูปแบบที่ถูกต้องแทน ไม่ใช่แค่รายงานว่าไม่ผ่าน
- 2.4 WHERE ยังคงมีจุดใน `Program.cs` ที่อ่าน config ก่อน `builder.Build()` (D2 ทางเลือก B) THE SYSTEM
  SHALL ให้ gate ตาม 2.1 บังคับว่าไม่มีเทสต์ที่ boot host ตั้งคีย์เหล่านั้นผ่านช่องทางที่มาถึงช้ากว่าจุดอ่าน
- 2.5 WHERE ทุกจุดอ่าน config ถูกย้ายไปหลัง `builder.Build()` แล้ว (D2 ทางเลือก A) THE SYSTEM SHALL ให้
  gate ตาม 2.1 บังคับว่าไม่มีการอ่าน config ที่ build time กลับเข้ามาใหม่ใน `Program.cs`
- 2.6 IF gate ตาม 2.1 ต้องมี allowlist THEN THE SYSTEM SHALL ตรวจด้วยว่ารายการใน allowlist ยังจำเป็นอยู่จริง
  และแดงเมื่อมีรายการที่หมดหน้าที่แล้วค้างอยู่
- 2.7 THE SYSTEM SHALL ให้ gate ตาม 2.1 ล้มเหลวจริงเมื่อทดลองใส่รูปแบบผิดกลับเข้าไป — พิสูจน์ด้วยการรัน
  ไม่ใช่ด้วยการอ่านโค้ดของ gate

**ที่ทางที่มีอยู่แล้วสำหรับ 2.1:** `tests/Architecture.Tests/SaleCodeRenameCompletenessTests.cs` เป็น gate
รูปแบบเดียวกันที่ทำงานอยู่จริงในโปรเจกต์นี้ — สแกนไฟล์ทั้ง repo (`:15` roots = `src`/`tests`/`docker`,
`:71-78` หา repo root จาก `pol-core.slnx`), มี allowlist (`:21-27`) และมีเทสต์คู่ที่กัน allowlist เน่า
(`:56-69` ตรงกับ 2.6 พอดี) อีกทางคือ gate script แบบ `scripts/check_rename_identifiers.py` ที่ CI เรียกที่
`.github/workflows/ci.yml:61` — แต่ทางแรกอยู่ในงาน CI ที่ 2.2 ต้องการอยู่แล้ว

---

## REQ-3: เทสต์ที่พึ่ง credential จริงต้องล้มเหลวแบบพูดได้

**User Story:** ในฐานะผู้พัฒนาที่เจอเทสต์แดงบน CI ฉันอยากรู้สาเหตุจาก log ของ run นั้นเลย
ไม่ใช่ต้องเดาแล้ว push commit ใส่ instrumentation ก่อนถึงจะเริ่มวินิจฉัยได้

**Acceptance Criteria (EARS):**

- 3.1 IF เทสต์ที่ boot host และเปิด connection จริงได้ status code ที่ไม่ใช่ค่าที่คาด THEN THE SYSTEM SHALL
  รายงาน response body และ log ระดับ Warning/Error ที่ host บันทึกไว้ระหว่างคำขอนั้น
- 3.2 THE SYSTEM SHALL รวม type ของ exception, ข้อความ และ stack trace ไว้ในรายงานตาม 3.1
- 3.3 THE SYSTEM SHALL ให้รายงานตาม 3.1 ปรากฏใน output ของ `dotnet test` โดยไม่ต้องเปิด verbose logging
  หรือ artifact เพิ่ม
- 3.4 THE SYSTEM SHALL ไม่ผ่อนเกณฑ์การ assert เพื่อแลกกับ diagnostics — status code ที่คาดต้องยังถูกบังคับ
  แบบตรงค่า และห้ามมี retry ที่เปลี่ยนแดงเป็นเขียว
- 3.5 THE SYSTEM SHALL ไม่ให้ข้อมูลใน 3.1 มี credential หรือค่าที่เป็นความลับ

**ของที่มีอยู่แล้ว:** `MerchantCatalogueLiveEndpointTests.cs:109-135` (`CapturingLoggerProvider`) และ `:277-286`
(`AssertOk`) จาก commit `8b5c20f` ทำครบ 3.1-3.3 แล้ว และเป็นตัวที่เปิดโปง root cause จริง ๆ ในรอบที่ 6
(`tests-t5.md` §รอบ 6) ทั้งสองเป็น `file`-scoped ในไฟล์นั้น

---

## REQ-4: พิสูจน์ว่าเทสต์ไม่ได้เขียวเพราะบังเอิญ

**User Story:** ในฐานะผู้พัฒนา ฉันอยากให้เทสต์ที่เขียวบนเครื่องฉัน เขียวเพราะระบบทำถูก
ไม่ใช่เพราะค่าใน `appsettings.Development.json` ของเครื่องฉันบังเอิญตรงกับความจริง

**Acceptance Criteria (EARS):**

- 4.1 THE SYSTEM SHALL มีเทสต์ที่ยืนยันว่า connection string ที่ host ใช้จริงหลัง boot เท่ากับค่าที่เทสต์ตั้งไว้
- 4.2 WHEN เทสต์ตาม 4.1 ทำงาน THE SYSTEM SHALL ให้มีค่าที่ขัดกันอยู่ในแหล่ง config ที่ลำดับต่ำกว่าด้วย
  เพื่อให้ผลลัพธ์แยกได้ว่า "ค่าไปถึงจริง" ไม่ใช่ "ค่าบังเอิญตรงกันทั้งสองแหล่ง"
- 4.3 THE SYSTEM SHALL ให้เทสต์ตาม 4.1 ผ่านหรือแดงเหมือนกันบนทุกเครื่อง โดยไม่ขึ้นกับไฟล์ที่ไม่ได้อยู่ใน repo
- 4.4 THE SYSTEM SHALL ให้เทสต์ตาม 4.1 ไม่ต้องเปิด connection จริงกับฐานข้อมูล เพื่อให้รันได้ในงาน CI ตาม 2.2
- 4.5 IF การแก้ตาม REQ-1 ถูกย้อนกลับเป็นรูปแบบเดิม THEN THE SYSTEM SHALL ทำให้เทสต์ตาม 4.1 แดง
- 4.6 THE SYSTEM SHALL บันทึกหลักฐาน mutation สองครึ่งไว้ในเอกสารของงานนี้ — รูปแบบเดิมภายใต้เงื่อนไข
  ที่ค่าในแหล่งสำรองไม่ตรงความจริง ต้องแดง และรูปแบบใหม่ภายใต้เงื่อนไขเดียวกัน ต้องเขียว

**ทำไม 4.4 สำคัญ:** งาน CI ที่มีฐานข้อมูลจริงคือ `dotnet integration (live SQL 2025)`
(`.github/workflows/ci.yml:139`) ซึ่งต้องใช้ repo secret `MSSQL_SA_PASSWORD` และคอมเมนต์ของมันเองที่
`:136-138` ระบุว่า "Add this to branch protection once the secret is configured" — แปลว่ายังอาจไม่ใช่
required check ถ้าเช็คกันความบังเอิญไปอยู่ในงานนั้น มันจะกันได้เฉพาะตอนที่งานนั้นรัน

---

## Edge Cases & Open Questions

### กรณีขอบที่ต้องมีเทสต์

1. เครื่องที่ไม่มี `appsettings.Development.json` เลย (สภาพของ CI) — เทสต์ทั้งชุดต้องให้ผลเหมือนเครื่องที่มี
2. เครื่องที่มี `appsettings.Development.json` ซึ่งค่าไม่ตรงกับความจริงของเครื่องนั้น — คือเงื่อนไขที่ใช้
   reproduce บั๊กนี้แบบ deterministic ในรอบที่ 6 และเป็นเงื่อนไขของ REQ-4.2
3. คีย์ที่ยังฉีดผ่าน `ConfigureAppConfiguration` ได้อย่างปลอดภัยวันนี้ เช่น `Vault:MasterKeyBase64`
   (ปลอดภัยเพราะ keyring ถูกทำเป็น DI singleton แบบ lazy แล้ว — `Program.cs:225-228`) ถ้ามีใครเปลี่ยนกลับเป็น
   eager คีย์นี้จะกลายเป็นสมาชิกของ class ทันที gate ตาม REQ-2 ควรกันเคสนี้ด้วย ไม่ใช่กันเฉพาะ
   `ConnectionStrings:*`

### ข้อสังเกตนอกขอบเขต (รายงาน ไม่แก้ในงานนี้)

4. `ConnectionStrings:Admin` **ไม่มีใครอ่านใน `src/` เลย** (มีแต่ `App` ที่ `Program.cs:137` และ `Migrator`
   ที่ `:484`) แต่เทสต์ 21 ไฟล์ตั้งค่านี้ และคอมเมนต์ที่ `HostContainerTests.cs:40` บอกว่า
   "ConnectionStrings:Admin is required at boot (Program.cs fails fast without it)" ซึ่งไม่จริงแล้ว —
   `ConnectionStrings:Worker` ที่ `WebHardeningTests.cs:36` ก็ไม่มีคนอ่านเช่นกัน คีย์ตายเหล่านี้ทำให้บล็อก
   config ของเทสต์ดูสำคัญกว่าที่เป็น
5. `src/Hosts/Worker/` เหลือแต่ build artifact ไม่มี `Program.cs` แล้ว — มี host จริงตัวเดียวคือ `Api`
   ซึ่งทำให้ขอบเขตของงานนี้แคบกว่าที่ชื่อโฟลเดอร์ชวนคิด
6. `MerchantCatalogueLiveEndpointTests.cs` ยังมี nit ค้างจาก `review-t5.md` ข้อ 2-8 (ถ้อยคำคอมเมนต์ที่มั่นใจ
   เกินหลักฐาน, `using` ที่ควรย่อ, ชนิด parameter ที่ไม่ตรงกันสองที่) ไม่เกี่ยวกับงานนี้แต่จะอยู่ในไฟล์เดียวกัน
7. ยังไม่มีความจำเป็นต้องดึง `CapturingLoggerProvider`/`AssertOk` (REQ-3) ออกมาเป็นของกลาง — วันนี้มีผู้ใช้
   รายเดียว (`MerchantCatalogueLiveEndpointTests` เป็นเทสต์ `Category=Integration` ตัวเดียวใน `Hosts.Tests`
   ยืนยันแล้วใน `review-t5.md` §Class sweep) ตัวกระตุ้นให้ย้ายคือมีผู้ใช้รายที่สอง ไม่ใช่ตอนนี้

---

## จุดตัดสินใจที่รอ approve

### D1 — ขอบเขตของ gate: `Hosts.Tests` เท่านั้น หรือทุกที่ที่ boot host

วันนี้ `WebApplicationFactory` ปรากฏเฉพาะใน `tests/Hosts.Tests/` เท่านั้น (grep ทั้ง `tests/`)
`tests/Integration.Tests/` ต่อฐานข้อมูลตรงโดยไม่ boot host ทั้งสองทางเลือกจึงให้ผลเหมือนกัน **วันนี้**
ต่างกันเฉพาะตอนมีโปรเจกต์เทสต์ใหม่

| ทางเลือก | ข้อดี | ข้อเสีย |
|---|---|---|
| **A. สแกนทั้ง `tests/`** | ครอบโปรเจกต์เทสต์ใหม่ที่ยังไม่มีวันนี้โดยไม่ต้องแก้ gate; ต้นทุนเท่ากับทางเลือก B เป๊ะ (`SaleCodeRenameCompletenessTests.cs:15` สแกน `src`/`tests`/`docker` อยู่แล้ว) | สแกนไฟล์มากกว่าเล็กน้อย (เวลารันไม่ต่างอย่างมีนัย) |
| **B. สแกนเฉพาะ `tests/Hosts.Tests/`** | ขอบเขตตรงกับที่ปัญหาอยู่จริง อธิบายง่าย | โปรเจกต์เทสต์ใหม่ที่ boot host จะอยู่นอกสายตา gate เงียบ ๆ — เป็น failure mode เดียวกับที่งานนี้เกิดจาก |

**ข้อเสนอ: A** — ราคาเท่ากัน ตาข่ายกว้างกว่า และ repo มี precedent ที่สแกนกว้างอยู่แล้ว

### D2 — แก้ที่ `Program.cs:137` หรือบังคับที่ฝั่งเทสต์ (แตะ production code จึงต้องให้ user ตัดสิน)

| ทางเลือก | สิ่งที่ทำ | ข้อดี | ความเสี่ยง |
|---|---|---|---|
| **A. ย้ายจุดอ่านไปหลัง `builder.Build()`** | `Program.cs:137` เลิกอ่าน config ที่ build time; connection string ถูก resolve ตอน DI สร้าง context | ตัดต้นเหตุจริง — ช่องทางไหนก็ใช้ได้ ไม่ต้องมีใครจำกฎ; เทสต์ 21 ไฟล์ไม่ต้องแก้; ปิด `Vault` เคสในกรณีขอบข้อ 3 ไปด้วยโดยปริยาย | แตะ production boot path ที่ `appConnString` ถูกส่งต่อไปยัง registration หลายจุด (`Program.cs:194/202/204`, `:222-223`, `:228`) — เปลี่ยนเป็น resolve ตอน DI ต้องรื้อ signature เหล่านั้น เป็นงานใหญ่กว่าที่บั๊กนี้เรียกร้อง และเสี่ยงเปลี่ยนพฤติกรรม fail-fast ที่ `Program.cs:173-175` โดยไม่ตั้งใจ |
| **B. คง `Program.cs` ไว้ บังคับที่ฝั่งเทสต์ + gate** | เทสต์ทุกตัวใช้ `UseSetting` สำหรับคีย์ที่ host อ่านที่ build time; gate ตาม REQ-2 กันไม่ให้กลับ | diff เล็ก แตะเฉพาะไฟล์เทสต์; ตรงกับ fix ที่พิสูจน์แล้วใน `b575e26`; ไม่มีความเสี่ยงต่อ production boot | ต้นเหตุยังอยู่ — กันได้ด้วย gate เท่านั้น ถ้า gate มีช่องโหว่ (เช่น เขียนคีย์ด้วย string interpolation) รูปแบบผิดจะรอดเข้ามาอีก; และ `Program.cs` ยังเป็นกับดักสำหรับคีย์ตัวถัดไปที่มีใครเพิ่มการอ่านที่ build time |

**ข้อเสนอ: B** — บั๊กนี้เป็นบั๊กของ harness ไม่ใช่ของ production และทางเลือก A ราคาสูงกว่าที่โจทย์เรียกร้องมาก
แต่ **ถ้าเลือก B ต้องยอมรับว่า `Program.cs:137` ยังเป็นกับดัก** และ gate คือสิ่งเดียวที่ยืนอยู่ระหว่างมันกับ
รอบ CI ถัดไป — จึงเป็นเหตุผลที่ REQ-2.7 บังคับให้พิสูจน์ว่า gate แดงจริงด้วยการรัน ไม่ใช่ด้วยการอ่าน

หมายเหตุ: D2 กำหนดว่า REQ-2.4 หรือ REQ-2.5 มีผล และกำหนดว่า REQ-1.5 ต้องกวาด 21 ไฟล์ (ทางเลือก B)
หรือไม่ต้องกวาดเลย (ทางเลือก A)
