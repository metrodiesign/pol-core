# Implementation Tasks: จำแนกความล้มเหลวของ dependency บนเส้นทางตรวจสถานะขายเอกสาร

> Status: approved 2026-08-06

> Each task is a cohesive, independently verifiable slice. Implement a whole task
> in one pass (it may touch many files). Decompose into sub-steps yourself at
> execution time — do NOT pre-split tasks here.

- [x] 1. primitives — `DependencyUnavailableException` (`BuildingBlocks.Application`),
     `PlatformReadGuard` (จับ `DbException`, cancellation ก่อนห่อ, Number/State/Class ลง message),
     arm 503 ใน handler (**wire เท่ากับ arm เดิมทุก byte**) + structured `{ExceptionType}` ใน log
     ทั้งสองบรรทัด, แก้ doc comment `MerchantRuntimeUnitOfWork.cs:7-8`, unit tests ของ guard
     (จำแนก/cancellation/message) + write-regression tests (unique violation -> 409 เดิม,
     write transport fail -> 500 ไม่ใช่ 503) — done = unit เขียว, ยังไม่มี call site ใดถูกห่อ
     Satisfies: REQ-1.4, REQ-1.5, REQ-3.3, REQ-4.1, REQ-4.2.
     Verify: `dotnet test` unit suites เขียว; diff handler มีแค่ arm ใหม่ + log property
     Evidence:
       - test: `dotnet test tests/Architecture.Tests --filter "FullyQualifiedName~PlatformReadGuardTests|FullyQualifiedName~MerchantRuntimeWriteRegression"` -> 8 passed / 0 failed
       - test: `dotnet test tests/Hosts.Tests --filter "FullyQualifiedName~ProblemDetailsExceptionHandlerTests"` -> 17 passed / 0 failed (รวม arm 503 ใหม่, ConflictException -> 409, raw DbException -> 500, leak probe ของ 503 ใหม่)
       - viewports: n/a — logic-only
       - deviations: none — diff handler มีแค่ arm ใหม่ (wire byte-เดิม) + `{ExceptionType}` ใน log 2 บรรทัด; SqlException ปลอมสร้างผ่าน reflection factory (`SqlExceptionFactory`) เพราะไม่มี public ctor

- [x] 2. sweep ห่อ read sites + gate — ห่อทุกจุดตามตาราง inventory ใน design.md (~30 จุด 16 ไฟล์
     รวม `MerchantRepository.cs:47` `ToDictionaryAsync`, แปลง `ExistsAsync` เป็น async method),
     ยืนยัน caller จริงของ `DoubleSellAuditor`/`VaultMaintenance` ก่อน commit ฝั่ง background,
     เติม `.ProducesProblem(503)` ตามกฎ "แตะ S2 read = ประกาศ 503" (อย่างน้อย `GetCart`,
     `ConfirmCheckout`, `AbandonCheckout`), สร้าง `PlatformReadGuardCoverageTests` (fact 1 token
     ครบชุด + fact 2 catch-all + fact 3 allowlist staleness, allowlist ราย (ไฟล์, method)) —
     done = gate เขียวบนโค้ดที่ห่อครบ, ไม่มี flag/config ข้ามการตรวจใน diff
     Satisfies: REQ-1.1, REQ-1.2, REQ-2.5, REQ-5.2. Depends on: 1.
     Verify: `dotnet test tests/Architecture.Tests --filter PlatformReadGuardCoverage` เขียว;
     build ทั้ง solution ผ่าน
     Evidence:
       - test: `dotnet build` (ทั้ง solution) -> 64 projects, 0 errors / `dotnet test tests/Architecture.Tests --filter "FullyQualifiedName~PlatformReadGuardCoverage"` -> 3 passed / 0 failed
       - caller check: `DoubleSellAuditor` <- `OrderPaidConsumer` (outbox consumer เท่านั้น), `VaultMaintenance` <- ไม่มี caller นอก DI registration -> ทั้งคู่คง allowlist ฝั่ง background ตาม design
       - OpenAPI: เติม `.ProducesProblem(503)` 22 endpoint ที่แตะ S2 read (webhook, cart 5, checkout 2, payments 3, orders 8, reports 2, merchants 2, admins me/get 2) — สามตัวที่ task บังคับ (`GetCart`/`ConfirmCheckout`/`AbandonCheckout`) อยู่ในชุดนี้
       - viewports: n/a — logic-only
       - deviations: fact 2 มี NonReadTokens exempt set (`SaveChangesAsync` ฯลฯ) ที่ design ไม่ได้ระบุ — จำเป็นเพื่อไม่ให้ catch-all บังคับห่อฝั่งเขียน (ขัด REQ-1.5); ไม่มี flag/config ข้ามการตรวจ

- [x] 3. เทสต์พฤติกรรมสองชุด + log — host ชุด 1 (DB ตาย: `FastFailConn` ผ่าน `UseSetting`, 4 ด่าน
     ตอบ 503 + body ไม่มี SQL/server/order id), host ชุด 2 (SQLite + fake `IDocumentSaleProbe` โยน
     ชนิดใหม่: add-item ไม่เพิ่มแถว, create-session ไม่มี row + fake PSP ไม่ถูกเรียก, checkout ไม่สร้าง
     session), เทสต์ read ล้มภายใน `ExecuteInTransactionAsync` -> ยังได้ 503, log assertions
     (`CapturingLoggerProvider` pattern: LogError + `{ExceptionType}` property + ไม่มี credential),
     integration `ProbeAsync` ตรงกับพอร์ตปิด -> ได้ชนิดใหม่ — done = เทสต์ใหม่ทุกตัวเขียว
     Satisfies: REQ-1.2, REQ-1.3, REQ-2.1, REQ-2.2, REQ-2.3, REQ-2.4, REQ-2.6, REQ-2.7,
     REQ-3.1, REQ-3.2, REQ-3.4, REQ-4.4. Depends on: 2.
     Verify: `dotnet test tests/Hosts.Tests` เขียวเต็มชุด + integration test ใหม่เขียวเมื่อมี SQL local
     Evidence:
       - test: `dotnet test tests/Hosts.Tests --filter "FullyQualifiedName~PlatformDependencyFailureEndpointTests"` -> 7 passed (ชุด 1 = 4 ด่าน 503 + leak-free body + structured log; ชุด 2 = 3 ด่านไม่มี state ค้าง)
       - test: `dotnet test tests/Hosts.Tests --filter "Category!=Integration"` -> 474 passed / 0 failed เต็มชุด
       - test: `dotnet test tests/Architecture.Tests --filter "...WriteRegression|...PlatformReadGuard"` -> 12 passed (รวม S5: guarded read ล้มใน `ExecuteInTransactionAsync` ยังออกเป็นชนิดใหม่)
       - test: `dotnet test tests/Integration.Tests --filter "...A_dead_platform_database_surfaces_as_DependencyUnavailable"` -> 1 passed (พอร์ตปิด ไม่ต้องมี SQL live)
       - viewports: n/a — logic-only
       - deviations: (1) ชุด 2 ใช้ in-memory fakes ตาม pattern `MerchantLifecycleEndpointTests` แทน SQLite-ใน-host (host ไม่มีทาง boot บน SQLite; สิ่งที่พิสูจน์เท่ากัน: ไม่มี state ค้างในทุก store) (2) S5 พิสูจน์ระดับ UoW จริง + SQLite (DbException จริงใน transaction จริง) ประกบกับ mapping test 503 — host ที่ DB ตายทั้งก้อนเข้าไม่ถึง read ใน transaction (3) `WebHardeningTests` เดิมคาด webhook resolver ตาย = 500 — อัพเดตเป็น 503 เพราะ resolver อยู่ใน S2 inventory (การเปลี่ยนนี้คือตัว spec เอง ไม่ขัด REQ-1.6 เพราะ 500 เดิมไม่ใช่ status ที่ spec ไหน pin)

- [x] 4. regression เต็มชุด + evidence — รัน `Hosts.Tests` + `Integration.Tests` + `Architecture.Tests`
     เต็มชุด (กรณีสำเร็จ status/payload เท่าเดิม, `BypassPrimitiveTests` เขียว, diff ไม่มี
     `ISecurityTelemetry` ใหม่, grep `EnableRetryOnFailure` = 0, transaction scope ไม่เปลี่ยน),
     mutation gate สองครึ่ง (read เปลือย -> fact 1 แดง; method นอก token list -> fact 2 แดง; ถอด ->
     เขียว) บันทึก Evidence ใต้ task นี้ — done = evidence ครบ + CI เขียว
     Satisfies: REQ-1.6, REQ-4.3, REQ-5.1, REQ-5.2, REQ-5.3, REQ-5.4. Depends on: 3.
     Verify: Evidence แนบใต้ task + CI ทั้ง `dotnet build + test` และ
     `dotnet integration (live SQL 2025)` เขียวบน PR
     Evidence:
       - test: `dotnet test tests/Hosts.Tests --filter "Category!=Integration"` -> 474 passed / 0 failed (กรณีสำเร็จ status/payload เท่าเดิม)
       - test: `dotnet test tests/Architecture.Tests` เต็มชุด -> 222 passed / 0 failed (รวม `BypassPrimitiveTests` + `PlatformReadGuardCoverageTests`)
       - test: `source .env.integration && dotnet test tests/Integration.Tests` (SQL local :11433 + sim :11434/:11435) -> 151 passed / 0 failed
       - mutation gate: read เปลือย (`AnyAsync` ไม่ห่อ) -> fact 1 แดงระบุ `CartRepository.cs:29 (AnyAsync)`; `BrandNewReadAsync` นอก token list -> fact 2 แดงระบุไฟล์:บรรทัด; revert -> 3/3 เขียว
       - regression checks: grep `EnableRetryOnFailure(` ใน src+tests = 0 จุด (hit เดียวคือ doc comment ที่บอกว่า "ปิดอยู่"), diff ไม่มี `ISecurityTelemetry`/`_telemetry.Emit` ใหม่ (REQ-4.3), `MerchantRuntimeUnitOfWork.cs` diff = doc comment ล้วน + `VaultAuditAppender.cs` ไม่ถูกแตะ (REQ-5.3), guard ไม่เพิ่ม query ต่อคำขอ (ห่อ call site เดิม 1:1 — REQ-5.2)
       - spec-trace: `scripts/spec-trace.sh probe-dependency-failure-mapping` -> OK เกณฑ์ 25 ข้อครบ, EARS lint ผ่าน
       - viewports: n/a — logic-only
       - CI: PR #188 (`feat/probe-dependency-failure-mapping`, commit `1bc4e31`) เขียวครบทุก check — `guards + spec-trace`, `docker build (api, migrate)`, `dotnet build + test`, `dotnet integration (live SQL 2025)` — ปิด Verify ของ task นี้แล้ว
       - deviations: none

## Suggested execution batches

> DEFAULT for a COUPLED feature (tasks share primitives/data/lib): run ALL tasks in
> ONE session — `scripts/pane-loop.sh probe-dependency-failure-mapping all-in-one`
> (or `/spec-implement all`). Separate sessions do NOT share cache, so each one re-pays
> the cold cache-write to re-acquire shared context. Split only for accuracy isolation.

ทั้ง 4 task เป็นสายเดียว (primitives -> sweep+gate -> เทสต์พฤติกรรม -> regression/evidence) —
all-in-one session เดียวเหมาะสุด ไม่มี Batch tag แยก; งานแตะ production เส้นเงิน — implement
ห้ามขยาย seam เกิน inventory ใน design (โดยเฉพาะห้ามลาม `Persistence.MerchantUsers` — residual B1
เป็น follow-up spec แยก)
