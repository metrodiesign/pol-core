# HANDOFF: checkout-chain-document-fields

Rolling handoff — teammate ทุกคน **append หัวข้อใหม่ท้ายไฟล์** หลังจบ task ของตัวเอง (สิ่งที่ทำ / ไฟล์ที่แตะ / กับดักที่เจอจริง / สถานะ test / สิ่งที่คนถัดไปต้องรู้). ห้ามแก้หัวข้อของคนก่อน

## จาก lead (Fable) — บริบทตั้งต้น 2026-07-30

- Branch `feat/products-insurance-document`, PR #143 OPEN (Products pivot เป็นเอกสารประกัน merge แล้วใน branch นี้, commit `9e87c51`)
- Bridge ที่ต้องถอดอยู่ `src/Modules/Products/Products.Application/ProductView.cs` (~:47-60): `Price`/`SumInsured`/`CoverageDurationDays`/`Insurer` — จุดใช้มี 2 ที่ใน `src/Hosts/Api/Program.cs`: cart (`~:663-668` ใช้ `IsActive`+`Price`) และ checkout (`~:770-780` snapshot 3 field เก่า)
- ตำแหน่งไฟล์ทั้งหมด + ลำดับ param กลาง + traps: อ่าน `design.md` (สำรวจยืนยันแล้ว 2026-07-30 — เชื่อได้ ไม่ต้อง re-explore กว้าง)
- 3 field เก่าไม่มี logic คำนวณอิงอยู่ — ลบได้ตรง ๆ; policy report / OrderSummaryReader ไม่แตะ — ห้ามไปยุ่ง
- dev DB :11433 สถานะ: migration `20260730072057_ProductsInsuranceDocument` apply แล้ว, shop.Products = 100 (seed-demo) + 6 (e6 migration seed); หมายเหตุ: seed-demo assert `merch.Merchants = 0` fail เป็นสภาพ local เดิม (merchant `vprivilege` คนละ Id ค้าง) ไม่เกี่ยวงานเรา
- Follow-up ที่จดไว้แล้ว ไม่ทำรอบนี้: wire `Product.MarkPaid`/`Deactivate` ตอน order paid; SP adapter

## chain-t1 — Task 1: Checkouts + Contracts (2026-07-30)

**สิ่งที่ทำ**: เปลี่ยน snapshot field เก่า 3 ตัว (`SumInsured`/`CoverageDurationDays`/`Insurer`) เป็นชุดเอกสาร 6 ตัวตาม design ตลอดเส้น Checkouts + Contracts

**ไฟล์ที่แตะ** (8):
- `src/Contracts/CheckoutConfirmed.cs` — `CheckoutConfirmedItem` positional ใหม่ + doc comment
- `src/Modules/Checkouts/Checkouts.Domain/Items/CheckoutItemInput.cs`
- `src/Modules/Checkouts/Checkouts.Domain/Items/Item.cs` — property + ctor + invariant ใหม่
- `src/Modules/Checkouts/Checkouts.Domain/Session.cs` — ส่งผ่าน
- `src/Modules/Checkouts/Checkouts.Application/ConfirmCheckout.cs` — mapper -> event
- `src/Modules/Checkouts/Checkouts.Infrastructure/Items/ItemConfiguration.cs`
- `src/Persistence/Persistence.MerchantRuntime/Checkouts/Items/ItemConfiguration.cs` (identical block กับข้างบน)
- `tests/Checkouts.Tests/ConfirmCheckoutTests.cs`

**สถานะ build/test**: `src/Contracts` 0/0; `Checkouts.Infrastructure` (7 โปรเจกต์) 0/0; `Persistence.MerchantRuntime` แดงเฉพาะที่ `Orders.Application/CheckoutConfirmedConsumer.cs:40` (3 error = field เก่า) ซึ่งเป็นงาน task 2; `dotnet test tests/Checkouts.Tests` 13/13 เขียว

**กับดักที่เจอจริง**:
1. `dotnet build a b c` หลายโปรเจกต์ใน call เดียว = MSB1008 (`Only one project can be specified`) — ต้องวน build ทีละโปรเจกต์
2. task gate ยิงตอน Edit ที่ flip `[x]` แม้ยังไม่ได้เติม Evidence — **flip ถูกเขียนลงไฟล์แล้วถึงจะ block** ไม่ใช่ rollback; แก้โดย Edit ต่อเติมบรรทัด Evidence ทันที (สรุป: อย่าแยก flip กับ Evidence)
3. Evidence line ต้องมี `Viewports 375/768/1440: n/a` + `Deviations:` ด้วย ไม่ใช่แค่ผล test

**สิ่งที่ chain-t2 (Orders) ต้องรู้**:
- ลำดับ param กลางที่ใช้จริง: `ProductId, Quantity, UnitPrice, DocumentNo, ProductGroup, DocumentType, PolicyNumber, StartDate, EndDate, InsuredFirstName, InsuredLastName, InsuredIdNumber, InsuredDateOfBirth` — `CheckoutConfirmedItem` เป็น shape นี้แล้ว map เข้า `OrderItemInput` ได้ 1:1
- CLR types: `string DocumentNo`, `string ProductGroup`, `string DocumentType`, `string? PolicyNumber`, `DateTime? StartDate`, `DateTime? EndDate`
- invariant ที่ `Checkouts.Item` ใช้ (ทำแบบเดียวกันใน `Orders.Item` เพื่อ defense in depth): `ArgumentException.ThrowIfNullOrWhiteSpace` 3 ตัว + Trim, `if (startDate is not null && endDate is not null && startDate > endDate) throw new ArgumentException(..., nameof(startDate))`; `PolicyNumber` ไม่ trim (nullable ปล่อยตามที่มา)
- EF block ที่ใช้จริง (copy ไปใช้กับ Orders ทั้ง 2 ไฟล์ให้เหมือนกัน):
  `DocumentNo` `.HasMaxLength(150).IsRequired()` / `ProductGroup` `.HasMaxLength(10).IsUnicode(false).IsRequired()` / `DocumentType` `.HasMaxLength(20).IsUnicode(false).IsRequired()` / `PolicyNumber` `.HasMaxLength(150).IsUnicode(false)` / `StartDate`+`EndDate` `.HasPrecision(0)` — ลบทั้ง `ComplexProperty(x => x.SumInsured, ...)`, `CoverageDurationDays`, `Insurer` (column `InsurerName`)
- error ที่ chain-t2 จะเห็นตอนเริ่ม: `CheckoutConfirmedConsumer.cs(40,57/71/95)` CS1061 x3 — คาดไว้แล้ว
- test helper pattern ที่ใช้ใน Checkouts.Tests: static `Line(...)` มี default param ทุกตัว แล้ว test แต่ละอันส่งเฉพาะ field ที่สนใจ — ลดการแก้ literal ซ้ำ ๆ ตอน signature เปลี่ยน (แนะนำทำแบบเดียวกันใน Orders.Tests)

## chain-t2 — Task 2: Orders (2026-07-30)

**สิ่งที่ทำ**: ต่อเส้น snapshot จาก `CheckoutConfirmedItem` เข้า Orders ครบ — `Orders.Item` ใช้ชุด field เอกสาร 6 ตัวตามลำดับ param กลางเดียวกับ Checkouts, invariant เดียวกัน (defense in depth), read models 2 ตัวเปลี่ยน field ตาม, EF dual-config 2 ไฟล์ identical

**ไฟล์ที่แตะ** (11):
- `src/Modules/Orders/Orders.Domain/Items/Item.cs` — property + ctor + invariant + doc comment
- `src/Modules/Orders/Orders.Domain/Items/OrderItemInput.cs`
- `src/Modules/Orders/Orders.Domain/Order.cs` — ส่งผ่านเข้า `Item` ctor (invariant `Order.Create` เดิมไม่แตะ)
- `src/Modules/Orders/Orders.Application/CheckoutConfirmedConsumer.cs` — map event -> `OrderItemInput` 1:1
- `src/Modules/Orders/Orders.Application/GetOrderDetail.cs` — `OrderItemDetail` (reveal-audit เดิมไม่แตะ)
- `src/Modules/Orders/Orders.Application/GetOrders.cs` — `OrderItemListItem` (`MaskIdNumber` เดิมไม่แตะ)
- `src/Modules/Orders/Orders.Infrastructure/Items/ItemConfiguration.cs`
- `src/Persistence/Persistence.MerchantRuntime/Orders/Items/ItemConfiguration.cs` (block เดียวกันเป๊ะ)
- `tests/Orders.Tests/{OrderItemsTests,GetOrderDetailTests,GetOrdersTests,CheckoutConfirmedConsumerTests,Fakes}.cs`

**สถานะ build/test**: `dotnet test tests/Orders.Tests` 75/75 เขียว; `Orders.Domain`/`Orders.Application`/`Orders.Infrastructure`/`Persistence.MerchantRuntime` build 0 error 0 warning ทุกตัว (error `CheckoutConfirmedConsumer.cs:40` ที่ chain-t1 ทิ้งไว้หายแล้ว); `grep` residue ใน `src/Modules/Orders src/Modules/Checkouts src/Contracts` (ยกเว้น `ItemPolicy*`) ว่าง

**กับดักที่เจอจริง**:
1. ยืนยันกับดัก task gate ของ chain-t1 ซ้ำ: flip `[x]` ใน Edit แยกโดน block **หลังจากเขียนลงไฟล์แล้ว** (ไฟล์ค้างสถานะ `[x]` ไม่มี Evidence) — ต้อง Edit เติม Evidence ทันทีเป็น step ถัดไป; ทางที่ปลอดภัยกว่าคือเขียน Evidence ก่อนแล้วค่อย flip
2. `GetOrderDetailTests` มี `OrderItemInput` 2 ตัวที่ต่างกันแค่ชื่อ/idNumber — `replace_all` จับได้แค่ตัวแรกเพราะ prefix ต่างกัน ต้องแก้ตัวที่สองแยก
3. doc comment ของ `Orders.Infrastructure/Items/ItemConfiguration.cs` อ้าง `<c>SumInsured</c>` เป็น complex-type Money ด้วย — ถ้าไม่แก้ grep residue จะไม่ว่าง (comment ก็ติด grep)

**สิ่งที่ chain-t3 (Host wiring + ถอด bridge) ต้องรู้**:
- ชั้น Contracts/Checkouts/Orders ครบแล้วทั้งเส้น — error ที่เหลือทั้งหมดควรอยู่ใน `src/Hosts/Api/Program.cs` + tests ของ Hosts/Architecture เท่านั้น
- `Orders.Item` ตอนนี้มี `DocumentNo`/`ProductGroup`/`DocumentType`/`PolicyNumber`/`StartDate`/`EndDate` และ throw `ArgumentException` ถ้า 3 ตัวแรกว่าง/whitespace หรือ `StartDate > EndDate` — Hosts.Tests ที่สร้าง order line ต้องส่งค่าไม่ว่าง ไม่งั้นพังตอน `Order.Create` ไม่ใช่ตอน assert
- read model ที่ FE/test อ่าน: `OrderItemDetail` และ `OrderItemListItem` ลำดับ field = `ProductId, [Quantity,] UnitPrice, DocumentNo, ProductGroup, DocumentType, PolicyNumber, StartDate, EndDate, ...` (list ไม่มี `Quantity` ตามเดิม, list ใช้ `MaskedInsuredIdNumber`)
- ยังไม่มี migration — model กับ DB ตอนนี้ไม่ตรงกัน (task 4); ดังนั้นเทสต์ใด ๆ ที่ต่อ DB จริงจะพังจนกว่า task 4 จะเสร็จ ไม่ใช่ regression จาก task 2/3
- ค่าตัวอย่างที่ใช้ใน Orders.Tests: `DocumentNo = "00098-69100/กธ/900001-10"`, `ProductGroup = "VMI"`, `DocumentType = "POLICY"` — ใช้ชุดเดียวกันได้ใน seed/tests อื่นเพื่อความสอดคล้อง

## chain-t3 — Task 3: Host wiring + ถอด bridge (2026-07-30)

**สิ่งที่ทำ**: ต่อปลายเส้น snapshot ที่ host (POST /checkouts snapshot field เอกสารจาก `ProductView` ตรง ๆ, cart ใช้ `TotalPremium`) แล้วลบ bridge properties 4 ตัวบน `ProductView` ทิ้ง — ทั้ง solution compile 0 error เป็นครั้งแรกตั้งแต่ task 1

**ไฟล์ที่แตะ** (9):
- `src/Hosts/Api/Program.cs` — checkout snapshot ชุดใหม่ (`product.DocumentNo, product.ProductGroup.ToString(), product.DocumentType.ToString(), product.PolicyNumber, product.StartDate, product.EndDate`) + cart `product.Price` -> `product.TotalPremium`
- `src/Modules/Products/Products.Application/ProductView.cs` — ลบ `Price`/`SumInsured`/`CoverageDurationDays`/`Insurer` + `ponytail:` comments (คง `InsuranceType` computed ไว้)
- `tests/Hosts.Tests/InsuranceCheckoutEndToEndTests.cs` — cart price, `CheckoutItemInput` ชุดใหม่, assert 6 จุดเป็น field เอกสารจริงตามที่ product ถูกสร้างใน test
- `tests/Hosts.Tests/WorkerWriteFloorTests.cs` — ctor args 2 จุด (`CheckoutConfirmedItem` + `OrderItemInput`)
- `tests/Hosts.Tests/{ListPolicyReportEndToEndTests,UpsertItemPolicyEndToEndTests,UpsertItemPolicyAdminEndToEndTests}.cs` — ctor args อย่างเดียว (**ไม่ได้อยู่ในรายการของ task** — compiler พาไปเจอ)
- `tests/Architecture.Tests/OrderItemsTests.cs` — ctor + assert 6 จุด
- `tests/Architecture.Tests/PaymentPricingQueryTests.cs` — ctor args

**สถานะ build/test**: `dotnet build` ทั้ง solution `64 projects, 0 errors, 0 warnings`; Architecture.Tests 229/229; Products.Tests 31/31; Hosts.Tests 352/353 — แดง 1 เคสคือ `ModelConsistencyTests.Model_has_no_pending_changes_against_the_migration_snapshot`

**กับดักที่เจอจริง**:
1. ยืนยันกับดัก task gate อีกรอบ: flip `[x]` แยกจาก Evidence โดน block **หลังเขียนลงไฟล์แล้ว** — ต่อ Evidence ทันทีในขั้นถัดไป
2. call site `OrderItemInput` มีอีก 3 ไฟล์ที่ tasks.md ไม่ได้ระบุ (policy-report / upsert-item-policy E2E) — อย่าไว้ใจรายการไฟล์ใน task ให้ `dotnet build` ทั้ง solution เป็นตัวชี้
3. `perl -0pi -e 's/\Q...\E/'` กับ pattern ข้ามบรรทัดใช้ไม่ได้ (`\Q` quote `\n` เป็น backslash-n) — ใช้ Edit tool หรือ python แทน
4. `git stash` แล้วรัน test เพื่อพิสูจน์ว่า fail มาก่อนไม่ได้ผลใน task นี้ — HEAD ยัง build ไม่ผ่าน (task 3 คือชิ้นที่ปิด compile)

**สิ่งที่ chain-t4 (Migration + seed + integration) ต้องรู้**:
- **`ModelConsistencyTests` แดงอยู่ = งานของ task 4 โดยตรง** — model กับ `PolDbContextModelSnapshot` ไม่ตรงตั้งแต่ task 1/2 แก้ EF config; task 3 ไม่แตะ config/snapshot เลย พอ `dotnet ef migrations add CheckoutChainDocumentFields` เสร็จ snapshot จะถูก regen แล้วเคสนี้ควรเขียวเอง ถ้ายังแดงหลัง gen แปลว่า config 4 ไฟล์ไม่ identical จริง
- ค่าตัวอย่างที่ใช้ทั่ว tests ตอนนี้ (ใช้ชุดเดียวกันใน seed ได้เลย): `DocumentNo = '00098-69100/กธ/900001-10'`, `ProductGroup = 'VMI'`, `DocumentType = 'POLICY'`; nullable 3 ตัวส่วนใหญ่เป็น NULL ยกเว้น `Architecture.Tests/OrderItemsTests` ที่ใส่ `PolicyNumber = "P-900001"` + Start/End เพื่อ prove round-trip ของ column nullable
- Integration.Tests ที่ยังต้องแก้ (task 4): `OrderSummaryReaderIntegrationTests.cs:39` INSERT column list ยังเป็น `SumInsuredAmount, SumInsuredCurrency, CoverageDurationDays, InsurerName` — task 3 ไม่แตะตามขอบเขต
- `ProductView` ไม่มี `Price` แล้ว — โค้ด/test ใหม่ใด ๆ ที่อยากได้ราคาให้ใช้ `TotalPremium`

## chain-t4 — Task 4: Migration + seed + integration (2026-07-30)

**สิ่งที่ทำ**: gen migration `20260730081227_CheckoutChainDocumentFields` (alter 2 ตาราง ตาม REQ-5.1 ไม่มี DropTable จึงไม่ re-GRANT), apply จริงบน :11433, อัปเดต seed-demo INSERT `shop.OrderItems` + raw-SQL INSERT ใน Integration.Tests 2 ไฟล์ ให้เป็น column ชุดใหม่

**ไฟล์ที่แตะ** (6):
- `src/BuildingBlocks/BuildingBlocks.Infrastructure/Persistence/Migrations/20260730081227_CheckoutChainDocumentFields.cs` (ใหม่)
- `src/BuildingBlocks/BuildingBlocks.Infrastructure/Persistence/Migrations/20260730081227_CheckoutChainDocumentFields.Designer.cs` (ใหม่)
- `src/BuildingBlocks/BuildingBlocks.Infrastructure/Persistence/Migrations/PolDbContextModelSnapshot.cs` (regen อัตโนมัติ ไม่แก้มือ)
- `docker/bootstrap/seed-demo.sql` — INSERT `shop.OrderItems` column list ใหม่ + comment 1 บรรทัดอธิบายว่า snapshot copy มาจาก product แถวที่อ้าง
- `tests/Integration.Tests/OrderSummaryReaderIntegrationTests.cs`
- `tests/Integration.Tests/OrderItemPolicyGrantsTests.cs`

**สถานะ build/test**: `dotnet ef database update` ผ่าน; seed-demo `shop.OrderItems = 4` + `seed-demo: OK.`; `Integration.Tests` 47/47 เขียว; `Hosts.Tests --filter ModelConsistencyTests` 1/1 เขียว (drift ที่ chain-t3 ทิ้งไว้ปิดแล้ว)

**กับดักที่เจอจริง**:
1. ยืนยันกับดัก task gate ครั้งที่ 4: flip `[x]` ใน Edit แยกโดน block **หลังเขียนลงไฟล์แล้ว** — ต่อ Evidence ทันทีเป็นขั้นถัดไป กลับมาเขียวเอง
2. seed-demo assert `merch.Merchants = 0` ยัง raise `Msg 51000` ทุกครั้ง = สภาพ local เดิม ไม่เกี่ยวงานนี้ (lead บันทึกไว้แล้ว) — **อย่าไปแก้ seed เพราะเห็น error นี้**; ตัวชี้วัดจริงของ task นี้คือบรรทัด `shop.OrderItems = 4`
3. scaffold ออกมาตรงกับ design เป๊ะไม่มีของแถม แปลว่า EF config 4 ไฟล์ของ chain-t1/t2 identical จริง — ถ้าใครแก้ config เพิ่มภายหลังต้องแก้ทั้ง 4 ไฟล์แล้ว gen migration ใหม่ ห้าม `ef migrations remove` ทับตัวนี้ (apply ลง DB ไปแล้ว)

**สิ่งที่ chain-t5 (full gate + PR) ต้องรู้**:
- ค่า snapshot ใน seed ผูกกับ product ที่ order อ้างจริง: item `ef…0001`/`0002` -> product `e9…0006` (`69100/สล/900006`, CMI/ENDORSEMENT), item `0003` -> `e9…000b` (`S001-69100/อค/900011`, FIRE/POLICY), item `0004` -> `e9…0009` (`00098-69100/กธ/900009-10`, VMI/POLICY) — ถ้าแก้ seed products ต้องตามมาแก้ตรงนี้ด้วย
- DB :11433 ตอนนี้ apply migration ล่าสุด + re-seed แล้ว — Integration.Tests รันซ้ำได้เลย ไม่ต้อง reset
- ยังไม่ได้รัน gate เต็มในรอบนี้ (`-warnaserror`, rename-identifiers, spec-trace, suite อื่นทั้งหมด) — งานของ task 5 ล้วน; เท่าที่รันในรอบนี้ทุกอย่างเขียว
- migration นี้ให้ `defaultValue: ""` กับ 3 column NOT NULL (แถว dev เก่า) — ตามที่ design ยอมรับไว้ (pre-prod) ถ้า reviewer ทัก ตอบด้วย design.md ข้อ 6
