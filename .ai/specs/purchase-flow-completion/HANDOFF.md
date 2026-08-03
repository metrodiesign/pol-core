# HANDOFF — purchase-flow-completion (rolling, ห้าม commit ไฟล์นี้)

> Lead: Fable orchestrator. Teammate ใหม่ 1 คน/task อ่านไฟล์นี้ก่อนเริ่มเสมอ
> เสร็จ task แล้ว append section ของตัวเองท้ายไฟล์ (สรุปสิ่งที่ทำ, decision, trap ใหม่, สิ่งที่ task ถัดไปต้องรู้)

## แผนรวม

- ลำดับ task: 1→2→3→4→5→6→7→8 (sequential, dependency ตาม tasks.md)
- Branch / PR (stacked):
  - `feature/purchase-flow-cart-checkout` = T1, T2 → PR A เข้า develop
  - `feature/purchase-flow-payment-lifecycle` (แตกจาก branch A) = T3, T4, T5 → PR B
  - `feature/purchase-flow-enrichment` (แตกจาก branch B) = T6, T7, T8 → PR C
- Teammate ห้าม push / ห้ามเปิด PR / ห้ามสร้าง branch — lead ทำ
- Commit บน branch ปัจจุบันได้เลย (pre-commit hooks จะรัน gate — ห้าม bypass)
- Spec 3 ไฟล์ (requirements/design/tasks) ยัง untracked — commit แรกของ T1 ให้ `git add` 3 ไฟล์นี้ด้วย (ห้าม add HANDOFF.md)

## Traps ที่เจอจริงมาแล้ว (จาก session ก่อน ๆ — อ่านก่อนเหยียบ)

- **tasks.md flip `[x]` + `Evidence:` block ต้องอยู่ใน Edit เดียวกัน** (old_string คร่อมตั้งแต่บรรทัด checkbox ถึงจุดแทรก Evidence) — แยก edit = task-gate hook block เสมอ. Evidence header ห้ามมี `-` นำหน้า (รูปแบบดู task ที่เสร็จแล้วใน spec อื่น)
- **task-gate รัน test ตอน flip** — flip หลัง test เขียวเท่านั้น
- `ef migrations add` แล้วตามด้วย `ef database update --no-build` = PendingModelChangesWarning — build ก่อน update
- `ef migrations add` อาจ emit DropColumn+AddColumn แทน RenameColumn = ข้อมูลหายเงียบ — migration ที่แตะข้อมูล (T6) เขียนมือตามลำดับใน design.md
- Payments มี `SessionConfiguration` 2 ไฟล์ (module Infrastructure + Persistence.MerchantRuntime) — config ต้อง mirror ทั้งคู่; `HasIndex` ซ้ำบน property เดิมคือ mutate ไม่ใช่ add
- Cart add-item 409 root cause ยืนยันแล้ว: Guid PK `ValueGeneratedOnAdd` + client mint ⇒ EF graph-paint Modified ⇒ UPDATE 0 rows — fix = `ValueGeneratedNever()` ทั้ง 2 mirror (Carts.Infrastructure + Persistence.MerchantRuntime)
- Integration tests: DB local = container pol-db ที่ :11433; `source .env.integration` ต้องอยู่ใน Bash call เดียวกันกับ `dotnet test`
- Hosts.Tests boot host จริง: `ValidateOnStart` ใหม่อาจฆ่า 17 test ที่ boot host; endpoint test = fake auth scheme + fake IAdminScope + replace ports (pattern `RegistrationHistoryEndpointTests`)
- ตาราง/คอลัมน์/sequence ใหม่ต้อง GRANT ให้ `pol_app` ใน migration (SQLite tests จับไม่ได้) + เช็ค `docker/bootstrap/assert-fresh-db.sql` ว่ามี count ที่ต้อง update ไหม
- rtk hook บีบ stdout ของ `git diff`/grep — งาน audit diff ให้ `rtk proxy git diff ... > ไฟล์` แล้วอ่านจากไฟล์
- ห้าม dump env ทั้งก้อน (`env`, `printenv`, `set` เดี่ยว ๆ) — secret รั่วลง transcript
- `FixedClock` ทำ `ORDER BY OccurredAt` ไม่มี tie-breaker — test ที่ sort ด้วยเวลาต้องมี tie-breaker
- ก่อน mark task สุดท้ายของ spec: รัน `scripts/spec-trace.sh purchase-flow-completion` — uncovered REQ = blocker
- Verify ข้อไหนพิสูจน์ไม่ได้จริง ให้รายงานตรง ๆ ใน deviations — ห้ามแต่ง evidence

## สถานะ

- [x] T1 — **DONE** commit `a45aece` + fix รอบ verifier `ed69183` บน `feature/purchase-flow-cart-checkout` (ยังไม่ push — lead ทำ). Working tree สะอาด เหลือแค่ HANDOFF.md untracked ตามแผน. รายละเอียดดู section "## T1 — done" + "## T1 — verifier fix" ท้ายไฟล์
- [x] T2 — **DONE** commit `302e62c` บน `feature/purchase-flow-cart-checkout` (ยังไม่ push — lead ทำ). Working tree สะอาด เหลือแค่ HANDOFF.md untracked ตามแผน. รายละเอียดดู section "## T2 — done" ท้ายไฟล์
- [x] T3 — **DONE** commit `3dc3193` บน `feature/purchase-flow-payment-lifecycle` (ยังไม่ push — lead ทำ). Working tree สะอาด เหลือแค่ HANDOFF.md untracked ตามแผน. รายละเอียด + signature ที่ T4/T8 ต้องเรียก ดู section "## T3 — done" ท้ายไฟล์
- [x] T4 — **DONE** commit `dca0903` บน `feature/purchase-flow-payment-lifecycle` (ยังไม่ push — lead ทำ). Working tree สะอาด เหลือแค่ HANDOFF.md untracked ตามแผน. รายละเอียดดู section "## T4 — done" ท้ายไฟล์
- [x] T5 — **DONE** commit `a0930ce` บน `feature/purchase-flow-payment-lifecycle` (ยังไม่ push — lead ทำ). Working tree สะอาด เหลือแค่ HANDOFF.md untracked ตามแผน. รายละเอียดดู section "## T5 — done" ท้ายไฟล์
- [x] T6 — **DONE** commit `c5902ff` + fix รอบ verifier `59b26d3` บน `feature/purchase-flow-enrichment` (ยังไม่ push — lead ทำ). Working tree สะอาด เหลือแค่ HANDOFF.md untracked ตามแผน. **ชื่อ field/enum/wire value ที่ T7/T8 ต้องใช้ ดู section "## T6 — done" ท้ายไฟล์**
- [x] T7 — **DONE** commit `f864fc6` บน `feature/purchase-flow-enrichment` (ยังไม่ push — lead ทำ). Working tree สะอาด เหลือแค่ HANDOFF.md untracked ตามแผน. **ops step ที่ต้องเข้า PR body + สิ่งที่ T8 ต้องใช้ (จุด mapping channel→method) ดู section "## T7 — done" ท้ายไฟล์**
- [x] T8 — **DONE** commit `40fba98` + fix รอบ verifier `8cfba51` บน `feature/purchase-flow-enrichment` (ยังไม่ push — lead ทำ). Working tree สะอาด เหลือแค่ HANDOFF.md untracked ตามแผน. **spec ปิดครบทั้ง 8 task — known gaps ทั้ง spec ดู section "## T8 — done" ท้ายไฟล์**

## T1 — done

Commit `a45aece` `fix(carts): mint cart item ids client-side so add-item inserts` (ยังไม่ push)

### ไฟล์ที่แตะ

- `src/Modules/Carts/Carts.Infrastructure/Items/ItemConfiguration.cs` — `builder.Property(x => x.Id).ValueGeneratedNever();`
- `src/Persistence/Persistence.MerchantRuntime/Carts/Items/ItemConfiguration.cs` — mirror เดียวกัน (ตัวที่ runtime + Hosts.Tests ใช้จริง)
- `src/BuildingBlocks/BuildingBlocks.Infrastructure/Persistence/Migrations/20260802134226_CartItemClientMintedId.cs` (+ `.Designer.cs`) — `Up`/`Down` ว่างจริง
- `src/BuildingBlocks/BuildingBlocks.Infrastructure/Persistence/Migrations/PolDbContextModelSnapshot.cs` — diff บรรทัดเดียว: ลบ `.ValueGeneratedOnAdd()` ออกจาก `Carts.Domain.Items.Item.Id`
- `tests/Hosts.Tests/InsuranceCheckoutEndToEndTests.cs` — ลบ `new Cart(...)` workaround + comment ที่โทษ rls-to-query-filter, เพิ่ม helper `CreateCartAsync()` / `AddProductToCartAsync()` (1 context = 1 request) + test ใหม่ `Adding_an_item_to_a_cart_created_by_an_earlier_request_inserts_the_line`
- `.ai/specs/purchase-flow-completion/{requirements,design,tasks}.md` — เข้า commit เดียวกันตามแผน (HANDOFF.md ไม่ได้ add)

### Decision

- Regression 2-scope วางใน `InsuranceCheckoutEndToEndTests` ไม่ใช่ `MerchantLifecycleEndpointTests` ที่ design.md แถว Testing Strategy ระบุ — เพราะ verify command ของ T1 คือ `--filter InsuranceCheckout` ถ้าวางในไฟล์ T2 filter จะไม่จับ. **T2 ยังต้องเพิ่ม regression ระดับ endpoint ใน `MerchantLifecycleEndpointTests` ตาม design เดิม** (ชั้นนี้พิสูจน์ handler+EF, ยังไม่พิสูจน์ HTTP)
- ไม่แตะ `Carts.Domain` เลย (design บอก "ไม่ถูกแตะ") — fix ทั้งหมดอยู่ชั้น EF configuration
- "2 DI scope" implement เป็น 2 `MerchantRuntimeDbContext` คนละตัวบน SQLite connection เดียวกัน = สิ่งที่ scoped DbContext 2 request ให้จริง ๆ (bug อยู่ที่ change tracker ไม่ใช่ที่ DI container)

### Trap ใหม่ที่เจอ

1. **`dotnet test <projA> <projB>` รันไม่ได้** — SDK 10.0.300 ตอบ `MSBUILD : error MSB1008: Only one project can be specified.` verify command หลาย task ใน tasks.md เขียนแบบ 2 project ในบรรทัดเดียว (T2, T4, T8 ก็เป็น) — ต้องแยกรันทีละ project เสมอ แล้วบันทึกเป็น deviation
2. **`destructive-guard` block `DROP DATABASE`** — probe DB ที่สร้างเพื่อ fresh-replay ลบเองไม่ได้ ต้องให้ user ยืนยัน. ตอนนี้ค้างบน :11433 อยู่ **2 ตัว: `VCentralPayT1Probe` (T1) และ `VCentralPayFreshT1` (teammate ก่อนหน้า)** — lead เก็บกวาดได้เมื่อสะดวก (dev DB `VCentralPay` ไม่ถูกแตะ). ครั้งหน้า: อยากได้ fresh-replay ที่ลบทิ้งได้เอง ต้องขอ user อนุมัติล่วงหน้า หรือใช้ `ModelConsistencyTests` แทนถ้า migration เป็น snapshot-sync เปล่า
3. **วิธี fresh-replay โดยไม่ล้าง dev DB** (ใช้ซ้ำได้กับ T6 ที่ต้อง migration proof): สร้าง catalog เปล่า + `CREATE USER pol_app FOR LOGIN pol_app` ผ่าน `docker exec pol-db /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -C` แล้วรัน `export POL_DESIGN_SQL="${POL_DESIGN_SQL/Database=VCentralPay;/Database=<probe>;}"` ก่อน `dotnet ef database update --context PolDbContext --project src/BuildingBlocks/BuildingBlocks.Infrastructure --startup-project src/Hosts/Api --no-build` (ต้อง `set -a; source .env; set +a` ใน Bash call เดียวกัน)
4. **พิสูจน์ regression test ว่าจับ bug จริง**: revert fix ชั่วคราวบน mirror `Persistence.MerchantRuntime` (ตัวเดียวพอ — Hosts.Tests ใช้ context นี้) แล้วรัน filter -> ต้องแดง แล้ว restore. ทำแล้วได้ 6 failed / 0 passed ทุกข้อ ด้วย `ConcurrencyConflictException` ที่ `AddItemToCartHandler.cs:32`
5. `Hosts.Tests` มี `ModelConsistencyTests.cs` (`HasPendingModelChanges`) อยู่แล้ว — **ไม่ได้อยู่ใน `Architecture.Tests`** filter `FullyQualifiedName~ModelConsistency` บน Architecture.Tests = 0 test. ใช้ `dotnet test tests/Hosts.Tests --filter ModelConsistencyTests` (0.5 วินาที) แทนการรัน Architecture.Tests เต็ม 9 นาที

### สิ่งที่ T2 ต้องรู้

- add-item เดินได้จริงแล้ว ทั้ง handler layer และข้าม scope — T2 ต่อยอดจากตรงนี้ได้เลย ไม่ต้อง workaround สร้าง cart เอง; ใช้ helper `CreateCartAsync()` / `AddProductToCartAsync()` ใน `InsuranceCheckoutEndToEndTests` เป็น pattern (`private` อยู่ในคลาสนั้น ถ้า `MerchantLifecycleEndpointTests` ต้องใช้ให้ก๊อป pattern ไม่ต้อง refactor แชร์)
- baseline ตัวเลข test ก่อน T2 เริ่ม: `Carts.Tests` 15, `Hosts.Tests` 386, `Architecture.Tests --filter CartItemAggregateBoundary|MoneyColumnMapping` 6 — ทั้งหมดเขียว
- `Cart.Reopen()` / `MarkCartCheckedOut` / guard mutation เมื่อไม่ Open: design.md บอก guard **มีอยู่แล้วใน domain** — T2 เช็คก่อนเขียนใหม่ (`src/Modules/Carts/Carts.Domain/Cart.cs`)
- migration ถัดไปของ branch นี้ต่อจาก `20260802134226_CartItemClientMintedId` — ระวัง timestamp inversion (ปัญหาที่เคยทำให้ seed ก่อนหน้าถูก revert เงียบ)

## T1 — verifier fix

Verifier อิสระตรวจ T1 ผ่าน 7/8 ข้อ — fail ข้อ 8 (test coverage): REQ-1.3 มี guard ที่ `Program.cs:679` แต่ไม่มี test ครอบทั้ง repo และ Evidence บรรทัด `Carts.Tests` เคลมว่าครอบ REQ-1.2/1.3 ทั้งที่ชุดนั้นไม่มี assertion เรื่อง `PaymentStatus` เลย. โค้ด fix ของ T1 เองถูกต้องครบ ไม่ถูกแตะในรอบนี้

### ที่แก้

- **NEW** `tests/Hosts.Tests/MerchantLifecycleEndpointTests.cs` — ไฟล์ที่ design.md Testing Strategy ระบุไว้ให้ T2 อยู่แล้ว สร้างล่วงหน้าโดยมี test เดียว: `Adding_a_product_that_is_not_UNPAID_is_rejected_with_400` (REQ-1.3). ยิง `POST /api/v1/carts/{id}/items` ผ่าน `WebApplicationFactory<Program>` — route + policy `merchant-user` + CSRF filter + minimal-API lambda ตัวจริง
- `.ai/specs/purchase-flow-completion/tasks.md` — แก้ถ้อยคำ Evidence บรรทัด `Carts.Tests` (ตัดคำเคลม REQ-1.3 ออก), เพิ่ม 2 บรรทัด evidence ของ REQ-1.3 + regression proof, อัปเดต Hosts.Tests เต็มชุด 386 -> 387, ปรับ deviation (2)

### วิธีที่ใช้ (T2 ใช้ต่อได้ทันที)

- fake auth: `file sealed class TestMerchantUserAuthHandler` + `services.PostConfigure<AuthorizationOptions>(o => o.AddPolicy("merchant-user", ...))` re-point ไป scheme ปลอม — overload ที่รับ `Action<AuthorizationPolicyBuilder>` เขียนทับ policy เดิมได้ (overload ที่รับ `AuthorizationPolicy` จะ throw ว่า policy ซ้ำ)
- CSRF: ใส่ `Cookie: mch_csrf=tok-1` + header `X-CSRF-Token: tok-1` (const `UserSessionCookies.CsrfCookieName` / `UserCsrfFilter.HeaderName` เข้าถึงได้ผ่าน `ApiHost::Api.Merchants` เพราะ Api มี `InternalsVisibleTo Hosts.Tests`)
- ไม่มี DB: fake `IProductRepository` แบบ scoped (last-registered wins) — path 400 ไม่แตะ EF เลย ไม่ต้องมี actor binding ด้วย (lambda อ่าน `actor.MerchantId` หลัง guard)
- body ยิงเป็น raw JSON `{"productId":"...","quantity":1}` ไม่ต้องอ้าง `AddItemToCartRequest` (internal record ใน Program.cs)

### Trap ใหม่

6. **guard คู่แฝด**: `product is null || product.PaymentStatus != PaymentStatus.UNPAID` มี **2 จุด** ใน `Program.cs` — add-item (บรรทัด ~679 -> 400) และ start-checkout (บรรทัด ~778 -> 409) แก้/แทนที่ด้วย Edit ต้องใส่ context ให้ unique เสมอ
7. mutation check ของ guard นี้: ตัดเป็น `if (product is null)` แล้วรัน `--filter MerchantLifecycle` -> ได้ `Expected: BadRequest / Actual: Conflict` (fall-through ไป `AddItemToCartCommand` แล้วตายเป็น 409) = test จับ guard หายได้จริง

## T2 — done

Commit `302e62c` `feat(checkouts): close the cart-checkout cycle with a freeze and a way back out` (ยังไม่ push)

### ไฟล์ที่แตะ

- `src/Modules/Carts/Carts.Domain/Cart.cs` — `Reopen()` (บรรทัดเดียว `Status = CartStatus.Open`)
- **NEW** `src/Modules/Carts/Carts.Application/CartLifecycle.cs` — `MarkCartCheckedOutCommand`/`ReopenCartCommand` + handler (`ICommand<Unit>` ตาม pattern `UpdateAdminProfile`; ใช้ `CartLoad.RequireAsync` เดิมเช็ค owner)
- `src/Modules/Checkouts/Checkouts.Domain/Session.cs` — `Abandon()` เพิ่ม branch `Abandoned -> return` ก่อน guard เดิม
- `src/Modules/Checkouts/Checkouts.Application/ICheckoutRepository.cs` — + `GetOpenForCartAsync`
- `src/Modules/Checkouts/Checkouts.Application/StartCheckout.cs` — pre-check -> `ConflictException`
- **NEW** `src/Modules/Checkouts/Checkouts.Application/AbandonCheckout.cs` — `AbandonCheckoutCommand` -> `AbandonCheckoutResult(CartId, Status)`
- `src/Modules/Checkouts/Checkouts.Infrastructure/SessionConfiguration.cs` + `src/Persistence/Persistence.MerchantRuntime/Checkouts/SessionConfiguration.cs` — index `IX_CheckoutSessions_CartId_Open` (named overload) ทั้ง 2 mirror
- `src/Persistence/Persistence.MerchantRuntime/Checkouts/CheckoutRepository.cs` — impl `GetOpenForCartAsync`
- `src/Hosts/Api/Program.cs` — `/checkouts`: cart-Open check + `MarkCartCheckedOutCommand` หลัง start; endpoint ใหม่ `POST /checkouts/{id}/abandon` -> abandon + `ReopenCartCommand`
- migration `20260802142744_CheckoutOneOpenSessionPerCart` (+ Designer + snapshot) — `CreateIndex`/`DropIndex` ล้วน
- tests: `tests/Carts.Tests/{CartTests,CartHandlerTests}.cs`, **NEW** `tests/Checkouts.Tests/CheckoutLifecycleTests.cs` (+ `FakeCheckoutRepository` เพิ่ม method ใหม่), **NEW** `tests/Architecture.Tests/OpenCheckoutIndexTests.cs`, **NEW** `tests/Integration.Tests/OpenCheckoutIndexIntegrationTests.cs`, `tests/Hosts.Tests/MerchantLifecycleEndpointTests.cs` (1 -> 13 test)
- `docs/reference/merchants.md` — เพิ่ม abandon เข้าตาราง funnel endpoints ของ merchant-user

### Decision

- **REQ-2.7 ไม่มีโค้ดใหม่เลย**: `ProblemDetailsExceptionHandler` map `InvalidOperationException -> 409` อยู่ก่อนแล้ว และ guard `Status == Open` ครบ 4 จุดใน `Cart` — งานคือ test อย่างเดียว (domain 4 mutation + endpoint Theory 4 เส้น) ตรงตาม design "ไม่แตะ domain"
- **race แพ้ index ได้ 409 ฟรี**: `MerchantRuntimeUnitOfWork` แปลง SQL 2627/2601 -> `ConflictException` อยู่แล้ว ไม่ต้องเขียน catch `DbUpdateException` ที่ไหนเลย — integration test pin เลข error + ชื่อ index ไว้เป็นหลักฐาน
- `AbandonCheckoutHandler` ใช้ `NotFoundException` (404) ต่างจาก `ConfirmCheckoutHandler` ที่ใช้ `InvalidOperationException` (409) — ไม่แตะ handler เดิม (T8 จะแก้ `GetSessionHandler` ไปทาง `NotFoundException` เหมือนกัน)
- `Cart.Reopen()` เป็น assignment บรรทัดเดียว (enum 2 ค่า => no-op เมื่อ Open ได้ฟรี ไม่ต้อง branch)
- endpoint test ใช้ **fake persistence port** (`ICartRepository`/`ICheckoutRepository`/`IUnitOfWork`/`IActorContext`) ไม่ใช่ DB — ชั้นที่พิสูจน์คือ orchestration + status code; EF/index พิสูจน์แยกที่ Integration.Tests

### Trap ใหม่ที่เจอ

8. **endpoint test ที่ต้องมี actor binding**: fake auth scheme ของ T1 ไม่มี claim `merchant_id` ⇒ `IMerchantScoped` message จะตาย `MerchantBindingException` -> 500. ทางแก้ที่ใช้ = `services.AddScoped<IActorContext>(_ => new BoundActor(merchantId))` (last-registered wins) ไม่ต้องยุ่งกับ claim เลย. คู่กับ fake `ICartRepository`/`ICheckoutRepository`/`IUnitOfWork` แบบ list-backed (aggregate อยู่ใน list ⇒ SaveChanges = no-op แต่ mutation ข้าม request ติดจริง) ได้ endpoint test เต็ม pipeline โดยไม่มี DB — **pattern นี้ T4/T8 ใช้ต่อได้ทันที** (ดู `file sealed class` ทั้ง 5 ตัวหัวไฟล์ `MerchantLifecycleEndpointTests.cs`)
9. `dotnet test tests/Architecture.Tests` เต็มชุดใช้เวลาแค่ ~2 วินาที (204 test) เมื่อ build อุ่นแล้ว — บันทึกเดิมที่ว่า "ครบชุด ~9 นาที" คือเวลารวม build เย็น ไม่ต้องกลัวรันเต็ม
10. **fresh-replay ไม่ต้องสร้าง probe DB ใหม่**: ใช้ `VCentralPayT1Probe` ที่ T1 ทิ้งไว้ซ้ำได้เลย (`export POL_DESIGN_SQL="${POL_DESIGN_SQL/Database=VCentralPay;/Database=VCentralPayT1Probe;}"` แล้ว `ef database update --no-build`) — ไม่เพิ่มขยะ ไม่ต้องเจอ `destructive-guard`
11. migration ที่เป็น index ล้วนไม่ต้อง GRANT และไม่กระทบ `docker/bootstrap/assert-fresh-db.sql` (ไฟล์นั้นไม่ได้นับ index) — แต่ **ต้องเช็คข้อมูลซ้ำก่อน apply**: `SELECT CartId, COUNT(*) FROM shop.CheckoutSessions WHERE Status IN (0,1) GROUP BY CartId HAVING COUNT(*) > 1` (ว่างจริงบน dev DB)

### สิ่งที่ T3 ต้องรู้

- baseline ตัวเลข test หลัง T2: `Carts.Tests` 20, `Checkouts.Tests` 21, `Hosts.Tests` 399, `Architecture.Tests` 204, `Orders.Tests` 76, `Payments.Tests` 162, `Products.Tests` 137
- `Payments.Infrastructure/Persistence/SessionConfiguration.cs` + mirror มี `IX_PaymentSessions_OrderId_Open` อยู่แล้ว (T3 ต้องใช้ index นี้กับ 2-phase expire+insert) — **อย่าเพิ่ม `HasIndex(x => x.OrderId)` แบบไม่ตั้งชื่อซ้ำ** จะ mutate index เดิม; `OpenSessionIndexTests` ใน Architecture.Tests คุมไว้แล้ว
- Integration test เคส index ของ T3 ลอกโครง `tests/Integration.Tests/OpenCheckoutIndexIntegrationTests.cs` ได้ตรง ๆ
- migration ถัดไปต่อจาก `20260802142744_CheckoutOneOpenSessionPerCart` — dev DB `VCentralPay` และ probe `VCentralPayT1Probe` อยู่ที่ head นี้ทั้งคู่
- docs ที่ยัง stale (ไม่ได้แก้ในรอบนี้ ตั้งใจให้ทำรอบเดียวตอนปิด PR): `docs/reference/platform-modules.md:724` ยังเขียนว่า `Abandon()` "ไม่มีผู้เรียก", `docs/reference/src-structure.md:400` ยังไม่มี abandon endpoint

## T3 — done

Commit `3dc3193` `feat(payments): decide every payment session on one confirm line` (ยังไม่ push)

### API ที่ T4/T8 ต้องเรียก (อ่านก่อนเขียนโค้ด)

ไฟล์: `src/Modules/Payments/Payments.Application/Confirmation/PaymentConfirmationService.cs`
namespace: `Payments.Application.Confirmation` — DI: `services.AddScoped<PaymentConfirmationService>()` ใน `PaymentsModuleRegistration` แล้ว (concrete class ไม่มี interface — inject ตรง ๆ)

```csharp
// รูปที่ผู้เรียกทุกคนใช้ ยกเว้น webhook
Task<ConfirmationOutcome> ConfirmAsync(Session session, CancellationToken cancellationToken);

// รูปเต็ม — webhook ส่ง access ที่ resolve เองแล้ว (by-id + secret ที่ reveal ไปตอน verify signature)
// และส่ง evt.EventId เป็น pspEventId
Task<ConfirmationOutcome> ConfirmAsync(
    Session session, PspAccess? access, string? pspEventId, CancellationToken cancellationToken);

public sealed record PspAccess(Connection Connection, string Secret);

public enum ConfirmationOutcome
{
    Pending = 0,        // ยัง chargeable, ยังไม่เกิน TTL, PSP ยังไม่ settle — ไม่มีอะไรเปลี่ยน
    Paid = 1,           // transition เกิดจริงในคอลนี้ + enqueue PaymentPaid แล้ว
    AlreadyPaid = 2,    // Paid อยู่ก่อนแล้ว — ไม่ transition ไม่ enqueue
    Duplicate = 3,      // claim ถูกใช้ไปแล้วโดยผู้เรียกอื่น — ไม่มีอะไรเปลี่ยน
    Failed = 4,         // session Failed (MarkFailed ในคอลนี้ หรือ Failed อยู่แล้ว)
    Expired = 5,        // session Expired (MarkExpired ในคอลนี้ หรือ Expired อยู่แล้ว)
    Conflicted = 6,     // PSP ยืนยัน Paid แต่ session terminal ไปแล้ว — LogCritical, ไม่เปลี่ยนอะไร, refund manual
    AmountMismatch = 7, // PSP เก็บยอด/สกุลที่ session ไม่รองรับ — LogCritical, ไม่ mark
}
```

พฤติกรรมที่ต้องรู้ตอน map เป็น HTTP:
- **service `SaveChangesAsync` เองเมื่อ transition จริง** (Paid/Failed/Expired) — ผู้เรียกเป็นเจ้าของ transaction boundary เท่านั้น ไม่ต้อง save ซ้ำ
- **`FetchChargeAsync` ไม่ถูก catch** — ambiguous (timeout/5xx/parse) ทะลุออกมาเป็น exception ของ adapter (`HttpRequestException`/`TaskCanceledException`/`JsonException`). **T4 `ReleaseOpenSessionCommand` และ T8 `ConfirmPaymentStatusCommand` ต้อง catch ที่ call site ของตัวเอง** แล้วตอบ `ConflictException` (409) / `pending` ตาม design — ห้าม `catch (Exception)` กว้าง ๆ แล้วอ่านเป็น "ยังไม่จ่าย"
- T8 short-circuit (REQ-8.13 "ไม่ fetch เมื่อ terminal") ต้องทำ **ก่อน** เรียก service — service ไม่ short-circuit ให้ (มันจะ fetch เสมอเมื่อมี chargeId เพื่อจับเคส `Conflicted`)
- key แชร์ = `{psp}:{connectionId}:charge:{chargeId}:confirmed`, context = `"payment-confirmation"`; `pspEventId` เพิ่ม key `{psp}:{connectionId}:event:{id}` และไปเป็น `PaymentPaid.EventId` (ผู้เรียกที่ไม่มี event id ได้ `inquiry:{chargeId}`)
- `Session.OpenTtl` (static 24h) + `Session.IsExpiredAt(now)` อยู่บน `Payments.Domain.Session` — `IsExpiredAt` คืน false เสมอสำหรับ terminal status

### ไฟล์ที่แตะ

- **NEW** `src/Modules/Payments/Payments.Application/Confirmation/PaymentConfirmationService.cs` (service + `ConfirmationOutcome` + `PspAccess`)
- `src/Modules/Payments/Payments.Domain/Session.cs` — `OpenTtl` + `IsExpiredAt` (ไม่มีคอลัมน์ใหม่ ไม่มี migration)
- `src/Modules/Payments/Payments.Application/HandlePspWebhook/HandlePspWebhookHandler.cs` — เหลือเฉพาะส่วน webhook-specific (resolve by id, verify signature, หา session, map outcome); ctor เปลี่ยนเป็น (connections, sessions, adapters, vault, unitOfWork, confirmation)
- `.../HandlePspWebhook/HandlePspWebhookCommand.cs` — แก้ doc ของ `WebhookOutcome.Processed` (ครอบ Failed/Expired ด้วย)
- `src/Modules/Payments/Payments.Application/CreateSession/CreateSessionHandler.cs` — lazy expire + `MintAsync` helper + ctor รับ `PaymentConfirmationService`
- `src/Modules/Payments/Payments.Application/Payments.Application.csproj` — + `Microsoft.Extensions.Logging.Abstractions` (แบบเดียวกับ Products.Application)
- `src/Modules/Payments/Payments.Infrastructure/PaymentsModuleRegistration.cs` — `AddScoped<PaymentConfirmationService>()`
- `tests/Architecture.Tests/TransactionInventoryTests.cs` — + call site `CreateSessionHandler.cs` = 1
- tests: **NEW** `tests/Payments.Tests/PaymentConfirmationServiceTests.cs` (15), **NEW** `tests/Integration.Tests/PaymentSessionExpiryIntegrationTests.cs` (2), `PaymentSessionTests` (+5), `CreateSessionHandlerTests` (+6 + harness), `Fakes.cs` (+`FakeSessionRepository.OnAdd`, +`RecordingLogger<T>`), `HandlePspWebhookHandlerTests`/`StartRedirectHandlerTests` (harness เท่านั้น ไม่แตะ assertion)

### Decision

- **service รับ `Session` เป็น input เดียว** (ไม่ใช่ chargeId) — chargeId มาจาก `session.PspExternalChargeId` เสมอ ทำให้ผู้เรียกทั้ง 4 ทางใช้ method เดียวกันได้; ผลข้างเคียง = webhook หา session ก่อน fetch (เดิม fetch ก่อน) และ webhook ของ charge ที่ยังไม่มีแถว session โยน -> 500 -> redeliver (ก่อนหน้านี้ตอบ 200 Ignored ถ้า fetch ยังไม่ Paid) — ถูกต้องกว่าเพราะเป็น race ที่ redelivery แก้ได้
- **service ไม่เปิด transaction เอง** แต่ **save เองเมื่อ transition** — ทำให้ lazy expire ได้ 2-phase ฟรี (confirm = phase 1 UPDATE, `MintAsync` = phase 2 INSERT) ใน `ExecuteInTransactionAsync` เดียว
- **ambiguous = ไม่ catch** (ดูข้างบน) — ตรงกับ design "ไม่ตัดสินอะไร" และคง webhook 500 -> redeliver ไว้
- idempotency context ใหม่ `payment-confirmation` + key `charge:{id}:confirmed` (เดิม `psp-webhook` + `charge:{id}:{evt.Status}`) — key เก่าที่ spent บน prod กลายเป็น inert แต่ enqueue-on-transition กัน event ซ้ำอยู่แล้ว (มี test คุม)
- lazy expire ตรวจ TTL **ก่อน** same-channel resume — ไม่งั้นลูกค้าได้ hosted URL ที่ตายไปแล้ว 24 ชม.
- lazy expire ยอม mint ใหม่เฉพาะ outcome `Expired`/`Failed` (whitelist, fail-closed) — `Paid`/`AlreadyPaid`/`Duplicate`/`AmountMismatch`/`Pending` -> `ConflictException`

### Trap ใหม่ที่เจอ

12. **`TransactionInventoryTests` gate ทุก `.ExecuteInTransactionAsync(` call site ในไฟล์ใต้ `src/`** — เพิ่ม transaction ใหม่ที่ไหนก็ต้องเพิ่มแถวใน `ExpectedExecuteInTransactionAsyncSites` ไม่งั้น Architecture.Tests แดง (T4 `ReleaseOpenSessionCommand`/`CancelOrderCommand` ถ้าเปิด transaction จะเจอแน่)
13. `Payments.Application` (และ `Orders.Application`) **ไม่ได้ reference `Microsoft.Extensions.Logging.Abstractions` มาแต่เดิม** — ต้องเพิ่มใน csproj เอง (CPM มี version ให้แล้ว ใส่ `<PackageReference Include=... />` เปล่า ๆ พอ); `Products.Application` เป็น precedent ของ comment ที่ต้องเขียนกำกับ
14. mutation ที่ใช้ `if (false)` ทำ build แดงด้วย `CS0162 Unreachable code` เพราะ `-warnaserror` — ใช้เงื่อนไขที่ compiler ตรวจไม่ได้ (เช่น พลิก enum ที่เทียบ) แทน
15. `dotnet test tests/Payments.Tests` เต็มชุด ~0.5 วินาที; Integration.Tests filter เดียว ~0.8 วินาที — verify loop ของโมดูลนี้ถูกมาก ไม่ต้องประหยัดการรัน

### สิ่งที่ T4/T5 ต้องรู้

- baseline ตัวเลข test หลัง T3: `Payments.Tests` 189, `Hosts.Tests` 399, `Architecture.Tests` 204, `Orders.Tests` 76, `Checkouts.Tests` 21, `Carts.Tests` 20, `Products.Tests` 137, `Integration.Tests --filter PaymentSessionExpiry` 2
- ไม่มี migration ใหม่ในรอบนี้ — head ยังเป็น `20260802142744_CheckoutOneOpenSessionPerCart` (dev DB `VCentralPay` + probe `VCentralPayT1Probe` อยู่ที่ head นี้)
- T4 `ReleaseOpenSessionCommand` เขียนได้ตรง ๆ: `GetOpenForOrderAsync` -> null = ok; ไม่ null -> `ConfirmAsync(session, ct)` -> `Expired`/`Failed` = ปล่อยผ่าน, ที่เหลือ + exception จาก fetch = `ConflictException` (ตรรกะเดียวกับ whitelist ใน `CreateSessionHandler` — ลอกได้เลย)
- `FakeSessionRepository.OnAdd` + `RecordingLogger<T>` ใน `tests/Payments.Tests/Fakes.cs` ใช้ต่อได้ทันที

## T4 — done

Commit `dca0903` `feat(orders): let a merchant cancel an order only when no charge can land` (ยังไม่ push)

### ไฟล์ที่แตะ

- **NEW** `src/Modules/Payments/Payments.Application/ReleaseOpenSession/ReleaseOpenSessionCommand.cs` + `ReleaseOpenSessionHandler.cs` — `ICommand<Unit>, IMerchantScoped` (ไม่มี `MerchantId` ใน record ตาม design), whitelist `Expired`/`Failed` เท่านั้น, catch ambiguous ที่ call site
- **NEW** `src/Modules/Orders/Orders.Application/CancelOrder.cs` — `CancelOrderCommand(OrderId)` + `CancelOrderResult(OrderId, Status)` + handler (ไฟล์เดียวตาม convention ของ Orders เช่น `ResendOrderSummary.cs`)
- `src/Hosts/Api/Program.cs` — endpoint `POST /orders/{orderId}/cancel` (Release ก่อน แล้วค่อย Cancel) + `using Payments.Application.ReleaseOpenSession;`
- `docs/reference/merchants.md` — เพิ่ม cancel เข้าตาราง funnel endpoints ของ merchant-user
- tests: **NEW** `tests/Payments.Tests/ReleaseOpenSessionHandlerTests.cs` (9), **NEW** `tests/Orders.Tests/CancelOrderHandlerTests.cs` (4), **NEW** `tests/Hosts.Tests/OrderCancelEndpointTests.cs` (7)

### Decision

- **ไม่แตะ `Order.Cancel()` เลย** — domain มีครบอยู่แล้ว (Paid -> throw, Cancelled -> return) ตรงตาม design; งานคือ handler + endpoint + test
- **ไม่มีคำสั่งใหม่ที่เปิด transaction** — release กับ cancel เป็นคนละ unit of work แบบเดียวกับ start/abandon checkout ของ T2 จึงไม่ต้องแตะ `TransactionInventoryTests` (trap #12)
- **ลำดับ release-ก่อน-cancel อยู่ที่ host** ไม่ใช่ใน handler ตัวใดตัวหนึ่ง — สองโมดูลคนละ aggregate, orchestrate ที่ host ตาม precedent `/checkouts/{id}/abandon`
- endpoint คืน `CancelOrderResult(OrderId, Status)` (status เป็น string เหมือน `OrdersListView`) — ไม่ใช่ 204 เพื่อให้ SPA เห็นสถานะปลายทางหลัง idempotent retry
- catch ambiguous = `HttpRequestException or TaskCanceledException or JsonException` **บวก** `&& !cancellationToken.IsCancellationRequested` (client ยกเลิกเอง ≠ PSP ตอบไม่ได้); `InvalidOperationException` จาก adapter (signature verify ล้ม) / vault reveal ล้ม ไม่ต้อง catch — `ProblemDetailsExceptionHandler` map เป็น 409 อยู่แล้ว
- endpoint test ใช้ **fake persistence port + `PaymentConfirmationService` ตัวจริง** โดยเลือกเคส session ที่ไม่มี chargeId ทั้งหมด — ตัดสิน offline ได้ ไม่ต้อง fake vault/adapter/connection เลย

### Trap ใหม่ที่เจอ

16. **`file sealed class` ชื่อซ้ำข้ามไฟล์ได้** — `OrderCancelEndpointTests.cs` มี `TestMerchantUserAuthHandler`/`BoundActor`/`NoOpUnitOfWork` ชื่อเดียวกับใน `MerchantLifecycleEndpointTests.cs` โดยไม่ชน (file-scoped จริง) — ก๊อป pattern ได้ ไม่ต้อง refactor แชร์
17. **`Payments.Domain.Session` ชนกับ `Checkouts.Domain.Session` ใน Hosts.Tests** — ใช้ alias `using PaymentSession = Payments.Domain.Session;` (ไฟล์ T2 ใช้ `CheckoutSession` alias ทางตรงข้าม)
18. mutation ที่ได้ผลชัดที่สุดของ task นี้คือ **สลับลำดับ 2 บรรทัดใน endpoint** (cancel ก่อน release) — จับได้ด้วย assertion `order.Status` ใน test ที่ session ยังสด ไม่ใช่ด้วย status code (status ยัง 409 เหมือนเดิม) — endpoint test ที่ assert แต่ status code จะไม่จับ bug นี้
19. `dotnet test tests/Architecture.Tests` เต็มชุดรอบนี้ใช้ ~15 นาที (build เย็น หลังแตะ `Program.cs`) — บันทึก trap #9 ที่ว่า ~2 วินาที ใช้ได้เฉพาะตอน build อุ่นแล้วจริง ๆ

### สิ่งที่ T5/T8 ต้องรู้

- baseline ตัวเลข test หลัง T4: `Payments.Tests` 198, `Orders.Tests` 80, `Hosts.Tests` 406, `Architecture.Tests` 204, `Checkouts.Tests` 21, `Carts.Tests` 20, `Products.Tests` 137
- ไม่มี migration ใหม่ — head ยังเป็น `20260802142744_CheckoutOneOpenSessionPerCart` (dev DB `VCentralPay` + probe `VCentralPayT1Probe` อยู่ที่ head นี้)
- **T8 `ConfirmPaymentStatusCommand` ลอก catch block ของ `ReleaseOpenSessionHandler` ได้ตรง ๆ** แค่เปลี่ยนปลายทางจาก `ConflictException` เป็นคืน `pending` — และอย่าลืม short-circuit terminal ก่อนเรียก service (REQ-8.13, service ไม่ short-circuit ให้)
- endpoint harness ของ T8 (`{token}/pay`, `{token}/payment-status`) ลอก `OrderCancelEndpointTests` ได้: fake `IOrderRepository`/`ISessionRepository`/`IUnitOfWork`/`IActorContext` + `WebApplicationFactory` ไม่มี DB — แต่ T8 ต้อง fake `IPspAdapter`/`IConnectionRepository`/`IVaultSecretStore` เพิ่มเพราะเส้นนั้นมี chargeId จริง
- `POST /orders/{orderId}/cancel` ใช้ path prefix `/orders/{orderId:guid}` ส่วน T8 ใช้ `/orders/{token}` (string) — route 2 ชุดนี้อยู่ร่วมกันได้เพราะ constraint `:guid` แยกให้ แต่ **ต้องระวังลำดับ/ความกำกวมตอนเพิ่ม `POST /orders/{token}/pay`** (มี `POST /orders/{orderId:guid}/cancel` และ `POST /orders/{orderId:guid}/summary/resend` อยู่ก่อนแล้ว — คนละ segment count จึงไม่ชนกันวันนี้)

## T5 — done

Commit `a0930ce` `feat(products): take sold documents off the page they are sold from` (ยังไม่ push)

### ไฟล์ที่แตะ

- `src/Modules/Products/Products.Domain/Product.cs` — `SoldOrderId: Guid?` + `MarkPaid(DateTime paidDate, Guid orderId)` (first-write: `if (SoldOrderId is null && orderId != Guid.Empty)`)
- `src/Modules/Products/Products.Application/ListProducts.cs` — ยก `paymentStatus` ที่ส่งให้ SP ขึ้นเป็น local แล้วใช้ตัวเดียวกันทั้ง request และ post-filter (`sellable` 3 บรรทัดก่อน `return`)
- `src/Modules/Products/Products.Application/DocumentPaidOnOrderPaidConsumer.cs` — ctor + `ILogger<>`, `LogCritical` ก่อน `MarkPaid`
- `src/Contracts/OrderPaid.cs` — + `Guid OrderId = default` (positional, มี default)
- `src/Modules/Orders/Orders.Application/OrderPaidConsumer.cs` — เติม `order.Id` ตอน enqueue (emitter เดียวในระบบ)
- migration `20260802164627_ProductSoldOrderId` (+ Designer + snapshot) — `AddColumn`/`DropColumn` ล้วน
- `docs/reference/products.md` — แถว `SoldOrderId`, ลายเซ็น `MarkPaid`, บรรทัด post-filter ของ `GET /products`
- tests: `Products.Tests/{ProductTests,ListProductsHandlerTests,DocumentPaidOnOrderPaidConsumerTests}.cs`, `Orders.Tests/OrderPaidConsumerTests.cs`, `BuildingBlocks.Tests/OutboxSerializerTests.cs`, `Hosts.Tests/{InsuranceCheckoutEndToEndTests,WorkerWriteFloorTests}.cs`, **NEW** `Integration.Tests/ProductSearchFilterIntegrationTests.cs`

### Decision

- **ไม่แตะไฟล์ mirror configuration ทั้งคู่** — `Guid?` map เป็น `uniqueidentifier NULL` ด้วย EF convention ทั้ง 2 model (ต่างจากเคส index ของ T2/T3 ที่ต้อง mirror มือ) — คอลัมน์บนตารางเดิมจึงไม่ต้อง GRANT และ `assert-fresh-db.sql` ไม่มี count ที่เกี่ยว
- **double-sell ตัดสินที่ consumer ไม่ใช่ domain** — domain ไม่มี logger; `MarkPaid` รับผิดชอบแค่ first-write, consumer เปรียบเทียบก่อนเรียก
- `OrderPaid.OrderId` เป็น positional param ที่มี default แทน required — payload v1 ที่ค้างใน outbox ตอน deploy ต้อง replay ได้ (มี test คุมด้วย `OutboxSerializer.Options` ตัวจริง ไม่ใช่เดาพฤติกรรม STJ)
- post-filter ตัดสินจาก **string ที่ส่งให้ SP** (`paymentStatus` local) ไม่ใช่จาก `filters.PaymentStatus` ดิบ — ไม่ส่ง filter = ALL = แสดงทุกสถานะ ตาม REQ-5.1 ตรงตัว
- `TotalRows` ไม่ re-query (REQ-5.2) — หน้าที่สั้นกว่า totals เป็นพฤติกรรมที่ตั้งใจ มี test pin ไว้

### Trap ใหม่ที่เจอ

20. **`InsuranceCheckoutEndToEndTests.Paid_order_marks_the_product_PAID_so_it_can_no_longer_be_sold` assert พฤติกรรมที่ REQ-5.1 มาแก้พอดี** — มัน assert ว่าเอกสารที่ขายแล้ว *ยังอยู่* บนหน้าค้นหา default (default = UNPAID) พอใส่ post-filter ก็แดงทันที ต้องแก้ assertion ไม่ใช่แก้โค้ด (แก้เป็น: หน้า UNPAID ว่าง + หน้า `ALL` ยังเจอใบเดิม PAID) — **T6/T8 ที่แตะ read surface ให้เผื่อว่า test เดิมอาจ pin พฤติกรรมเก่าที่ spec สั่งเปลี่ยน**
21. `Product.MarkPaid` มีผู้เรียก 6 จุดข้าม 3 test project (Products.Tests 3, Hosts.Tests 2 ผ่าน consumer ctor, Integration.Tests) — เปลี่ยน signature ของ domain method ให้ build ทั้ง solution ก่อนแล้วอ่าน error list เป็น checklist เร็วกว่า grep
22. Integration.Tests **ไม่ต้อง** ใช้ raw SQL ก็ได้ — csproj มี `Persistence.MerchantRuntime` + `Products.Infrastructure` + `InternalsVisibleTo` อยู่แล้ว จึง `new MerchantRuntimeUnitOfWork(db, NoOpSecurityTelemetry.Instance)` และเดิน handler/consumer ตัวจริงได้ (ProductUpsertIntegrationTests เป็น precedent เดียวเดิม ตอนนี้มี 2)
23. mutation ที่คุ้มที่สุดของ task นี้คือ **พลิก enum ที่เทียบ** (`nameof(UNPAID)` -> `nameof(PAID)`) — ได้ 3 ชั้นแดงพร้อมกัน (unit/E2E/integration) โดยไม่ชน `CS0162` เหมือน `if (false)` (trap #14)

### สิ่งที่ T6/T7/T8 ต้องรู้

- baseline ตัวเลข test หลัง T5: `Products.Tests` 148, `Orders.Tests` 80, `BuildingBlocks.Tests` 44, `Hosts.Tests` 406, `Architecture.Tests` 204, `Payments.Tests` 198, `Checkouts.Tests` 21, `Carts.Tests` 20, `Integration.Tests --filter ProductSearchFilter` 1 / `--filter ProductUpsert` 6
- migration head ตอนนี้ = `20260802164627_ProductSoldOrderId`; dev DB `VCentralPay` + probe `VCentralPayT1Probe` อยู่ที่ head นี้ทั้งคู่ — **T6 เขียน migration มือต่อจากตัวนี้ ระวัง timestamp inversion**
- `Contracts.OrderPaid` ตอนนี้มี 4 field — T6 ที่แก้ `CheckoutConfirmed`/`CustomerOrderNotification` ใช้ pattern เดียวกันได้: positional param ที่มี default + 1 test ใน `OutboxSerializerTests` ยิง JSON รูปเก่าผ่าน `OutboxSerializer.Options` (ที่นั่นคือที่ที่ควรอยู่ ไม่ใช่ test ของโมดูล)
- `ProductSearchFilterIntegrationTests` ใช้ SaleCode `90001` + prefix ต่อรัน — T6 ที่ต้องแตะ `shop.Products` บน integration ให้ลอก pattern นี้ (seed 42 แถว SaleCode 77001 ห้ามแตะ)

## T6 — done

Commit `c5902ff` `feat(checkouts,orders): carry the channel, the discount and the buyer to the order` (ยังไม่ push)

### ชื่อจริงที่ T7 (eligibility) + T8 (summary/pay) ต้องใช้ — อ่านก่อนเขียนโค้ด

```csharp
// enum ช่องทาง — ชื่อ member = wire value ตรงตัว (ไม่มี mapping table)
namespace Checkouts.Domain;
public enum PaymentChannel { CARD = 0, PROMPTPAY_QR = 1, INSTALLMENT = 2 }

// order เก็บ channel เป็น "สตริง wire" ไม่ใช่ enum (Orders ห้ามอ้าง Checkouts)
Orders.Domain.Order.PaymentChannel   // string?  — "CARD" | "PROMPTPAY_QR" | "INSTALLMENT" | null (order ก่อน spec นี้)
Orders.Domain.Order.OrderNo          // string   — "ORD" + ปี พ.ศ. 2 หลัก + running 8 หลัก เช่น ORD6900000001 (varchar(13), unique)
Orders.Domain.Order.CustomerName     // string (NOT NULL, ค่า placeholder = CustomerContact.UnknownName)
Orders.Domain.Order.CustomerPhone    // string (NOT NULL, "" = ไม่มี)
Orders.Domain.Order.CustomerEmail    // string?
Orders.Domain.Order.Customer         // CustomerContact (view เหนือ 3 คอลัมน์ — ไม่ใช่คอลัมน์, ถูก builder.Ignore)
Orders.Domain.Order.NotificationRecipient  // คงไว้ = CustomerPhone ?? CustomerEmail ?? Recipient(payload เก่า)

Checkouts.Domain.Session.Channel / .CustomerName / .CustomerPhone / .CustomerEmail / .Customer
Checkouts.Domain.Session.NotificationRecipient  // **ถูกลบแล้ว** (คอลัมน์ DROP ใน migration) — อย่าอ้างอิง

// SharedKernel (ใช้ร่วมทั้ง Checkouts + Orders + endpoint)
SharedKernel.CustomerContact.Of(name, phone, email)         // validate -> ArgumentException (400)
SharedKernel.CustomerContact.FromStorage(name, phone, email) // ไม่ validate (อ่านจาก DB)
SharedKernel.CustomerContact.Unspecified                     // placeholder = DB DEFAULT
SharedKernel.CustomerContact.UnknownName                     // const "(ไม่ระบุ)"
SharedKernel.CustomerContact.NotificationRecipient           // phone ?? email ?? null
SharedKernel.LineAmounts.Gross(unitPrice, qty) / NormaliseDiscount(discount?, gross) / Net(gross, discount)

// port มินต์เลข (impl = raw SQL NEXT VALUE FOR shop.OrderNoSeq)
Orders.Application.IOrderNoSequence.NextAsync(ct) -> Task<string>
Persistence.MerchantRuntime.Orders.OrderNoSequence.Format(mintedAt, seqValue)  // internal static, ใช้ใน test ได้
```

**T7 (eligibility)**: channel ที่ merchant ส่งมาถูก parse เป็น `Checkouts.Domain.PaymentChannel` ที่ endpoint `POST /checkouts`
(`Program.cs` — `Enum.TryParse<Checkouts.Domain.PaymentChannel>(body.PaymentChannel, out var channel)`) **ก่อน** สร้าง
`StartCheckoutCommand`. จุดแทรก eligibility check คือทันทีหลังบรรทัดนั้น (ก่อนอ่าน cart items) — mapping ไป PSP method
ตาม design: `CARD`->`card`, `PROMPTPAY_QR`->`promptpay`, `INSTALLMENT`->`installment` (ค่า `Session.Method` เดิม, ยังไม่มีใครเขียน mapping นี้ — T7 เป็นคนแรก)

**T8 (summary/pay)**:
- `OrderSummary` record ตอนนี้เป็น `(Guid OrderId, string OrderNo, Money Amount, string Status, Guid? PaymentSessionId, DateTime ExpiresAt, IReadOnlyList<OrderSummaryLine> Lines)` — T8 ยังต้อง **+MerchantId, +PaymentChannel, −PaymentSessionId** ตาม REQ-8.9/8.4 (task นี้ทำแค่ +OrderNo ตาม REQ-7.3)
- `OrderSummaryReader` เป็น raw SQL: query แรกตอนนี้คือ `SELECT TOP 1 Id, MerchantId, OrderNo, AmountAmount, AmountCurrency, Status, PaymentSessionId, SummaryTokenExpiresAt FROM shop.Orders WHERE SummaryToken = {0}` (MerchantId select อยู่แล้วแต่ยังไม่ project ออก record) — T8 แค่เพิ่ม `PaymentChannel` เข้า SELECT + `OrderSummaryRow`
- `OrderSummaryResponse` (wire, `Program.cs`) = `(Guid OrderId, string OrderNo, Money Amount, string Status, Guid? PaymentSessionId, IReadOnlyList<OrderSummaryLineResponse> Lines)` — T8 ถอด `PaymentSessionId` ออก
- `pay` ต้อง 409 เมื่อ `order.PaymentChannel is null` (order ก่อน deploy) — คอลัมน์ nullable ตาม design

### ไฟล์ที่แตะ (59 ไฟล์)

- **NEW** `src/SharedKernel/CustomerContact.cs`, `src/SharedKernel/LineAmounts.cs`
- **NEW** `src/Modules/Checkouts/Checkouts.Domain/PaymentChannel.cs`
- **NEW** `src/Modules/Orders/Orders.Application/IOrderNoSequence.cs`
- **NEW** `src/Persistence/Persistence.MerchantRuntime/Orders/OrderNoSequence.cs` (+ registration + `BypassPrimitiveTests.AllowedPorts`)
- domain: `Checkouts.Domain/{Session,Items/Item,Items/CheckoutItemInput}.cs`, `Orders.Domain/{Order,Items/Item,Items/OrderItemInput}.cs`
- application: `Checkouts.Application/{StartCheckout,ConfirmCheckout}.cs`, `Orders.Application/{CheckoutConfirmedConsumer,CreateOrderCommand,ResendOrderSummary,GetOrders,GetOrderDetail,IOrderRepository,IOrderSummaryReader}.cs`
- contracts: `src/Contracts/{CheckoutConfirmed,CustomerOrderNotification}.cs`
- EF config 4 คู่ mirror: `Checkouts.Infrastructure/{SessionConfiguration,Items/ItemConfiguration}.cs` + `Persistence.MerchantRuntime/Checkouts/*`, `Orders.Infrastructure/{OrderConfiguration,Items/ItemConfiguration}.cs` + `Persistence.MerchantRuntime/Orders/*`
- `Persistence.MerchantRuntime/Orders/{OrderRepository,OrderSummaryReader}.cs`
- migration `20260802172209_CheckoutOrderEnrichment` (+ Designer + snapshot) — เขียนมือทั้ง `Up`/`Down`
- host: `src/Hosts/Api/Program.cs` (`POST /checkouts` body + validate, `GET /orders` filter, `OrderSummaryResponse` +orderNo)
- docs: `docs/reference/merchants.md` (body ของ `POST /checkouts`, filter ของ `GET /orders`, รูปแบบ OrderNo)
- tests: **NEW** `SharedKernel.Tests/{CustomerContactTests,LineAmountsTests}.cs`, `Checkouts.Tests/CheckoutEnrichmentTests.cs`, `Orders.Tests/OrderEnrichmentTests.cs`, `Hosts.Tests/StubOrderNoSequence.cs`, `Integration.Tests/OrderNoSequenceIntegrationTests.cs`; แก้ `Orders.Tests/Fakes.cs` (+`FakeOrderNoSequence`), `Hosts.Tests/{MerchantLifecycleEndpointTests,OrderCancelEndpointTests}.cs` และ call site `Order.Create`/`Session.Start` อีก 12 ไฟล์

### Decision

- **`CustomerContact` + `LineAmounts` อยู่ `SharedKernel`** ไม่ใช่ซ้ำ 2 domain — Checkouts กับ Orders ถือคอลัมน์ชุดเดียวกันและอ้างกันไม่ได้; แยก = กฎ validate/money 2 ชุดที่ drift ได้ (เส้น money)
- **`Order.Create(..., string orderNo, ...)` เป็น parameter บังคับ** — คอลัมน์ NOT NULL, aggregate ที่ไม่มีเลข = commit ไม่ได้; ราคาที่จ่ายคือแก้ call site 28 จุดใน test (mechanical)
- **channel เก็บเป็น enum ฝั่ง Checkouts, เป็น string ฝั่ง Orders** — ตาม precedent `ProductGroup`/`DocumentType` (โมดูลปลายทางถือ wire value เป็นสตริง ไม่อ้าง enum ข้ามโมดูล)
- **`Session.Channel` map ไปคอลัมน์ชื่อ `PaymentChannel`** ผ่าน `HasColumnName` — property ชื่อ `PaymentChannel` จะชนกับชื่อ type ในตัวคลาสเอง
- **discount บน contract แตกเป็น 2 scalar** (`DiscountAmount`/`DiscountCurrency`) เพราะ `Money` เป็น struct ที่ `default` ไม่ถูกต้อง (Currency null) จึงไม่มี default ปลอดภัยสำหรับ positional record ที่ต้อง back-compat; **discount 0 รับสกุลจากบรรทัด** ไม่ใช่ 'THB' ตายตัว (ไม่งั้น line USD + default THB = currency mismatch ตอน replay)
- **`amount` ใน body ใช้ตรวจเท่านั้น** ไม่เคยตั้งราคา — server คิด `Σ(gross − discount)` เสมอ
- **`filters=orderNo:eq:` ใน design = SFS JSON DSL ของ repo** (`SfsQueryParser` เดิม) ไม่ประดิษฐ์ format ที่สอง; ไม่ใส่ `SfsQueryParamsMarker` เพราะยังไม่รับ page/limit/sort/search

### Trap ใหม่ที่เจอ

24. **`ef migrations add` อ่านการเปลี่ยนของ CheckoutSessions เป็น `RenameColumn NotificationRecipient -> CustomerEmail`** — ถ้าปล่อยไว้ เบอร์โทรทุกแถวจะกลายเป็นอีเมลเงียบ ๆ (trap #22 ของ products-sp-53 รอบใหม่ คนละตาราง). วิธีที่ใช้: ให้ scaffold สร้าง Designer + snapshot ให้ (ถูกต้อง) แล้ว **เขียนทับเฉพาะ `Up`/`Down`** ด้วยมือ — ได้ทั้ง lineage ที่ถูกและ operation ที่ถูก
25. **DB DEFAULT ที่ migration ใส่เอง EF ไม่รู้จัก** — `Down()` ต้อง `DROP CONSTRAINT DF_*` ก่อน `DROP COLUMN` เอง; migration ในอนาคตที่จะลบคอลัมน์เหล่านี้ก็ต้องทำเหมือนกัน (scaffold จะ emit `DropColumn` เปล่า ๆ แล้วพัง)
26. **`sqlcmd -i` กับสคริปต์ที่แตะตารางมี filtered index ต้องมี `SET QUOTED_IDENTIFIER ON;` + `GO` นำหน้า** ไม่งั้น `Msg 1934 ... INSERT failed because the following SET options have incorrect settings: 'QUOTED_IDENTIFIER'` (เจอตอน seed probe DB)
27. **`sqlcmd` ตัดคอลัมน์กว้างจนอ่านไม่ออก** — ใช้ `-W -s"|"` ทุกครั้งที่ verify migration ไม่งั้น output 30KB+ ต่อ query เดียว
28. **`Money` บน wire เป็น string ไม่ใช่ number** (`MoneyJsonConverter`) — JSON ตัวอย่างใน test back-compat ต้องเขียน `{"amount":"15000.0000","currency":"THB"}` ไม่งั้น `JsonException: Money.amount must be a JSON string`
29. **test ที่ persist order ลง DB จริง/SQLite ต้องมี OrderNo ไม่ซ้ำ** — `IX_Orders_OrderNo` unique ทำให้ helper ที่ hardcode เลขเดียวแล้วถูกเรียกหลายครั้งต่อ 1 database พังทันที (แก้ด้วย `NextOrderNo()` counter ต่อคลาส ใน 5 ไฟล์)
30. `Integration.Tests` เต็มชุดมี **3 test แดงที่มีอยู่ก่อนงานนี้** (`SpDocumentContractTests` coverage window) — พิสูจน์ด้วย `git stash` + รันซ้ำ; อย่าไล่แก้คิดว่าตัวเองทำพัง
31. `dotnet test tests/Architecture.Tests` เต็มชุดรอบนี้ = **1 นาที 15 วินาที** (206 test, build อุ่น) — ระหว่าง trap #9 (~2 วิ) กับ #19 (~15 นาที); ตัวแปรคือ build เย็น/อุ่น ไม่ใช่จำนวน test

### สิ่งที่ T7/T8 ต้องรู้ (นอกเหนือจากชื่อ field ด้านบน)

- baseline ตัวเลข test หลัง T6 (+ verifier fix): `Checkouts.Tests` 34, `Orders.Tests` 93, `SharedKernel.Tests` 75, `BuildingBlocks.Tests` 46, `Hosts.Tests` 430, `Architecture.Tests` 206, `Carts.Tests` 20, `Payments.Tests` 198, `Products.Tests` 148, `Integration.Tests` 122 passed / 3 pre-existing failed
- migration head ตอนนี้ = `20260802172209_CheckoutOrderEnrichment`; dev DB `VCentralPay` อยู่ที่ head นี้แล้ว, probe `VCentralPayT6Probe` (มีข้อมูลทดสอบ) ก็อยู่ที่ head นี้; **`VCentralPayT1Probe` ยังอยู่ที่ head เก่า** (`ProductSoldOrderId`) — ถ้า T7/T8 จะใช้ probe ให้ใช้ `VCentralPayT6Probe`
- `POST /checkouts` body เปลี่ยนรูปแล้ว: `recipient` หายไป, `paymentChannel` + `customer{name,phone,email}` บังคับ, `amount` และ `insuredPersons[].discount` optional — endpoint test ทุกตัวที่ยิง `/api/v1/checkouts` ต้องใช้ helper `StartCheckoutBody(cart, product, channel, discount)` ใน `MerchantLifecycleEndpointTests`
- `CheckoutConfirmedConsumer` / `CreateOrderHandler` ctor รับ `IOrderNoSequence` เพิ่มแล้ว — harness ที่ประกอบเองต้องส่งเข้าไป (`FakeOrderNoSequence` ใน `Orders.Tests/Fakes.cs`, `StubOrderNoSequence` ใน `Hosts.Tests`)
- docs ที่ยัง stale (ตั้งใจแก้รอบเดียวตอนปิด PR ตามแผน T2): `docs/reference/platform-modules.md`, `docs/reference/src-structure.md`

## T6 — verifier fix

Verifier อิสระตรวจ T6 ผ่าน 8/9 ข้อ — fail ข้อ 1 (Evidence เคลม `scripts/check-rename-identifiers.sh` OK ทั้งที่รันจริงได้ exit 1 / 11 hit) + ชี้ช่องโหว่ REQ-6.2 ที่ endpoint. โค้ด/migration ที่เหลือของ T6 ไม่ถูกแตะ

### ที่แก้ (commit `59b26d3` ต่อจาก `c5902ff`)

- `tests/Checkouts.Tests/CheckoutEnrichmentTests.cs` — helper `Line(...)` -> `Insured(...)` (11 call site) ปลด rename gate
- `src/Hosts/Api/Program.cs` (REQ-6.2) — guard channel เดิม `Enum.TryParse` + `Enum.IsDefined` รับค่านอกสัญญา; เปลี่ยนเป็น `Enum.GetNames<PaymentChannel>().Contains(body.PaymentChannel, StringComparer.Ordinal)` ก่อน แล้วค่อย `TryParse`
- `tests/Hosts.Tests/MerchantLifecycleEndpointTests.cs` — `Starting_a_checkout_with_an_unsupported_channel_is_400` +3 InlineData: `"0"`, `"2"`, `"CARD,INSTALLMENT"`
- `.ai/specs/purchase-flow-completion/tasks.md` — แก้บรรทัด gate ให้ตรงความจริง (บันทึกว่ารอบแรกแดง + สาเหตุ), Hosts.Tests 427 -> 430, MerchantLifecycle 29 -> 32, เพิ่มบรรทัด verifier fix

### Trap ใหม่ที่เจอ

32. **`scripts/check-rename-identifiers.sh` อ่านไฟล์จาก `git ls-files`** (`scripts/check_rename_identifiers.py:162`) — ไฟล์ใหม่ที่ยังไม่ `git add` จะไม่ถูกสแกน gate เลย **ผ่านแบบหลอก ๆ** แล้วไปแดงบน CI (`.github/workflows/ci.yml:64`). ต้องรัน gate นี้ **หลัง `git add`** เสมอ
33. **`Line` เป็น retired token ที่ชนง่ายที่สุดในลิสต์** (จาก OrderLine->OrderItem) — ห้ามตั้งชื่อ local/helper/parameter ว่า `Line` แม้ในไฟล์ test; ลิสต์เต็มอยู่หัวไฟล์ `scripts/check_rename_identifiers.py` (`MerchantUser`, `PaymentSession`, `CartItem`, `CheckoutSession`, `MasterData`, `Line`, `CheckoutLineInput` ฯลฯ)
34. **`Enum.TryParse` + `Enum.IsDefined` ไม่พอสำหรับ wire enum** — TryParse รับตัวเลขล้วน (`"0"`) และ comma list (`"CARD,INSTALLMENT"`) และ IsDefined ก็เป็น true เพราะค่าที่ได้อยู่ในชุด; enum ที่ wire value = ชื่อ member ต้องเทียบกับ `Enum.GetNames<T>()` (Ordinal) — **T7 แทรก eligibility check ตรงบรรทัดนี้พอดี ตอนนี้ปิดช่องแล้ว**

## T6 — verifier round 2 + lead fix (ปิดสมบูรณ์)

verifier รอบ 2 ตัดสิน fail เพราะ REQ-7.3 surface 2/3 ไม่มีด่าน (โค้ดถูกแต่ถอด OrderNo ออกได้โดย test ไม่แดง) — lead แก้เองใน commit `1f04693`:
- `tests/Orders.Tests/GetOrderDetailTests.cs` + assertion `OrderNo` ใน test detail เดิม
- ไฟล์ใหม่ `tests/Hosts.Tests/OrderSummaryEndpointTests.cs` — fake `IOrderSummaryReader` ยิง `GET /api/v1/orders/{token}/summary` จริง assert property `orderNo` บน JSON wire (pattern เดียวกับ OrderCancelEndpointTests; route anonymous ไม่ต้อง fake auth)
- `tests/Integration.Tests/OrderSummaryReaderIntegrationTests.cs` — sync SQL สำเนามือทั้ง 2 query ให้ตรง `OrderSummaryReader` จริง column-for-column (query แรก 8 คอลัมน์, query สอง 4 คอลัมน์รวม ProductId, FieldCount = 4)
- tasks.md Evidence T6 อัปเดตแล้ว (Hosts.Tests เต็มชุดตอนนี้ = 431)

ผล: Orders.Tests 93/0, Hosts.Tests 431/0, Integration --filter OrderSummaryReader 1/0, rename gate OK — T6 ถือว่าผ่านสมบูรณ์ T7 เริ่มได้เลย
**probe DB ค้างตอนนี้ 3 ตัว: VCentralPayT1Probe, VCentralPayFreshT1, VCentralPayT6Probe — อย่ายุ่ง อย่า DROP**

## T7 — done

Commit `f864fc6` `feat(payments): let a merchant pick any channel 2C2P can actually charge` (ยังไม่ push)

### OPS STEP บังคับ — ต้องเข้า PR body (lead)

connection ที่มีอยู่แล้วบนทุก env (dev/uat/prod) ยังถือ `EnabledMethods` ค่าเดิม ต้องอัปเดตเอง มิฉะนั้น
merchant เลือก PromptPay/ผ่อนชำระไม่ได้ (400 ตั้งแต่ `POST /checkouts`) และถ้ามี order เก่าค้างก็ยัง 409 ที่
`CreateSessionHandler.EnsureEligible`:

```sql
UPDATE txn.PspConnections
SET    EnabledMethods = 'card,promptpay,installment'
WHERE  Psp = 0                     -- 2C2P (Omise ยังรองรับ card อย่างเดียว ห้ามขยาย)
  AND  MerchantId = '<merchant-id>';
```

ค่าใน `EnabledMethods` ตามธรรมเนียมของ seed คือ subset ของ `merch.Merchants.EnabledChannels` ของ merchant
นั้น — ถ้าจะเปิดช่องทางที่ merchant ยังไม่มีใน `EnabledChannels` ให้ขยาย 2 ที่พร้อมกัน

### ไฟล์ที่แตะ

- `src/Modules/Payments/Payments.Infrastructure/Psp/TwoCTwoPAdapter.cs` — `SupportedMethods` = 3 method, `PaymentChannelFor` +`promptpay`→`QR` +`installment`→`IPP` (ข้อความ exception ตัดคำว่า "card only" ออก)
- `src/Modules/Payments/Payments.Domain/PaymentMethods.cs` — **NEW** `ForChannel(string channel)` = จุดเดียวที่ channel wire value กลายเป็น method code
- **NEW** `src/Modules/Payments/Payments.Application/MethodPayable/{MethodPayableQuery,MethodPayableHandler}.cs` — `MethodPayableQuery(Guid MerchantId, string Method) : IQuery<bool>, IMerchantScoped`
- `src/Hosts/Api/Program.cs` — `POST /checkouts` เพิ่ม eligibility check ถัดจาก guard channel ของ T6 + alias `using PaymentMethods = Payments.Domain.PaymentMethods;`
- `docker/bootstrap/seed-demo.sql` — comment เหนือ `txn.PspConnections` (บอกว่าคอลัมน์นี้คือสิ่งที่ merchant เห็นตอน checkout + ชี้ ops step) และ comment เหนือ `txn.PaymentSessions` (เลิกอ้างว่า adapter รองรับ card อย่างเดียว)
- docs: `docs/reference/merchants.md` (สัญญา `paymentChannel` + 400), `payment-orchestration-modules.md` (2 จุดที่เขียนว่า `SupportedMethods` ทั้ง 2 adapter = `{ card }`), `platform-modules.md` (as-built 3 ช่องทาง + gap ข้อ 8 เหลือเฉพาะ Omise)
- tests: `tests/Payments.Tests/Psp/TwoCTwoPAdapterTests.cs`, `tests/Payments.Tests/PaymentMethodsTests.cs`, **NEW** `tests/Payments.Tests/MethodPayableHandlerTests.cs`, `tests/Hosts.Tests/MerchantLifecycleEndpointTests.cs`

### Decision

- **mapping channel→method อยู่ที่ `Payments.Domain.PaymentMethods.ForChannel`** ไม่ใช่ข้าง `Checkouts.Domain.PaymentChannel` — `Checkouts.Domain` reference ได้แค่ `SharedKernel` เห็น `PaymentMethods` ไม่ได้ ถ้าวางฝั่งนั้นต้อง hardcode literal `"card"`/`"promptpay"`/`"installment"` ซ้ำ = drift บนเส้น money
- **eligibility เป็น mediator query คืน `bool`** (`MethodPayableQuery`) ตาม design "host-layer query ไปยัง Payments" — endpoint ต้องได้ 400, ส่วน `CreateSessionHandler` ยังถือกฎเดิมที่ throw 409 ไว้ไม่แตะ (2 ที่ ถามคำถามเดียวกัน แต่ต่าง audience/status code — มี test คุมทั้งคู่)
- query ถาม connection ของ **`Code.TwoCTwoP` ตายตัว** — ลูกค้าถูก redirect ไป 2C2P เท่านั้น (ตาม design "การเลือก PSP connection ฝั่งลูกค้า"); Omise ยังเป็น `{ card }` ห้ามขยาย
- **ไม่มี connection เลย = ไม่ payable** (400 เท่ากับ channel ที่ปิด) ไม่ throw — สำหรับลูกค้าผลลัพธ์เดียวกัน
- **ไม่แก้ค่า `EnabledMethods` ใน seed-demo**: vPrivilege เปิดครบ 3 อยู่แล้ว, vCommerce/vSouvenir แคบโดยเจตนาของ spec `demo-seed-data` (invariant `EnabledMethods` ⊆ `merch.Merchants.EnabledChannels`) — ขยายต้องแก้ `EnabledChannels` ตาม = แก้ data design ของอีก spec และทำให้ไม่เหลือข้อมูล demo ของเคส 400; แก้ comment + ops step แทน (ลง deviations แล้ว)

### Trap ใหม่ที่เจอ

35. **`using Payments.Domain;` ใน `Program.cs` ทำ build แดงทันที** — `Payments.Domain.SessionStatus` ชนกับ `Admins.Domain.Users.SessionStatus` (CS0104 x3 + CS1503 + CS8422 ที่ static local function ไกลออกไป 1000 บรรทัด อ่านเหมือนคนละเรื่อง) — ใช้ alias `using PaymentMethods = Payments.Domain.PaymentMethods;` แทน (T8 ที่ต้องใช้ `PaymentMethods`/`Session` ฝั่ง Payments เจอเรื่องเดียวกัน)
36. **endpoint test ทุกตัวที่ยิง `POST /api/v1/checkouts` ต้องมี `IConnectionRepository` แล้ว** — `CartFactory` รับพารามิเตอร์ `enabledMethods` (default `"card,promptpay,installment"`, `null` = ไม่มี connection) และ register `FakeConnections`; ถ้าไม่ register จะไปโดน repository ตัวจริงที่ต้องมี DB
37. **eligibility ที่ endpoint intersect กับ adapter ตัวจริง** — `CartFactory` ไม่ได้ fake `IPspAdapterFactory` เพราะ `SupportedMethods` ไม่ต้องยิง HTTP; ผลคือหด `TwoCTwoPAdapter.SupportedMethods` เมื่อไรก็ตาม endpoint test ของ channel จะแดงทันที (พิสูจน์แล้วเป็น regression proof ข้อ 3)
38. `git add -A` ดูด `HANDOFF.md` เข้า index ด้วย (ไฟล์ untracked ไฟล์เดียวที่ห้าม commit) — ต้อง `git restore --staged .ai/specs/purchase-flow-completion/HANDOFF.md` ก่อน commit เสมอ

### สิ่งที่ T8 ต้องรู้

- baseline ตัวเลข test หลัง T7: `Payments.Tests` 218, `Hosts.Tests` 434, `Checkouts.Tests` 34, `Carts.Tests` 20, `Orders.Tests` 93, `Products.Tests` 148, `Merchants.Tests` 134, `SharedKernel.Tests` 75, `BuildingBlocks.Tests` 46, `Architecture.Tests` 206 — เขียวทั้งหมด; `Integration.Tests` ไม่ได้รันรอบนี้ (ไม่แตะ EF/SQL)
- **จุดที่ T8 ต้องใช้**: `PaymentMethods.ForChannel(order.PaymentChannel)` คือทางเดียวที่แปลง `Orders.Domain.Order.PaymentChannel` (string `"CARD"`/`"PROMPTPAY_QR"`/`"INSTALLMENT"`) เป็น `method` ที่ส่งเข้า `CreateSessionCommand(orderId, merchantId, method, Code.TwoCTwoP)` — **ห้ามเขียน switch ใหม่**; `order.PaymentChannel is null` (order ก่อน deploy) ต้องเช็คก่อนเรียก แล้วตอบ 409 ตาม design (ForChannel จะโยน `ArgumentNullException` = 400 ซึ่งไม่ใช่ contract ที่ต้องการ)
- `MethodPayableQuery` มีไว้สำหรับ **checkout** เท่านั้น — เส้น pay ของลูกค้าไม่ต้องเรียกซ้ำ ปล่อยให้ `CreateSessionHandler` ตัดสิน (409 generic ตาม design) เพราะ eligibility อาจเปลี่ยนหลัง checkout และคำตอบตอนจ่ายคือคำตอบที่นับ
- ไม่มี migration ใหม่ — head ยังเป็น `20260802172209_CheckoutOrderEnrichment` (dev DB `VCentralPay` + probe `VCentralPayT6Probe` อยู่ที่ head นี้)
- ก่อนปิด T8: รัน `scripts/spec-trace.sh purchase-flow-completion` (task สุดท้ายของ spec) + `dotnet test tests/Integration.Tests` (มี 3 test แดงมาก่อนงานนี้ตาม trap #30)

## T8 — done (task สุดท้ายของ spec)

Commit `40fba98` `feat(payments,orders): let the customer pay the order from their own link` (ยังไม่ push)

### ไฟล์ที่แตะ (21 ไฟล์)

- **NEW** `src/Modules/Payments/Payments.Application/ConfirmPaymentStatus/{ConfirmPaymentStatusCommand,ConfirmPaymentStatusHandler}.cs` — `PaymentStatusResult` enum (Pending/Paid/Failed/Cancelled) + handler
- **NEW** `src/Hosts/Api/Customers/PaymentRateLimiting.cs` — policy `customer-payment` (sliding 10/60s/IP, `namespace Api.Customers`)
- `src/Modules/Payments/Payments.Application/Ports/IPayableOrderReader.cs` — enum ใหม่ `PayableOrderStatus`; `PayableOrder(Guid, Money, PayableOrderStatus)` + property `IsAwaitingPayment` derive
- `src/Persistence/Persistence.MerchantRuntime/Payments/PayableOrderReader.cs` — `Map(OrderStatus)` total (default = throw)
- `src/Modules/Payments/Payments.Application/GetSession/GetSessionHandler.cs` — `NotFoundException` (REQ-8.8)
- `src/Modules/Orders/Orders.Application/IOrderSummaryReader.cs` — `OrderSummary(OrderId, **MerchantId**, OrderNo, Amount, Status, **PaymentChannel**, ExpiresAt, Lines)` (−`PaymentSessionId`)
- `src/Persistence/Persistence.MerchantRuntime/Orders/OrderSummaryReader.cs` — SELECT `PaymentChannel` แทน `PaymentSessionId`, project `MerchantId`
- `src/Hosts/Api/Program.cs` — 3 endpoint ใหม่ (`POST /orders/{token}/pay`, `POST /orders/{token}/payment-status`, `GET /payments/sessions/{paymentSessionId:guid}`), `AddCustomerPaymentRateLimiter()`, `OrderSummaryResponse` −`PaymentSessionId`, record ใหม่ `PaymentStatusResponse`, alias 3 ตัว
- docs: `docs/reference/{orders,merchants,platform-modules}.md`
- tests: **NEW** `Payments.Tests/{ConfirmPaymentStatusHandlerTests,GetSessionHandlerTests}.cs`, **NEW** `Hosts.Tests/CustomerPaymentEndpointTests.cs`; แก้ `Hosts.Tests/{OrderCancelEndpointTests,OrderSummaryEndpointTests}.cs`, `Integration.Tests/OrderSummaryReaderIntegrationTests.cs`, `Payments.Tests/{CreateSessionHandlerTests,StartRedirectHandlerTests}.cs`

### Decision

- **`PayableOrder` ถือ enum แทน bool**: REQ-8.12 ต้องแยก Paid ออกจาก Cancelled ซึ่ง `IsAwaitingPayment` เดิมทำไม่ได้ และ Payments อ้าง `Orders.Domain.OrderStatus` ไม่ได้ — จึงมี `PayableOrderStatus` (seam-local twin) + reader map แบบ total; `IsAwaitingPayment` เหลือเป็น derived property ⇒ `CreateSessionHandler` ไม่ถูกแตะเลย
- **order status ตัดสินที่ handler ไม่ใช่ที่ host** สำหรับ `payment-status` (design table สั่ง) แต่ **`pay` ตัดสินที่ host** เพราะต้องแยก 404 (Cancelled) ออกจาก 409 (Paid) ซึ่ง `CreateSessionHandler` ให้ 409 ทั้งคู่ — host อ่านจาก `summary.Status` ที่โหลดมาแล้วอยู่ดี ไม่ query ซ้ำ
- **resume (REQ-8.10) ไม่มีโค้ดใหม่จริง ๆ** ตามที่ design ระบุ — `CreateSessionHandler` คืน session เดิม + `StartRedirectHandler` คืน URL เดิม; test `Paying_twice_resumes_the_same_charge` assert 3 อย่าง (URL เท่ากัน, 1 session, adapter charge 1 ครั้ง)
- **`StartRedirectResponse` ถูกใช้ซ้ำเป็น response ของ `pay`** (รูป `{redirectUrl}` เหมือนกันเป๊ะ) ไม่สร้าง record ใหม่
- **`MethodPayableQuery` ไม่ถูกเรียกในเส้น pay** ตามที่ T7 สั่ง — `CreateSessionHandler` ตัดสิน 409 เอง
- `GET /payments/sessions/{id}` gate แค่ `merchant-user` ไม่มี iam key (แบบเดียวกับ cancel ของ T4)

### Trap ใหม่ที่เจอ

39. **`using Payments.Application.GetSession;` ทำ build แดงแบบเดียวกับ trap #35** — `SessionView` ชนกับ `Admins.Application.Users.SessionView` (CS0104 x2 + cascade CS0411/CS8422 ที่บรรทัดไกลออกไปอ่านเหมือนคนละเรื่อง) ใช้ alias `using GetPaymentSessionQuery = ...GetSessionQuery;` + `using PaymentSessionView = ...SessionView;` แทน (ชื่อ alias ยาวกว่า token `PaymentSession` จึงไม่ชน rename gate — gate เป็น word-bounded)
40. **endpoint ที่ bind actor เองห้าม register fake `IActorContext` ใน test** — ต่างจาก pattern ของ T2/T4: ถ้าใส่ `BoundActor` เข้าไป จะกลบข้อพิสูจน์ว่า host เรียก `actorScope.Begin(summary.MerchantId)` จริง (`AmbientActor` ถูก consult ก่อนโดย `IActorContext` ตัวจริงอยู่แล้ว) — `CustomerPaymentEndpointTests` จึงไม่ override `IActorContext` เลย
41. เส้น pay/payment-status ที่เดิน `PaymentConfirmationService` ตัวจริงใน Hosts.Tests ต้อง fake **7 port**: `IOrderSummaryReader`, `IPayableOrderReader`, `ISessionRepository`, `IConnectionRepository`, `IPspAdapterFactory`, `IVaultSecretStore`, `IIdempotencyStore`, `IOutbox`, `IUnitOfWork` (idempotency/outbox ตัวจริงต้องมี DB) — ดู `file sealed class` หัวไฟล์ `CustomerPaymentEndpointTests.cs` ก๊อปได้
42. **mutation ที่คุ้มที่สุดของ task นี้คือถอด short-circuit** (2 บรรทัด order status) — จับได้ทั้ง status ที่คืนและ `adapter.Fetched`; assertion `Fetched == 0` / `Vault.Reveals == 0` คือชั้นเดียวที่พิสูจน์ "ไม่ fetch เมื่อ terminal" ได้จริง (ค่าที่คืนเหมือนกันได้ทั้งสองทาง)

### Known gaps ทั้ง spec (สำหรับ PR body ของ lead)

1. **ops step บังคับจาก T7**: `UPDATE txn.PspConnections SET EnabledMethods = 'card,promptpay,installment' WHERE Psp = 0 AND MerchantId = '<merchant-id>';` ทุก env — ไม่ทำ = merchant เลือก PromptPay/ผ่อนได้แต่โดน 400 ที่ checkout และ 409 ที่ `CreateSessionHandler.EnsureEligible` (รายละเอียด + invariant `EnabledMethods` ⊆ `merch.Merchants.EnabledChannels` ดู section "## T7 — done")
2. **manual E2E บน 2C2P sandbox ยังไม่ได้ทำ** (T8 deviation 2) — ไม่มี credential/browser ใน environment นี้ ยังไม่พิสูจน์ redirect ปลายทาง + การกลับมาที่ `payment-status` ด้วยเงินจริง; channel code `CC`/`QR`/`IPP` ของ PGW v4.3 ก็ยังไม่ verify กับ sandbox (design ระบุให้ verify ตอน implement)
3. **probe DB ค้าง 3 ตัวบน :11433**: `VCentralPayT1Probe`, `VCentralPayFreshT1`, `VCentralPayT6Probe` — `destructive-guard` block `DROP DATABASE` ต้องให้ user ยืนยันเอง
4. **`Integration.Tests` มี 3 test แดงมาก่อน spec นี้** (`SpDocumentContractTests` coverage window, trap #30) — รอบนี้ยังแดง 3 เท่าเดิม (122 passed / 3 failed) ไม่ใช่ของใหม่
5. **`payment-status` ตอบ `pending` แทน `failed`** เมื่อ session ล่าสุดถูก mark Failed/Expired ไปแล้ว (ไม่มี open session) — ตรงตาม REQ-8.12 ตามตัวอักษร แต่ต่างจากที่ SPA อาจคาด; ทางขยาย = read "session ล่าสุด" (มี `ponytail:` comment กำกับในโค้ด)
6. **token TTL 72 ชม. > session TTL 24 ชม.** — ลิงก์ยังใช้ได้แต่ session ตายไปแล้ว (design ยอมรับไว้แล้ว, ทางแก้ = resend); `pay` จะ lazy-expire แล้วเปิดใบใหม่ให้อัตโนมัติ
7. docs ที่ยัง stale (ไม่ได้ไล่ครบในรอบนี้ ตั้งใจตาม T2): `docs/reference/src-structure.md` ยังไม่มี abandon/cancel/pay/payment-status ในตาราง endpoint; `docs/reference/orders.md` ตาราง endpoint ยังมี line number เก่าของ `Program.cs` (แถวใหม่ใส่ `—` แทนเลขที่จะ drift)
8. การส่ง SMS/อีเมลจริงยังเป็น logging stub (ระบุใน requirements Edge Cases แล้ว)

### สถานะ baseline ตอนปิด spec

`Payments.Tests` 234, `Hosts.Tests` 453, `Orders.Tests` 93, `Checkouts.Tests` 34, `Carts.Tests` 20, `Products.Tests` 148, `Merchants.Tests` 134, `SharedKernel.Tests` 75, `BuildingBlocks.Tests` 46, `Architecture.Tests` 206 — เขียวทั้งหมด; `Integration.Tests` 122 passed / 3 pre-existing failed; `dotnet build -warnaserror` 64 projects 0/0; `scripts/check-rename-identifiers.sh` OK; `scripts/spec-trace.sh purchase-flow-completion` OK (54/54 criteria)

ไม่มี migration ใหม่ใน T8 — head ยังเป็น `20260802172209_CheckoutOrderEnrichment`

## T8 — verifier fix

Verifier อิสระตรวจ T8 ผ่าน 7/8 ข้อ — fail ข้อ 8 (test coverage): REQ-8.3 เป็นเกณฑ์เดียวใน REQ-8ที่ไม่มีด่าน
test เลย ถอด `.RequireRateLimiting(PaymentRateLimiting.PolicyName)` ออกจาก `Program.cs:1025` (pay) และ
`:1055` (payment-status) ได้โดยไม่มี test ไหนแดง (17 test ใน `CustomerPaymentEndpointTests` พิสูจน์แค่ว่า
policy ถูก register — ไม่ register แล้ว middleware จะ throw -> 500 — ไม่ได้พิสูจน์ว่า limiter ปฏิเสธจริง)
failure mode เดียวกับที่ตี T6 ตกรอบ 2 โค้ด T8 เองถูกต้องครบ ไม่ถูกแตะในรอบนี้

Commit `8cfba51` `test(api): pin the customer payment rate limit to both anonymous routes` (ยังไม่ push)

### ที่แก้

- `tests/Hosts.Tests/WebHardeningTests.cs` — คลาสใหม่ `CustomerPaymentRateLimitTests` (`[Theory]` 2 เส้น:
  `pay` / `payment-status`) วางถัดจาก `WebhookRateLimitTests` ในไฟล์เดียวกัน **เพราะ `HardeningFactory` และ
  `FactoryExtensions.WithFastFailDatabase` เป็น `file`-scoped** — ไฟล์ใหม่ใช้ต่อไม่ได้ ต้องก๊อปทั้งชุด
- `.ai/specs/purchase-flow-completion/tasks.md` — เพิ่ม evidence 1 บรรทัด (test REQ-8.3) + regression proof
  1 บรรทัด, อัปเดต Hosts.Tests เต็มชุด 453 -> 455 และ filter `MerchantLifecycle|CustomerPayment` 52 -> 54

### วิธีที่ใช้

- ยิง 30 request พร้อมกัน (`Task.WhenAll`) เข้า route เดียว — ทุกใบใช้ partition เดียวกัน (loopback IP) แล้ว
  assert มี 429 + `Retry-After` > 0 (มาจาก `options.OnRejected` ที่ `Api/Webhooks/RateLimiting.cs:46` ซึ่ง
  config ครั้งเดียวและใช้ร่วมกันทุก policy — fallback 2 วินาที)
- **เลข 30 เลือกมาให้คร่อมโควตา 2 ตัว**: มากกว่า `customer-payment` (10/60s) แต่น้อยกว่า `psp-webhook`
  (60/10s) ⇒ ถ้าใครสลับไปใส่ policy ของ webhook บน route นี้ test ก็แดงด้วย ไม่ต้องเขียน assertion แยก
- `WithFastFailDatabase()` ทำให้ request ที่ผ่านด่านเข้าไปโดน `IOrderSummaryReader` แล้วตายทันที (Connect
  Timeout=1) — เส้น 429 ตัดสินใน middleware ก่อนถึง DB จึงไม่ต้อง fake port อะไรเลยสักตัว

### Trap ใหม่

43. **`.RequireRateLimiting(...)` ที่หายไปไม่ทำให้อะไรพัง** — endpoint ยังตอบ 200/404/409 ปกติ ต่างจาก
    policy ที่ไม่ได้ register (throw -> 500) ⇒ test ที่ assert status code ของ happy path พิสูจน์ metadata
    ตัวนี้ไม่ได้เลย ต้องมี flood test เท่านั้น. เกณฑ์แนวนี้ (middleware metadata บน endpoint) ให้ตรวจด้วย
    "ถอดบรรทัดแล้ว test แดงไหม" ทีละบรรทัด ไม่ใช่ทั้งคู่พร้อมกัน — ไม่งั้น test เดียวคลุม 2 route แบบหลอกตัวเอง

### ผล test จริงหลังแก้

- `dotnet test tests/Hosts.Tests --filter CustomerPaymentRateLimit` -> 2 passed / 0 failed
- mutation ต่อบรรทัด: ถอดจาก `pay` -> แดงเฉพาะเส้น `route: "pay"` (`Assert.NotEmpty() Failure: Collection
  was empty`) เส้น `payment-status` เขียว; restore แล้วถอดจาก `payment-status` -> สลับข้างแดงเป๊ะ; restore ->
  เขียว 2 (ยืนยัน `git diff --stat` ว่า `Program.cs` กลับมาเหมือนเดิมไม่มีร่องรอย)
- `dotnet test tests/Hosts.Tests` เต็มชุด -> **455 passed / 0 failed** (เดิม 453)
- `scripts/check-rename-identifiers.sh` OK, `scripts/spec-trace.sh purchase-flow-completion` OK (54/54)
- ไม่ได้แตะ `src/` เลยในคอมมิตนี้ (2 ไฟล์: test + tasks.md) จึงไม่ต้องรัน suite อื่น
- working tree สะอาด เหลือแค่ HANDOFF.md untracked ตามแผน

## Live 2C2P sandbox E2E — customer payment link (2026-08-03, ปิด known gap #2)

รันบน develop (ad0ca89) + local fix 2 ไฟล์ (ยังไม่ commit — ดู "Bug ที่พบ") merchant = vprivilege seed
(`E1000000-0000-4000-8000-000000000001`), connection 2C2P = `E8000000-0000-4000-8000-000000000001`
(EnabledMethods ครบ 3 จาก seed — ops UPDATE ไม่ต้องรันสำหรับ dev)

### ผลต่อ channel

| channel | paymentToken | หน้า PSP | จ่ายจบ | payment-status | webhook | DB |
|---|---|---|---|---|---|---|
| CARD (ใบ 1 `ORD6900000163`, 637.25) | ผ่าน (`CC`) | บัตร + 3DS OTP 123456 | สำเร็จ | `paid` | — | session Paid, order Paid, PaymentPaid+OrderPaid Attempts=1, product PAID + SoldOrderId |
| CARD (ใบ 2 `ORD6900000164`, 1186.25) | ผ่าน | เหมือนใบ 1 | สำเร็จ | — | `Processed` แล้ว replay ซ้ำ = `Duplicate` | ครบเหมือนใบ 1 |
| PROMPTPAY_QR (`ORD6900000165`, 6127.25) | ผ่าน (`QR`) | QR จริง render + timer 10 นาที | จ่ายไม่ได้ (sandbox ไม่มี simulator บนหน้า) | `pending` (ถูกต้อง) | webhook ปลอม respCode 0000 = `Ignored` (fetch-to-confirm ตีตก — fail-closed พิสูจน์แล้ว) | session ยัง open |
| INSTALLMENT (`ORD6900000166`, 11891.75) | ผ่าน (`IPP`) | แผนผ่อน 9 แผน (SCB 3/4/6/10 เดือน) render | charge โดน sandbox decline ทั้ง VISA 4111 และ MC 5555 (คาดต้องใช้บัตร test เฉพาะธนาคาร) | `failed` (ถูกต้อง) | — | session Failed(3), order ยัง AwaitingPayment, **pay ซ้ำ mint session ใหม่สำเร็จ (lazy re-mint พิสูจน์ live)** |

### Bug จริงที่พบบน develop (ทั้งคู่ escape ทุก gate — แก้ local แล้ว ยังไม่ commit)

1. **`OrderNoSequence.NextAsync`**: `SqlQueryRaw<long>(...).SingleAsync()` — EF ห่อเป็น
   `SELECT TOP(2) ... FROM (...) AS [v]` แล้ว SQL Server ปฏิเสธ `Msg 11719` (NEXT VALUE FOR ห้ามอยู่ใน
   derived table) ⇒ **order สร้างไม่ได้เลยทั้งระบบ** — CheckoutConfirmed ค้างใน outbox จน poison
   (MaxAttempts=8). ทำไมหลุด: `OrderNoSequenceIntegrationTests` ยิง sequence ผ่าน `SqlConnection` ดิบ
   ไม่เคยเรียก `NextAsync` ผ่าน EF. Fix = ADO ตรง (`GetDbConnection` + `CreateCommand` + enlist
   `CurrentTransaction`)
2. **`PayableOrderReader.GetForMintAsync`**: `FromSqlInterpolated` + `.Select(o => o.Amount.Amount...)` —
   EF compose projection ทับ FromSql แล้วลืม `HasColumnName` ของ complex type ⇒
   `Invalid column name 'Amount_Amount'` ⇒ **`POST /orders/{token}/pay` 500 ทุกครั้ง**. `GetAsync`
   (LINQ ล้วน) ปกติ. ทำไมหลุด: Hosts.Tests fake `IPayableOrderReader` ทั้งชุด ไม่มี integration test
   เรียก reader จริง. Fix = 2 ขั้น: raw `SELECT Id ... WITH (UPDLOCK)` (lock) แล้ว `GetAsync` (mapping +
   query filter) ใน transaction เดิม
   - **บทเรียนทั่วไป**: `NEXT VALUE FOR` และ complex-type projection ห้ามผ่าน EF composition — checklist
     ใหม่: raw-SQL port ทุกตัวต้องมี integration test ที่เรียก method จริงผ่าน EF ไม่ใช่ SQL สำเนามือ

### Trap ใหม่

44. **merch.Sessions rotation ฆ่า token ที่ insert มือ** — BFF rotate session ระหว่างใช้งาน (Set-Cookie
    ใหม่ที่ curl ไม่เก็บ) ⇒ 401 กลางคัน; แก้โดย mint token ใหม่ต่อ chain หรือใช้ cookie jar (`curl -c/-b`)
45. **หน้า 2C2P SPA ไม่ re-init เมื่อเปลี่ยนแค่ hash token** — navigate ไป token ใหม่แล้วต้อง reload 1 ครั้ง
46. **ช่องชื่อผู้ถือบัตร 2C2P concat ค่าเดิม** (framework model ไม่ sync กับ DOM clear) — ต้อง set ผ่าน native
    value setter + dispatch `input`/`change`
47. **order `ORD6985474739` (15000, PROMPTPAY_QR) ใน dev DB เป็นซากของ `Two_orders_cannot_share_a_number`**
    ที่รันกับ dev DB — query ระวังใช้ `OrderNo LIKE 'ORD6900%'`

### สถานะแวดล้อมหลังจบ

- `.env`: เพิ่ม `Psp__PublicBaseUrl=http://localhost:5100`, ลบ key ตาย `Psp__TwoCTwoP__BackendReturnUrl`
- vault: `merch.VaultSecrets` มี `psp/vprivilege/2c2p` (sandbox credential ใหม่จาก user, KeyId `local-envelope-v1`)
- dev DB reseed ไปก่อนหน้านี้ — rig เก่ารอบ captive (merchant `025642A0`, connection `C582F741`) ไม่มีแล้ว
- API รันค้างไว้ด้วย `nohup` (log `/tmp/pol-api.log`); e2e merchant sessions ถูก revoke แล้ว
- ค้างทำต่อ: เปิด fix PR สำหรับ bug 2 ตัว + regression/integration test ที่เรียก `NextAsync`/`GetForMintAsync` จริง

### อัปเดต IPP + bug ตัวที่ 3 (พบหลัง user สั่งติ๊ก checkbox ให้จ่าย IPP จนจบ)

- IPP จ่ายจบจริงได้: MC 5555 + ติ๊กยอมรับเงื่อนไข SCB (คลิกผ่าน label — native setter ไม่ sync model,
  รอบก่อน submit ไม่ผ่าน validation เงียบ ๆ) -> 3DS OTP -> หน้า success (3 เดือน 0%, 3,963.92/งวด)
  = channel IPP ใช้งานได้จริง end-to-end ฝั่ง PSP
- **Bug #3 (money-path, ยังไม่แก้)**: ระหว่างที่ charge ยังไม่ถูก attempt ผมเรียก `payment-status`
  -> inquiry คืน respCode นอกลิสต์ Pending (`0001/2001/4009`) -> `TwoCTwoPAdapter` map เป็น Failed
  -> `MarkFailed` session `41872F54` ก่อนเวลา -> ลูกค้าจ่ายบน hosted token เดิมสำเร็จทีหลัง ->
  เงินเข้า PSP 11,891.75 THB บน session terminal. ยืนยันด้วย webhook replay: outcome `Ignored` +
  `LogCritical "PSP confirmed payment for a TERMINAL payment session: order 68ea3ff1-..., payment
  session 41872f54-... (status Failed), charge 41872f54..., amount 11891.7500 THB. Refund is manual."`
  — design รับมือถูกตาม `Conflicted` แต่ root cause คือ classification: สถานะ "ยังไม่ attempt"
  ต้องเป็น Pending ไม่ใช่ Failed (ซ้ำรอยบทเรียน feedback-money-path-failure-classification)
  งานแก้: หา respCode จริงจาก 2C2P doc v4.3 (ต้อง research) แล้วขยายลิสต์ Pending ใน
  `TwoCTwoPAdapter.cs` + test; ระวังอย่า map กว้างจน charge ที่ตายจริงค้าง Open ตลอด
- สถานะ dev DB ที่ต้องรู้: `ORD6900000166` มีเงิน sandbox เข้าแล้วแต่ order ยัง AwaitingPayment,
  session ทั้ง 2 ใบ Failed(3) — คือภาพจริงของเคส refund-manual ปล่อยไว้เป็นหลักฐาน

### Bug #3 — root cause ยืนยันแล้วด้วย live reproduction (2026-08-03)

ยิง `paymentInquiry` v4.3 ตรง (JWT HS256, script `/tmp/inquiry.py` — ไม่มี secret ฝังในไฟล์) 3 สถานะ:

| สถานะ session | invoiceNo | respCode จริง | adapter ปัจจุบัน map เป็น | ที่ถูกต้อง |
|---|---|---|---|---|
| token สร้างแล้ว ไม่เคย attempt (`D6F14007`, order 6401.75) | d6f14007... | **`2002` "Transaction not found."** | Failed (bug) | **Pending** |
| จ่ายแล้ว (`EFD54A37`) | efd54a37... | `0000` "Success" | Paid | ถูก |
| QR สร้างแล้วยังไม่จ่าย (`B5EFDDCF`) | b5efddcf... | `2001` "Transaction in progress." | Pending | ถูก |

สรุป fix ของ task ใหม่ (จาก brief researcher + repro นี้): เพิ่ม `"2002"` เข้ากลุ่ม Pending ใน
`TwoCTwoPAdapter.MapRespCode` (`TwoCTwoPAdapter.cs:153-158`) + test theory `2002`->Pending และ
regression `0003`->Failed (cancelled ตายจริง ห้ามย้าย); `2003` ยัง ambiguous — ห้ามเดา ต้อง repro
หรือถาม 2C2P support (อาจต้องยกไป `PspAmbiguousException` layer ถ้า semantics = inquiry ล้มเหลวเอง)
อ้างอิงตาราง: https://developer.2c2p.com/docs/response-code-payment

### PROMPTPAY_QR จ่ายจบจริงผ่าน 2C2P PromptPay Simulator (2026-08-03 บ่าย)

gap สุดท้ายของ E2E ปิดแล้ว — user ชี้ทาง simulator ทางการของ 2C2P:
`https://uatqrgw.2c2p.com/TQRPromptPayTestTools/QRTestPage/Test1PromptPaySimulator`

ลำดับที่ทำจริง (order `ORD6900000165`, token `a507b380...`):

1. session QR เดิม `B5EFDDCF` (mint 07:49) — hosted page ตอบ **"ชำระเงินไม่สำเร็จ (4093)"**
   ตอนกดสร้าง QR = payment token 2C2P หมดอายุ (นานกว่า ~7 ชม.) -> `payment-status` settle เป็น
   `failed` (ถูกต้อง — token ตายจริง ไม่ใช่เคส 2002) -> `POST /pay` lazy re-mint ได้ session ใหม่
   `86AA7069` ทันที (พิสูจน์ re-mint ซ้ำอีกรอบ)
2. หน้า hosted กรอกชื่อ (ต้องใช้ native value setter — trap เดิม) -> สร้าง QR (หน้าต่าง 10 นาที)
3. โหลดรูป QR PNG จาก S3 ของ 2C2P -> upload เข้า simulator (**trap ใหม่ #48**: ชื่อไฟล์ upload
   ต้องเป็นชื่อต้นฉบับจาก S3 เช่น `15317499.png` — ชื่ออื่นโดน "Invalid file name")
   -> decode อัตโนมัติ: BillerNo/Ref1/REF2/ProxyId/Amount 6127.25 ตรงครบ
4. **trap ใหม่ #49**: กด Invoke Payment ทั้งที่ payer fields ว่าง = `{"resCode":"99","resDesc":
   "SystemError"}` — ต้องเติม Payer Account Number / Payer Name / Sending Bank Code (004) /
   Receiving Bank Code (002) / Transaction Id เองก่อน -> ได้
   `{"resCode":"00","resDesc":"SUCCESS","transactionId":"TXN...","confirmId":"000L9L"}`
5. settle ทาง 1: `POST /orders/{token}/payment-status` -> `{"status":"paid"}`
   DB: session `86AA7069` Status=2 (Paid), order `ORD6900000165` Status=1 (Paid),
   outbox `PaymentPaid` Attempts=1 ProcessedAt stamped, `shop.Products.SoldOrderId` stamp แล้ว
6. settle ทาง 2: webhook replay -> `{"outcome":"Duplicate"}` HTTP 200 (idempotency ถูกต้อง)

**trap ใหม่ #50**: dev DB ถูก reseed — connection 2C2P ของ merchant ตอนนี้คือ
`E8000000-0000-4000-8000-000000000001` (ไม่ใช่ `C582F741` ของ rig captive เดิม — ยิง webhook
ด้วย id เก่าได้ 404 จาก merchant resolver ซึ่งเป็น no-leak 404 ไม่ใช่ route หาย); หา id ปัจจุบัน:
`SELECT Id FROM txn.PspConnections WHERE MerchantId=(SELECT MerchantId FROM txn.PaymentSessions WHERE Id='<session>')`

**สรุป E2E ครบ 3 channel แล้ว**: CARD จ่ายจบ + settle 2 ทาง, INSTALLMENT จ่ายจบ (SCB 3 เดือน 0%),
PROMPTPAY_QR จ่ายจบผ่าน simulator + settle 2 ทาง — ไม่มี channel ค้างอีก
