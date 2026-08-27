# Implementation Tasks: products-sp-gateway

> Status: approved 2026-07-31

> Each task is a cohesive, independently verifiable slice. Implement a whole task
> in one pass (it may touch many files). Decompose into sub-steps yourself at
> execution time — do NOT pre-split tasks here.
> อ่าน design.md ก่อนเริ่มทุก task — โครง SP 10 ขั้น, error mapping, upsert semantics
> ถูกตัดสินแล้วทั้งหมด ห้าม re-derive

- [x] 1. Database จำลอง hippodb/mammothdb + bootstrap wiring ทุก environment — `docker/bootstrap/02-external-sim.sql` ครบทั้งไฟล์ (CREATE DATABASE x2, `dbo.Documents` + filtered unique DocumentNo, SP 2 ตัวตามโครง 10 ขั้นใน design — validate fixed order + COLLATE BIN2 + window ต่อแถว + materialize `#page` + LIKE escape, principals/GRANT EXECUTE, seed deterministic prefix แยกฝั่ง `77xxx-`/`88xxx-`, self-check) + จุดเสียบ: `docker-compose.yml` `pol-db-init` (คำสั่งใหม่มี `-C -b`), `docker/migrate-entrypoint.sh` (`-N -b`, หลัง 01) + อัปเดต `docker/migrate-entrypoint.test.sh` (assert 02 รันหลัง 01, มี `-N`, ไม่มี `-C`), `.github/workflows/ci.yml` + `.gitlab-ci.yml` job integration รัน 02 ก่อน `dotnet test` — done = fresh `docker compose down -v && up` ผ่าน + sqlcmd smoke EXEC ทั้ง 2 SP ได้ + suite เดิมเขียว ไม่มี app change
     Satisfies: REQ-1 (ทั้งหมด), REQ-2 (ทั้งหมด — ตัว implementation), REQ-3 (ทั้งหมด). Verify: `docker compose down -v && docker compose up -d && bash docker/migrate-entrypoint.test.sh` + sqlcmd EXEC smoke ทั้ง 2 SP ด้วย pol_app.

- [x] 2. SP contract tests — `tests/Integration.Tests/SpDocumentContractTests.cs` + helper `IntegrationDb.ForCatalog(string)` ยิง SP ตรงด้วย connection `pol_app` พิสูจน์ contract ของ task 1 ครบตามตาราง Testing Strategy: normalization defaults, cap 25, THROW ครบ 9 + ลำดับ multi-invalid, 2 RS ตามลำดับ + ชื่อคอลัมน์, FAST (totals NULL, RS2 <= PageSize, HasNextPage), ทะเบียนรถเฉพาะ Motor, M1 (PaymentStatus ผิด + PaidDateFrom -> 50007 ยังยิง), M3 (RENEWAL window ต่อแถวเมื่อ ALL), หน้าเกินท้ายชุด, case-sensitivity, LIKE escape, predicate REQ-2.12 ทุกตัว
     Satisfies: REQ-2 (ทั้งหมด — ตัวพิสูจน์), REQ-3.1, REQ-10.1. Depends on: 1. Verify: `dotnet test tests/Integration.Tests --filter Category=Integration` (source `.env.integration` ใน call เดียวกัน) เขียว.

- [x] 3. ชั้น port + wire DTO + exception mapping — `Products.Application/Ports/` (`ISpDocumentGateway`, `SpDocumentContracts.cs` ตาม signature ใน design — ไม่มี BranchCode บน request, `SpDocumentSearchRejectedException`) + `BuildingBlocks.Application/UpstreamUnavailableException` + arm 503 ใน `BuildingBlocks.Web/ProblemDetailsExceptionHandler.Map()` + unit tests ของ exception mapping — done = build เขียว ยังไม่มี consumer
     Satisfies: REQ-4.1, REQ-4.2, REQ-4.3, REQ-4.4, REQ-4.5, REQ-4.6. Verify: `dotnet build pol-core.slnx -warnaserror` + unit test ใหม่เขียว.

- [x] 4. Domain + mapper — `ProductInput` เพิ่ม `PaymentStatus`/`PaidDate` (B1; ไล่แก้ caller ทุกจุดรวม `CreateProductCommand` + tests + seed path), `Product.Create` honor wire PAID/PaidDate, refactor `ApplyFields` ร่วม, `RefreshFromExternal` (DocumentNo guard + side-flip guard + no-downgrade semantics), `SpDocumentItemMapper` (skip row + dedupe + SourceSystem->ProductGroup + ignore wire InsuranceType + naive datetime) + unit tests ครบ (`ProductTests`, `SpDocumentItemMapperTests`)
     Satisfies: REQ-7.3, REQ-7.4, REQ-7.5, REQ-7.6, REQ-7.7, REQ-7.10. Depends on: 3. Verify: `dotnet test tests/Products.Tests` เขียว + build -warnaserror.

- [x] 5. Adapter จริง — `Products.Infrastructure/Sp/{SpDocumentOptions,SpDocumentGateway}.cs` (typed params 17+@BranchCode, RS order + GetOrdinal, error mapping M8-hardened: cancellation passthrough / 5000x -> Rejected / drift -> Upstream), `Microsoft.Data.SqlClient` pin exact ใน `Directory.Packages.props` + csproj, `PostConfigure` derive connection ใน `Program.cs`, DI ใน `ProductsModuleRegistration` + `tests/Integration.Tests/SpDocumentGatewayIntegrationTests.cs` (mapping ทั้ง 2 ฝั่ง, 50006 -> exception เลขตรง, @BranchCode จาก options)
     Satisfies: REQ-5 (ทั้งหมด), REQ-6.6, REQ-10.2. Depends on: 1, 3. Verify: integration tests เขียว + Hosts.Tests ยัง boot ได้ (`dotnet test tests/Hosts.Tests`).

- [x] 6. Cutover read path + upsert — `ProductFilterDto` (+`InsuranceType?`/`CountMode` + Parse), `ListProductsHandler` เขียนใหม่ (routing 5 เคส -> gateway -> mapper -> upsert -> envelope), `ProductPage`, `IProductRepository` (ลบ `ListAsync`, เพิ่ม `UpsertByDocumentNoAsync`) + impl ใน `Persistence.MerchantRuntime` (retry race 2601/2627 + reset tracker), endpoint `Program.cs` (`Produces<ProductPage>` + 503 + description) + `SfsOpenApi.AddProductQueryParameters`, ชะตากรรม test เดิม (M10: `git rm tests/Architecture.Tests/ProductRepositoryListTests.cs`, rewrite 2 ไฟล์ Hosts.Tests ด้วย fake gateway), insulation guard `tests/Hosts.Tests/SpInsulationTests.cs` (fail-closed), repository integration tests (upsert new/refresh/no-downgrade/retry), handler + filter unit tests
     Satisfies: REQ-6.1, REQ-6.2, REQ-6.3, REQ-6.4, REQ-6.5, REQ-6.7, REQ-7.1, REQ-7.2, REQ-7.8, REQ-7.9, REQ-8 (ทั้งหมด), REQ-9 (ทั้งหมด), REQ-4.7, REQ-4.8, REQ-10.3, REQ-10.4, REQ-10.5. Depends on: 3, 4, 5. Verify: test suite เต็ม (unit + integration + Hosts + Architecture) เขียว.

- [x] 7. Quality gates + docs + E2E — รัน `dotnet build pol-core.slnx -warnaserror`, suite เต็มรวม Integration, `bash scripts/spec-trace.sh products-sp-gateway` (ต้องพิมพ์ `OK:`), `check-rename-identifiers.sh`, `check-migration-lineage.sh`; E2E checklist ตาม design (fresh compose, curl ทั้ง 2 ฝั่ง, EXACT/FAST, 400 countMode, add-to-cart จาก Id upsert, 503 ตอน sim DB ดับ); อัปเดต docs ที่อ้าง read path เดิม (`docs/reference/platform-modules.md` ส่วน Products ถ้ามี) + บันทึกผลลง spec
     Satisfies: REQ-11 (ทั้งหมด). Depends on: 1-6. Verify: ทุก gate ผ่าน + E2E checklist ติ๊กครบใน task notes.
ให้ตัดที่รอยต่อ 1+2 (SQL ล้วน) | 3-6 (C#) | 7 (gate)
