# Implementation Tasks: config ที่เทสต์ตั้งให้ host ต้องเป็นค่าที่ host ใช้จริง

> Status: approved 2026-08-06

> Each task is a cohesive, independently verifiable slice. Implement a whole task
> in one pass (it may touch many files). Decompose into sub-steps yourself at
> execution time — do NOT pre-split tasks here.

- [x] 1. sweep 21 ไฟล์เทสต์เป็น `UseSetting` — ย้ายทุก entry `ConnectionStrings:*` ออกจาก
     `ConfigureAppConfiguration` เป็น `builder.UseSetting(...)` แปลตรงตัว (คีย์อื่นในไฟล์คงเดิม,
     dict ที่ว่างแล้วถอด block ทิ้งได้, คีย์ตาย `Admin`/`Worker` ยังตั้งต่อ); ระหว่างกวาดตรวจว่ามีคีย์อื่น
     ที่ `Program.cs` อ่านก่อน `builder.Build()` ถูกตั้งผ่านช่องช้าอยู่หรือไม่ เจอ = ย้ายด้วยและจดไว้ให้ task 3
     — done = ไม่เหลือ `ConnectionStrings` ใน `ConfigureAppConfiguration` block ใดใน `tests/`
     Satisfies: REQ-1.1, REQ-1.2, REQ-1.3, REQ-1.5, REQ-1.6.
     Verify: `dotnet test tests/Hosts.Tests` ผ่าน >= 463 เท่าเดิม (462 non-integration + 1 integration);
     grep ยืนยันไม่มี `ConnectionStrings` ใน `ConfigureAppConfiguration` block ทั้ง `tests/`
     Evidence:
       - test: `dotnet test tests/Hosts.Tests` -> 462 passed / 1 failed (integration ตัวเดียว fail เพราะไม่มี env `POL_SA_PASSWORD` ใน run แรก); รันซ้ำ `source .env.integration && dotnet test tests/Hosts.Tests --filter MerchantCatalogueLiveEndpointTests` -> 1 passed / 0 failed — รวม 463/463 เท่า baseline
       - grep: `grep -rn '\["ConnectionStrings' tests/ --include="*.cs"` -> 0 match (ก่อนกวาด 42 entry ใน 21 ไฟล์)
       - viewports: n/a — logic-only
       - deviations: none — sweep-check พบว่าคีย์ build-time อื่น (`AdminAuth:*`/`MerchantAuth:*` อ่าน eager ที่ Program.cs:253/:291, `Seq:IngestionUrl` ที่ :130) ทุกเทสต์ตั้งผ่าน `UseSetting` อยู่แล้ว ไม่มีสมาชิกใหม่ต้องเพิ่มเข้า ban list ของ task 3

- [x] 2. canary `HostConfigPrecedenceCanaryTests` — boot host ด้วยค่าขัดกันสองช่องทาง
     (`UseSetting` = marker A, `ConfigureAppConfiguration` = marker B คีย์เดียวกัน) แล้ว assert ค่าที่
     host ใช้จริงจาก DI (`MerchantRuntimeDbContext` -> `Database.GetDbConnection().ConnectionString`)
     ต้องเป็น A โดยไม่เปิด connection; ข้อความ fail ระบุค่าทั้งสองตัว — done = เทสต์เขียวในชุดปกติ
     Satisfies: REQ-1.4, REQ-4.1, REQ-4.2, REQ-4.3, REQ-4.4.
     Verify: `dotnet test tests/Hosts.Tests --filter HostConfigPrecedenceCanary` เขียว
     โดยไม่มี SQL Server ให้ต่อ
     Evidence:
       - test: `dotnet test tests/Hosts.Tests --filter HostConfigPrecedenceCanary` -> 1 passed / 0 failed (15s) — marker ชี้ `(local)` ปลอม ไม่เปิด connection จริง
       - viewports: n/a — logic-only
       - deviations: เทียบ/รายงานค่าเป็น `Database=` token (ผ่าน `SqlConnectionStringBuilder`) แทน connection string เต็ม — กัน fail message รั่ว credential ของเครื่องเมื่อ host ตกไปใช้ค่า machine-local (สอดคล้อง REQ-3.5); จดให้ task 3: canary จงใจตั้ง `ConnectionStrings:App` ผ่าน `ConfigureAppConfiguration` (คือ marker B ตาม REQ-4.2) — gate Fact 1 ต้อง exempt ไฟล์นี้หนึ่งรายการพร้อม staleness check

- [x] 3. gate `HostTestConfigGateTests` ใน `Architecture.Tests` — 4 fact ตาม design: (1) ban
     `BuildTimeKeyPrefixes` ใน `ConfigureAppConfiguration` block ทั้ง `tests/` ด้วย text scan วงเล็บสมดุล
     ข้อความระบุไฟล์+คีย์+รูปแบบถูก, (2) pin ว่า `Program.cs` ยังอ่าน `GetConnectionString("App")` ก่อน
     `builder.Build()` — ย้ายเมื่อไรแดงสั่งถอด ban (ทางเข้าของ REQ-2.5 ถ้าเลือกทาง A ในอนาคต), (3) ทุกบรรทัด
     `builder.Configuration` ก่อน Build ต้อง match allowlist, (4) allowlist staleness check
     — done = Architecture.Tests เขียวทั้งชุดบนโค้ดหลัง task 1
     Satisfies: REQ-2.1, REQ-2.2, REQ-2.3, REQ-2.4, REQ-2.5, REQ-2.6. Depends on: 1.
     Verify: `dotnet test tests/Architecture.Tests --filter HostTestConfigGate` เขียว
     Evidence:
       - test: `dotnet test tests/Architecture.Tests --filter HostTestConfigGate` -> 4 passed / 0 failed (113ms)
       - test: `dotnet test tests/Architecture.Tests` (เต็มชุด) -> 210 passed / 0 failed (15m3s)
       - viewports: n/a — logic-only
       - deviations: Fact 1 มี exemption หนึ่งรายการ `HostConfigPrecedenceCanaryTests.cs` (canary ต้องปลูกค่า
         ขัดกันในช่องช้าตาม REQ-4.2 — design ไม่ได้ระบุ) พร้อม staleness check ใน Fact 4 ว่าไฟล์ยังอยู่และยัง
         ปลูกคีย์ต้องห้ามจริง; token `.ConfigureAppConfiguration(` ในตัว gate ประกอบจากสอง string ตอน runtime
         กันไฟล์ gate สแกนเจอตัวเอง

- [x] 4. หลักฐาน mutation + ยืนยัน REQ-3 — mutation gate สองครึ่ง (ใส่รูปแบบผิดกลับหนึ่งไฟล์ -> Fact 1
     ต้องแดงระบุไฟล์ / ถอด -> เขียว), mutation canary สองครึ่ง (สลับ canary เป็นช่องช้า -> แดงพร้อมค่าทั้งสอง /
     กลับ -> เขียว) บันทึกทั้งหมดเป็น Evidence ใต้ task นี้; รัน + อ่าน `MerchantCatalogueLiveEndpointTests`
     ยืนยัน `AssertOk`/`CapturingLoggerProvider` ยังรายงาน body + log + exception ใน output ปกติ,
     assertion ตรงค่า ไม่มี retry, ไม่มี credential ใน log — done = Evidence ครบทุกข้อ
     Satisfies: REQ-2.7, REQ-3.1, REQ-3.2, REQ-3.3, REQ-3.4, REQ-3.5, REQ-4.5, REQ-4.6.
     Depends on: 2, 3. Verify: Evidence แนบใต้ task + `dotnet test` ทั้ง `Hosts.Tests` และ
     `Architecture.Tests` เขียวรอบสุดท้าย
     Evidence:
       - mutation gate ครึ่งแดง (REQ-2.7): ใส่ `["ConnectionStrings:App"]` กลับใน `ConfigureAppConfiguration`
         ของ `SfsOpenApiTests.cs` ชั่วคราว -> `dotnet test tests/Architecture.Tests --filter HostTestConfigGate`
         -> Fact 1 `No_test_sets_a_build_time_key_through_ConfigureAppConfiguration` FAILED ข้อความระบุ
         "tests/Hosts.Tests/SfsOpenApiTests.cs: sets a 'ConnectionStrings' key inside ConfigureAppConfiguration"
         พร้อมรูปแบบถูก (`builder.UseSetting(...)`) — 1 failed / 3 passed
       - mutation gate ครึ่งเขียว: ถอด mutation -> รันซ้ำ -> 4 passed / 0 failed (128ms)
       - mutation canary ครึ่งแดง (REQ-4.5, REQ-4.6): สลับ canary ให้ตั้งค่าที่ต้องการผ่าน
         `ConfigureAppConfiguration` อย่างเดียว (รูปแบบเดิม) -> `dotnet test tests/Hosts.Tests --filter
         HostConfigPrecedenceCanary` FAILED ข้อความ: "the host booted with Database=VCentralPay, but the test
         set Database=UseSettingWins via UseSetting (conflicting deferred ... Database=DeferredSourceLoses)"
         — host ตกไปใช้ `appsettings.Development.json` ของเครื่องจริง แดงพร้อมค่าทั้งสอง ไม่มี credential
       - mutation canary ครึ่งเขียว: กลับเป็นรูปแบบใหม่เงื่อนไขเดียวกัน -> 1 passed / 0 failed (15s)
       - REQ-3 (อ่าน + รัน): `AssertOk` (:277-286) รายงาน status + response body + server logs ใน
         `Assert.Fail` message ปกติของ `dotnet test` (3.1, 3.3); `CapturingLoggerProvider` (:109-135) แนบ
         `exception.GetType().FullName` + Message + StackTrace (3.2); assert `== HttpStatusCode.OK` ตรงค่า
         ไม่มี retry loop ในไฟล์ (3.4); log จับเฉพาะ log line/exception ไม่มี connection string หรือ
         credential (3.5); รันจริง: integration test ผ่านใน suite เต็มรอบสุดท้าย
       - final: `source .env.integration && dotnet test tests/Hosts.Tests` -> 464 passed / 0 failed (463 เดิม
         + canary ใหม่ 1); `dotnet test tests/Architecture.Tests` เต็มชุด -> 210 passed / 0 failed (15m3s
         บนโค้ดสถานะสุดท้าย — gate 4 facts รันซ้ำหลัง revert เขียว); `scripts/spec-trace.sh` -> OK เกณฑ์ 24 ข้อครบ
       - viewports: n/a — logic-only
       - deviations: none

## Suggested execution batches

> DEFAULT for a COUPLED feature (tasks share primitives/data/lib): run ALL tasks in
> ONE session — `scripts/pane-loop.sh host-test-config-precedence all-in-one` (or `/spec-implement all`).
> Separate sessions do NOT share cache, so each one re-pays the cold cache-write to
> re-acquire shared context. Split only for accuracy isolation.

ทั้ง 4 task พันกันรอบ pattern เดียว (sweep -> canary -> gate -> evidence) — all-in-one session เดียว
เหมาะสุด ไม่มี Batch tag แยก; ลำดับสำคัญจุดเดียวคือ gate (task 3) ต้องมาหลัง sweep (task 1)
ไม่งั้น Fact 1 แดงบนไฟล์ที่ยังไม่กวาด
