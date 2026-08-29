# Design: Captive Intra-Group Payment Alignment

> Status: unknown
> 2026-07-27; see requirements.md "Gate note"); amended 2026-07-28 with D8a after a live 2C2P sandbox
> E2E found a real money-path bug in the pre-existing webhook claim order (REQ-8.5)

## Guiding constraint

ทุก decision ต้องอยู่ใต้ประโยคเดียว: **แพลตฟอร์มเป็น channel ให้บริษัทในเครือรับเงินผ่าน PSP ที่ถือ
ใบอนุญาต — เราไม่ถือเงิน ไม่ประมวลผลการจ่าย และไม่ mint ยอดเงินเอง**. เลือกทางที่ทำให้ (ก) ยอดที่ PSP
เห็นสืบกลับไป order row ได้ 100%, (ข) connection ของบริษัทเป็นตัวกำหนดทั้งช่องทางที่ charge ได้และปลายทาง
ที่ PSP แจ้งกลับ, (ค) ไม่มีสถานะไหนที่ระบบเดินต่อไม่ได้.

ไม่มี entity ใหม่ ไม่มี module ใหม่ ไม่มี status ใหม่. ทั้งหมดคือ: 1 read port, 1 domain guard,
1 vocabulary, 1 capability declaration, 2 repository query, 1 filtered index + migration, 1 config key,
การเดินสาย `Connection`/`MarkFailed` ที่มีอยู่แล้วให้มี call site จริง, และ 1 field เพิ่มบนผลลัพธ์
fetch-to-confirm.

## Architecture deltas

### D1 — `IPayableOrderReader`: Payments อ่าน order ผ่าน port

```csharp
// src/Modules/Payments/Payments.Application/Ports/IPayableOrderReader.cs
namespace Payments.Application.Ports;

/// <summary>The only order facts a payment session needs: the amount to charge and whether the order is
/// still awaiting payment. Deliberately carries no line/PII data — the merchant-facing order detail read
/// (which writes a reveal audit) must never be on the payment path.</summary>
public sealed record PayableOrder(Guid OrderId, Money Amount, bool IsAwaitingPayment);

public interface IPayableOrderReader
{
    /// <summary>Reads the order under the bound merchant's query filter; null when it does not exist for
    /// that merchant (an order under another merchant is indistinguishable from a missing one).</summary>
    Task<PayableOrder?> GetAsync(Guid orderId, CancellationToken cancellationToken);
}
```

impl: `src/Persistence/Persistence.MerchantRuntime/Payments/PayableOrderReader.cs` — `internal sealed`,
`AsNoTracking()`, **project scalar 3 ตัวแล้วประกอบ `Money.Of` ใน memory** ตาม pattern ที่ repo ใช้อยู่
(`OrderRepository.cs:29`, `OrderSummaryReader.cs:51`) — ห้าม project complex type `Money` ทั้งก้อนใน LINQ
(ไม่มีที่ไหนในโค้ดทำ และเสี่ยง translate ไม่ผ่าน). ห้ามใช้ `Find`/`FindAsync`
(`RuntimeContextFindBanTests`) และห้าม `IgnoreQueryFilters` (`BypassPrimitiveTests`).
register ใน `MerchantRuntimePersistenceRegistration` ข้าง `ISessionRepository`.

**ทำไมที่นี่:** `Persistence.MerchantRuntime.csproj:20-23` reference ทั้ง `Orders.Domain` และ
`Payments.Application` อยู่แล้ว -> **ไม่ต้องแก้ csproj** และไม่สร้าง dependency Payments -> Orders.
pattern เดียวกับ `IWebhookMerchantResolver`/`IProfileLookup`. query filter บน `OrderConfiguration.cs:25`
ทำให้ cross-merchant อ่านไม่เจอเอง -> REQ-1.2 มาฟรี ไม่ต้องเช็ค `MerchantId` ซ้ำ.

### D2 — vocabulary + capability: `PaymentMethods` และ `IPspAdapter.SupportedMethods`

```csharp
// src/Modules/Payments/Payments.Domain/PaymentMethods.cs
/// <summary>The canonical payment-method vocabulary. One place defines the codes, so a connection's
/// EnabledMethods, a session's Method and an adapter's capability set all speak the same strings.</summary>
public static class PaymentMethods
{
    public const string Card = "card";
    public const string PromptPay = "promptpay";
    public const string Installment = "installment";

    public static bool IsKnown(string? method);          // trim+lower compare
    public static string Normalize(string method);       // trim+lower, throws ArgumentException if unknown
}
```

`IPspAdapter` เพิ่ม `IReadOnlySet<string> SupportedMethods { get; }` — วันนี้ทั้ง 2 adapter คืน
`{ PaymentMethods.Card }` (ตรงกับความสามารถจริง: `TwoCTwoPAdapter` ทำได้แค่บัตร,
`OmiseAdapter` throw สำหรับ promptpay). Application อ่านผ่าน `IPspAdapterFactory.For(psp)`.

**ทำไมต้องมีชั้นนี้แยกจาก `EnabledMethods`:** สองเรื่องต่างกัน — `EnabledMethods` = ข้อตกลงเชิงพาณิชย์
ที่บริษัทเปิดกับ PSP; `SupportedMethods` = สิ่งที่โค้ดของเรา honour ได้จริงวันนี้. seed จริง
(`seed-demo.sql:102-105`) เปิด promptpay/installment บน 2C2P อยู่ — ถ้าเช็คแค่ `EnabledMethods`
คำขอ promptpay จะผ่านแล้วถูก `TwoCTwoPAdapter` ส่งไปจ่ายด้วยบัตรเงียบ ๆ (E). intersection ของสองชุด
คือ eligibility จริง.

### D3 — `Connection.EnsureEligible`: eligibility guard จุดเดียวบน domain

```csharp
// Payments.Domain.Psp.Connection
/// <summary>Throws unless this connection may charge <paramref name="method"/> right now: it must be
/// enabled and the method must be in its enabled list. The single eligibility gate — both the
/// create-session and the start-redirect paths call it, so a connection disabled between the two cannot
/// still reach the PSP (REQ-3.5). InvalidOperationException (409) not ArgumentException (400): a
/// disabled connection or a method the company never enabled is SERVER state, not malformed input.</summary>
public void EnsureEligible(string method)
{
    ArgumentException.ThrowIfNullOrWhiteSpace(method);

    if (!IsEnabled)
        throw new InvalidOperationException($"PSP connection {Id} is disabled.");
    if (!Supports(method))
        throw new InvalidOperationException($"PSP connection {Id} does not enable method '{method}'.");
}
```

`Supports` เดิมคงไว้ และได้ call site จริงผ่าน `EnsureEligible` -> ปิด REQ-3.6.

**status mapping (ยืนยันกับ `ProblemDetailsExceptionHandler.cs:54-72`):**
`ArgumentException`=400, `NotFoundException`=404, `InvalidOperationException`=409, `ConflictException`=409.
เส้นแบ่งที่ design นี้ใช้: **input ที่ client เขียนผิด = 400** (method ไม่อยู่ใน vocabulary),
**สถานะ/config ฝั่ง server = 409** (order ไม่ awaiting, ไม่มี connection, connection ปิด, adapter ไม่
support, มี session เปิดอยู่ด้วยช่องทางอื่น). ไม่ต้องแก้ exception handler.

### D4 — `CreateSessionHandler`: ลำดับการตรวจทั้งหมด

```csharp
public sealed record CreateSessionCommand(Guid OrderId, Guid MerchantId, string Method, Code Psp)
    : ICommand<CreateSessionResult>, IMerchantScoped;
```

ลำดับใน `Handle` (ทั้งหมดก่อน `Session.Create`):

1. `var method = PaymentMethods.Normalize(command.Method);` -> `ArgumentException` (400) — REQ-3.4
2. `var order = await _orders.GetAsync(command.OrderId, ct)` — null -> `NotFoundException` (404) — REQ-1.2
3. `!order.IsAwaitingPayment` -> `InvalidOperationException` (409) — REQ-1.3 / REQ-2.3
4. `var connection = await _connections.GetAsync(command.MerchantId, command.Psp, ct)` — null ->
   `InvalidOperationException` (409) — REQ-3.3
5. `connection.EnsureEligible(method)` -> `InvalidOperationException` (409) — REQ-3.1 / REQ-3.2
6. `!_adapters.For(command.Psp).SupportedMethods.Contains(method)` -> `InvalidOperationException` (409)
   — REQ-6.2
7. `var open = await _sessions.GetOpenForOrderAsync(command.OrderId, ct)` —
   - `open is not null && open.Method == method && open.Psp == command.Psp` -> **return
     `new CreateSessionResult(open.Id)`** (idempotent 200) — REQ-2.1
   - `open is not null` (ช่องทางต่าง) -> `ConflictException` (409) — REQ-2.2
8. `Session.Create(merchantId, orderId, order.Amount, method, psp, now)` — **amount มาจาก order** — REQ-1.1

endpoint ส่งแค่ `body.OrderId, actor.MerchantId, body.Method, body.Psp`; `CreatePaymentSessionRequest`
ตัด `Amount` ออก; อัปเดต `.WithDescription` + `ProducesProblem(400/404/409)` ให้ตรงจริง.

**ทำไมอยู่ใน handler ไม่ใช่ endpoint (REQ-1.5):** endpoint เป็นทางเข้าเดียว *วันนี้*; invariant เรื่องเงิน
ต้องอยู่จุดที่ทุก caller ผ่าน. (`/checkouts` compose ที่ endpoint เพราะเป็นการ *ประกอบ input* ข้ามโมดูล
ซึ่งต่างจากการ *บังคับ invariant*.)

**ทำไม 2.1 เป็น idempotent-return ไม่ใช่ 409:** `Session.MarkFailed`/`MarkExpired` ไม่มี production caller
(F) ดังนั้น session ที่ลูกค้าทิ้งค้างที่ `Redirected` ตลอดกาล. ถ้า create-session ตอบ 409 ทุกกรณี + มี
unique index -> **order นั้นจ่ายไม่ได้ตลอดกาล** ซึ่งแย่กว่าโรคเดิม. idempotent-return ทำให้ producer
เรียกซ้ำแล้วได้ session เดิม -> `StartRedirect` คืน hosted URL เดิม (`StartRedirectHandler.cs:51`) ->
ลูกค้าจ่ายต่อได้ ไม่มี charge ที่สอง. เส้นทาง fail จริงถูกปลดด้วย D6 (`MarkFailed`).

### D5 — DB floor: หนึ่ง open session ต่อ order

- port: `ISessionRepository.GetOpenForOrderAsync(Guid orderId, CancellationToken ct)` — คืน session ที่
  `OrderId == orderId && Status is Created or Redirected` (query filter คุม merchant ให้แล้ว) หรือ null.
  คืน entity ไม่ใช่ bool เพราะขั้น 7 ต้องอ่าน `Method`/`Psp`/`Id` ของใบเดิม.
- EF: **แก้ทั้งสองไฟล์** (มี `SessionConfiguration` ของ Payments สองใบจริง — ใบที่ PolDbContext/migration
  ใช้ และใบ runtime; ใส่ผิดใบ = DDL ไม่มี index แต่ unit test เขียว):
  - `src/Modules/Payments/Payments.Infrastructure/Persistence/SessionConfiguration.cs`
  - `src/Persistence/Persistence.MerchantRuntime/Payments/SessionConfiguration.cs`

```csharp
// ต้องใช้ overload ที่ตั้งชื่อ index — HasIndex(x => x.OrderId) ครั้งที่สองจะไป MUTATE index เดิม
// (EF Core: หนึ่ง index ต่อ property-set) ทำให้ lookup index ธรรมดาหายไป ไม่ใช่ได้ index ใบที่สอง
builder.HasIndex(x => x.OrderId, "IX_PaymentSessions_OrderId_Open")
       .IsUnique()
       .HasFilter("[Status] IN (0, 1)");
```

  SQL Server รับ `IN (constants)` ใน filter predicate ของ filtered index (grammar อนุญาต disjunct แบบ
  `column IN (...)`; `OR` ไม่อนุญาต).
- migration: `src/BuildingBlocks/BuildingBlocks.Infrastructure/Persistence/Migrations/`
  timestamp **ต้อง > `20260723160500`**.
- race -> 409 มาฟรี: `MerchantRuntimeUnitOfWork.SaveChangesAsync` แปลง SQL 2627/2601 เป็น
  `ConflictException` อยู่แล้ว (`MerchantRuntimeUnitOfWork.cs:34-42`) -> REQ-2.5 ไม่ต้องเขียนโค้ดใหม่
  แต่ต้องมี test.
- offline proof (REQ-2.6): assertion ใน `tests/Architecture.Tests` ว่า model ของ **ทั้งสอง** context มี
  index ชื่อ `IX_PaymentSessions_OrderId_Open`, `IsUnique == true`, filter ตรงตัว — เพราะ CI ข้าม job
  integration เมื่อไม่มี secret (`ci.yml:128-143`).

### D5a — amended 2026-07-27 (Codex review #4782168269 P1): migration ต้องสะสางแถวซ้ำก่อนสร้าง index

`CreateIndex` เปล่าจะ **fail กลาง migration chain** บนฐานข้อมูลใดก็ตามที่ create-session เวอร์ชันก่อน
หน้าเคยรัน (Risk 3 ของ design นี้ยอมรับสภาพนั้นเองแต่สั่งแค่ให้ reset ด้วยมือ = ไม่ใช่ remediation ที่ ship).
migration ต้องมี `migrationBuilder.Sql` นำหน้า `CreateIndex` ที่ทำตามลำดับนี้ (REQ-2.7):

1. เลือกผู้ชนะต่อ `OrderId` จากแถวที่ `Status IN (0,1)` — เรียงโดยให้ `PspExternalChargeId IS NOT NULL`
   มาก่อน (ใบที่ลูกค้าอาจกำลังจ่ายอยู่จริง) แล้วจึง `CreatedAt DESC`, tiebreak `Id`.
2. `UPDATE` แถวที่แพ้ **และ `PspExternalChargeId IS NULL`** ให้เป็น `Status = 4` (`Expired`) + `UpdatedAt`
   ปัจจุบัน. แถวที่แพ้แต่ **มี** charge ผูกไว้ **ห้ามแตะ**.
3. ถ้ายังเหลือ `OrderId` ที่มีแถว `Status IN (0,1)` มากกว่าหนึ่ง (แปลว่ามีใบผูก charge หลายใบ) ->
   `RAISERROR` ที่ระบุ `OrderId` เหล่านั้นแล้วหยุด. เหตุผล: expire ใบที่มี charge จริงจะทำให้ webhook
   `MarkPaid` throw ตลอดไป (poison) — เป็นการตัดสินใจของคน ไม่ใช่ของ migration.
4. `CreateIndex` ตามเดิม.

`Down` คง `DropIndex` เดิม (ไม่ย้อน `Expired` — ข้อมูลสถานะที่สะสางแล้วไม่มีค่าเดิมให้กู้อย่างปลอดภัย
และ `Down` ของ index ไม่ควรเดา business state; บันทึกไว้ในคอมเมนต์ของ migration).

### D6 — liveness: ปฏิเสธก่อน claim + `MarkFailed` เมื่อ charge ล้ม

`StartRedirectHandler.Handle` เรียงใหม่:

```
1. load session (404 ถ้าไม่มี)
2. idempotent re-entry: Redirected + RedirectUrl != null -> คืน URL เดิม            (เดิม)
3. Status != Created -> InvalidOperationException (409)                            (เดิม)
4. *** resolve connection (409 ถ้าไม่มี) + connection.EnsureEligible(session.Method) ***   <- ย้ายขึ้นมา
5. BeginRedirect() + SaveChangesAsync   (claim ใต้ RowVersion; loser คืน URL ผู้ชนะ)  (เดิม)
6. reveal secret
7. try { adapter.CreateRedirectChargeAsync(session, connection.Id, secret, ct) }
   catch { session.MarkFailed(reason, now); await SaveChangesAsync(); throw; }      <- ใหม่
8. SetPspCharge + SaveChangesAsync
```

- ขั้น 4 ย้ายขึ้น **ก่อน** ขั้น 5 = REQ-3.5 + REQ-7.3 และแก้บั๊กที่มีอยู่แล้ว (`:76-79` throw หลัง claim).
- ขั้น 7 = REQ-7.1/7.2 และเป็นสิ่งที่ทำให้ REQ-7.4 (retry) บังคับได้จริงคู่กับ index ของ D5.
- reason ที่ส่งเข้า `MarkFailed` ต้องไม่มี secret (ใช้ `ex.GetType().Name` + ข้อความของ adapter ที่ระบุแค่
  PSP + HTTP status ตามที่ `PspAdapterBase.SendOnceAsync` โยนอยู่แล้ว).
- **ไม่** catch แล้วกลืน — rethrow เสมอ ให้ ProblemDetails handler จัดการ.

### D6a — amended 2026-07-27 (Codex review #4782168269 P1 x2): definitive vs ambiguous

D6 เวอร์ชันแรก catch `Exception` ทั้งก้อนแล้ว `MarkFailed` ซึ่ง **สร้างช่องจ่ายซ้ำ**: timeout/cancel/
transport/parse หลัง PSP รับ charge ไปแล้ว จะถูกนับเป็น fail -> create-session เปิดใบใหม่ -> `Session.Id`
ใหม่ -> idempotency key ใหม่ที่ PSP -> charge ใบที่สอง; และ charge ใบแรกไม่มี session ที่ผูก
`PspExternalChargeId` ไว้ ทำให้ `GetByExternalChargeAsync` คืน null และ webhook handler throw = poison.
อีกช่องหนึ่งคือ `_vault.RevealAsync` ที่อยู่ **หลัง** claim commit แต่ **นอก** try -> session ค้าง
`Redirected` + `RedirectUrl == null` ซึ่งคือสภาพที่ REQ-7.2 ห้ามไว้ตรง ๆ.

**เส้นแบ่ง:** ผลลัพธ์เป็น **definitive** (พิสูจน์ได้ว่า request ยังไม่ถึง PSP หรือ PSP ปฏิเสธเด็ดขาด) หรือ
**ambiguous** (charge อาจเกิดขึ้นแล้ว). เฉพาะ definitive จึง `MarkFailed` ได้; ambiguous ต้องคง claim ไว้
แล้วสะสางด้วยการเรียกซ้ำ.

- adapter layer เป็นชั้นเดียวที่รู้ว่าได้ส่ง request ออกไปหรือยัง -> ให้ adapter สื่อด้วย **exception type**:
  `PspRejectedException` (definitive: PSP ปฏิเสธ / ยังไม่ได้ส่ง) และ exception อื่นทั้งหมด = ambiguous.
  `PspAdapterBase.SendOnceAsync` map **4xx ยกเว้น 408/429** -> definitive; 5xx/408/429/transport/timeout ->
  ambiguous. throw ที่เกิด **ก่อน** ส่ง HTTP (amount ไม่ representable, method ที่ adapter ไม่รองรับ,
  key-environment mismatch) = definitive. response ที่ verify signature ไม่ผ่าน / อ่าน field ไม่ได้ =
  **ambiguous** (charge อาจถูกสร้างแล้ว).
- `_vault.RevealAsync` ย้ายเข้าไปในเส้นทาง failure ที่ถือว่า **definitive** (ยังไม่มี request ไป PSP เลย)
  -> `MarkFailed` ได้อย่างปลอดภัย.
- **สะสาง claim ที่ค้าง (REQ-7.6):** เปลี่ยนเงื่อนไข re-entry ที่หัว handler จาก
  `Redirected && RedirectUrl != null -> คืน URL` เป็น 2 ทาง — มี URL แล้วคืน URL (เหมือนเดิม);
  `Redirected` + `RedirectUrl == null` -> **เดินขั้น 6-8 ซ้ำ** (reveal secret, เรียก
  `CreateRedirectChargeAsync` อีกครั้ง, `SetPspCharge`) โดย **ไม่** `BeginRedirect` ใหม่.
  ปลอดภัยเพราะ key ของทั้งสอง adapter derive จาก `Session.Id` (2C2P `invoiceNo` + `idempotencyID`,
  Omise `Idempotency-Key`) — doc comment ของ adapter เองระบุว่า POST ซ้ำคืน charge เดิมไม่สร้างใบใหม่.
  ผลคือ charge เดิมถูกผูกกลับเข้า session -> webhook correlate ได้ -> ไม่ต้องมี reconciliation job ใหม่
  (Non-Goal 4 ยังคงอยู่).
- concurrency ของ re-entry: ผู้เรียกสองคนพร้อมกันจะได้ charge เดียวกันจาก PSP; คนที่ save ทีหลังโดน
  `SetPspCharge` throw หรือ concurrency conflict = 409 ให้ผู้แพ้ ซึ่งยอมรับได้ (ไม่มี charge ที่สอง).

### D7 — backend webhook URL ต่อ connection

- `PspOptions` เพิ่ม `public string PublicBaseUrl { get; set; } = "";` และ **ลบ**
  `TwoCTwoPOptions.BackendReturnUrl`.
- `IPspAdapter.CreateRedirectChargeAsync` -> `(Session session, Guid pspConnectionId, string secret,
  CancellationToken ct)`. ส่ง `Guid` ไม่ใช่ `Connection` ทั้งก้อน — adapter ไม่ควรเห็น
  `SecretRefName`/`EnabledMethods`.
  **call site ที่ต้องแก้ (ไม่มี fake/test double ของ `IPspAdapter` ในโค้ดเลย — อย่าไปหา):**
  `IPspAdapter.cs:20`, `PspAdapterBase.cs:38-39`, `TwoCTwoPAdapter.cs:34`, `OmiseAdapter.cs:37`,
  `StartRedirectHandler.cs:83`, และ positional call ใน
  `tests/Payments.Tests/Psp/TwoCTwoPAdapterTests.cs` (7 จุด) +
  `tests/Payments.Tests/Psp/OmiseAdapterTests.cs` (6 จุด).
- `PspAdapterBase` เพิ่ม helper เดียว:

```csharp
/// <summary>The per-connection backend-notification URL this charge must call back on:
/// {PublicBaseUrl}/api/v1/webhooks/{pspConnectionId}. Derived, never configured per deployment — a
/// global callback URL cannot carry the connection id the webhook route (and the per-company isolation)
/// requires (REQ-4.1).</summary>
protected string WebhookUrlFor(Guid pspConnectionId) =>
    $"{Options.PublicBaseUrl.TrimEnd('/')}/api/v1/webhooks/{pspConnectionId:D}";
```

- `TwoCTwoPAdapter`: `backendReturnUrl = WebhookUrlFor(pspConnectionId)` และ
  `paymentChannel = new[] { TwoCTwoPChannel(session.Method) }` โดย mapping `card -> "CC"` (method อื่น
  ไม่ถึงที่นี่แล้วเพราะ REQ-6.2 กันที่ create-session; ถ้าถึงให้ throw ระบุ method) — REQ-6.3/6.4.
- `OmiseAdapter` ไม่ใช้ `WebhookUrlFor` (Omise ตั้ง webhook endpoint จาก dashboard) -> งาน ops ใน runbook
  ตาม REQ-4.5.
- **config surface (REQ-4.3/4.6) — ทางที่ไม่ทำให้ test เดิมล้ม:** `AddOptions<>().ValidateOnStart()`
  จะทำให้ Hosts.Tests **17 ไฟล์ที่ boot host จริง** ล้มทั้งชุด (ไม่มี shared harness; `appsettings.json`
  ไม่มี section `Psp` เลย). ใช้ pattern ที่ repo มีอยู่แทน:
  1. `appsettings.json` เพิ่ม `"Psp": { "PublicBaseUrl": "" }` เป็น placeholder (แบบเดียวกับ blank
     password ที่ `Program.cs:138-143`).
  2. ใน block `if (!builder.Environment.IsDevelopment())` (`Program.cs:141-151`) เพิ่ม
     `ProvisioningGuards.RequirePublicBaseUrl(builder.Configuration)` (หรือ overload ของ
     `RequireInjectedCredential` ที่เหมาะกว่า) — fail fast เฉพาะ non-Development, unit-testable ใน
     `tests/Hosts.Tests/ProvisioningGuardsTests.cs` ที่มีอยู่ **โดยไม่ต้อง boot host**.
  3. `docker-compose.prod.yml` (`Psp__PublicBaseUrl: ${PSP_PUBLIC_BASE_URL:?...}` เข้า,
     `Psp__TwoCTwoP__BackendReturnUrl` ออก), `.env.prod.example`, และ env ของ CI job ที่รัน
     `docker compose config` ใน `.github/workflows/ci.yml` **และ** `.gitlab-ci.yml` ถ้ามี render check.

### D8 — fetch-to-confirm พายอดกลับมา

```csharp
// Payments.Application/Ports/PspContracts.cs
/// <summary>A server-confirmed charge: the normalized status plus the amount the PSP reports having
/// collected, when its response carries one. Amount is nullable because the PSP response contract for
/// it is not sandbox-verified for every path — a null means "status-only confirmation", never "zero".</summary>
public sealed record PspChargeConfirmation(PspChargeStatus Status, Money? Amount);
```

`IPspAdapter.FetchChargeAsync` คืน `PspChargeConfirmation` แทน `PspChargeStatus`.
- `TwoCTwoPAdapter`: อ่าน `amount` + `currencyCode` จาก paymentInquiry claims (มีอยู่ใน response ของ 4.3).
- `OmiseAdapter`: อ่าน `amount` (minor units) + `currency` แล้วแปลงกลับเป็น major unit ด้วย
  `Iso4217.MinorUnitDigits`.
- ทั้งสอง: field หาย/ผิดชนิด -> `Amount = null` (ไม่ throw).

`HandlePspWebhookHandler` ระหว่างขั้น fetch กับ `MarkPaid`:

```csharp
var confirmed = await adapter.FetchChargeAsync(evt.ExternalChargeId, secret, ct);
if (confirmed.Status != PspChargeStatus.Paid)
    return WebhookOutcome.Ignored;

// ... resolve session ...

// REQ-8.2: the PSP-reported amount must match what the order backs. A null means the PSP response did
// not carry one (REQ-8.3) — status-only confirmation, the pre-existing behavior.
if (confirmed.Amount is { } paid && !(paid.Amount == session.Amount.Amount && paid.SameCurrencyAs(session.Amount)))
    return WebhookOutcome.Ignored;
```

`Ignored` เพราะ: ไม่เปลี่ยน state, ไม่ enqueue, ตอบ 200 (PSP ไม่ retry ไม่รู้จบ), และ outcome ปรากฏใน
response ให้ ops เห็น. **ไม่** เปลี่ยน idempotency key / ลำดับ transaction / สัญญา `PaymentPaid` — REQ-8.4.

### D8a — amended 2026-07-28 (live 2C2P sandbox E2E): claim ต้อง atomic กับ transition ไม่ใช่ก่อน fetch

D10 เดิมยืนยัน "webhook ingest order (resolve -> verify -> **idempotency** -> fetch)" เป็นสิ่งที่ **ไม่แตะ**
(pre-existing, out of scope). พิสูจน์สดบน 2C2P sandbox วันนี้ (จ่ายจริง 20 THB) จับได้ว่าลำดับนั้นเป็นบั๊ก
money-path จริง ไม่ใช่แค่ debt: `TryBeginAsync` `SaveChangesAsync` ภายใน transaction เดียวกับทั้ง handler
ซึ่ง **commit ทุก return ปกติ** (รวม `Ignored`) — ดังนั้น notification ที่มาถึง**ก่อน** `FetchChargeAsync`
เห็นสถานะจ่ายจริง (เช่น PSP ยังไม่อัปเดต paymentInquiry ทัน หรือ 2C2P portal กด resend) จะเผา key
`charge:{invoice}:Paid` ทิ้งบน `Ignored` แล้วทำให้ notification ที่ตามมา**หลัง**จ่ายจริงกลายเป็น `Duplicate`
ตลอดกาล — session ค้าง `Redirected` ทั้งที่ลูกค้าจ่ายเงินแล้ว ไม่มีทางสะสางเองได้ (ต่างจาก gap 25 ใน
`docs/reference/platform-modules.md` ซึ่งเป็นเรื่อง `StartRedirectHandler`/settle path คนละ handler).

**แก้:** ย้าย claim (`_idempotency.TryBeginAsync`) ไปท้ายสุดของ transaction — **หลัง** status gate และ amount
check, **atomic คู่กับ** `session.MarkPaid` + `_outbox.Enqueue`. ลำดับใหม่: resolve -> verify -> fetch-to-
confirm -> เทียบยอด -> **idempotency claim** -> `MarkPaid` -> enqueue -> commit. ผลคือ `Ignored` ไม่เผา key
อีกต่อไป — concurrency ไม่เปลี่ยน (unique-key insert ยังเป็นตัวตัดสิน two racing `Paid` deliveries เหลือ
`Processed` เดียว), เคส `Duplicate` ของ event ที่ settle แล้วยังทำงานถูกต้อง. นี่คือ **REQ-8.5** (ใหม่);
D10 ด้านล่างถูกแก้ลำดับให้ตรงตาม.

### D9 — provisioning vocabulary + seed

- `ProvisionMerchantHandler.cs:63` เปลี่ยนจาก `Trim()` เป็น `PaymentMethods.Normalize(m)` ต่อรายการ
  (ค่าที่ไม่รู้จัก -> `ArgumentException` 400) — REQ-3.7.
- `docker/bootstrap/seed-demo.sql`: session ที่ seed ต้องใช้ `card` เท่านั้น (`:387`); `EnabledMethods`
  ของ connection คงเดิมได้ (สะท้อนข้อตกลงเชิงพาณิชย์) เพราะ REQ-6.2 เป็นด่านที่ปฏิเสธชัดเจนอยู่แล้ว —
  REQ-6.5.

### D10 — ไม่แตะ (ยืนยันเชิงบวก)

`SessionStatus` (ค่าเดิม 5 ค่า), webhook ingest order **นอกเหนือจากตำแหน่ง claim** (resolve -> verify ->
fetch -> เทียบยอด -> **idempotency** -> transition — ดู D8a สำหรับการย้าย claim), idempotency keys,
outbox/dispatcher, `Order`/`OrderStatus`/`OrderPaidConsumer`, vault + reveal audit,
RBAC keys (`payment.create`/`payment.redirect` เดิมพอ), route paths, `OmiseAdapter.VerifyWebhook`
(Non-Goal 1), frontend return URLs (REQ-4.4), rate limiter.

## Test strategy

| ชั้น | ที่ไหน | พิสูจน์อะไร |
|---|---|---|
| unit (domain) | `tests/Payments.Tests` | `PaymentMethods.Normalize/IsKnown`; `Connection.EnsureEligible` ทุก branch |
| unit (handler) | `tests/Payments.Tests` (ต้องสร้างไฟล์ fakes ใหม่ — ยังไม่มี, เทียบ `tests/Carts.Tests/Fakes.cs`) | `CreateSessionHandler` ทั้ง 8 ขั้น: 400 method แปลก, 404 order, 409 order ไม่ awaiting, 409 ไม่มี connection, 409 connection ปิด, 409 adapter ไม่ support, idempotent-return ช่องทางเดิม, 409 ช่องทางต่าง, happy path amount == order.Amount |
| unit (handler) | `tests/Payments.Tests` | `StartRedirectHandler`: ปฏิเสธ ineligible **ก่อน** claim (status ยัง `Created`, vault ไม่ถูกเรียก); charge ล้ม -> session `Failed` + rethrow; fail-then-retry เปิดใบใหม่ได้ |
| unit (adapter) | `tests/Payments.Tests/Psp` | 2C2P: `backendReturnUrl == {PublicBaseUrl}/api/v1/webhooks/{id}`, `amount` == session amount, `paymentChannel` มาจาก method; ทั้งสอง PSP: `FetchChargeAsync` คืน amount เมื่อ response มี และ null เมื่อไม่มี |
| unit (webhook) | `tests/Payments.Tests` | amount mismatch -> `Ignored`, ไม่ `MarkPaid`, ไม่ enqueue; amount null -> ทำงานเหมือนเดิม |
| offline model | `tests/Architecture.Tests` | index `IX_PaymentSessions_OrderId_Open` unique + filter ตรงตัว ในทั้งสอง context |
| host | `tests/Hosts.Tests` | wire contract ใหม่ (ไม่มี `amount`), status code ต่อ REQ, `ProvisioningGuards` ของ `Psp:PublicBaseUrl` (ไม่ต้อง boot host), suite เดิมยังเขียวเท่าเดิม |
| integration | `tests/Integration.Tests` (`Category=Integration`) | filtered unique index จริงบน SQL Server -> `ConflictException`; `sys.indexes.has_filter` |

## Requirement Traceability

| REQ | Section | ปิดด้วย |
|---|---|---|
| REQ-1 | 1.1, 1.4 | D4 (ตัด `Amount`, ใช้ `order.Amount`) |
| REQ-1 | 1.2 | D1 (query filter -> null -> `NotFoundException` 404) |
| REQ-1 | 1.3 | D4 ขั้น 3 |
| REQ-1 | 1.5 | D4 (การตรวจอยู่ใน `CreateSessionHandler`) |
| REQ-1 | 1.6 | Test strategy (host wire-pin + adapter amount test) |
| REQ-1 | 1.7 | D1 (port + impl ที่ `Persistence.MerchantRuntime`, ไม่ใช้ `GetOrderDetailCommand`) |
| REQ-2 | 2.1, 2.2 | D4 ขั้น 7 + D5 (`GetOpenForOrderAsync`) |
| REQ-2 | 2.3 | D4 ขั้น 3 + test |
| REQ-2 | 2.4 | D5 (named filtered unique index ทั้งสองไฟล์ + migration) |
| REQ-2 | 2.5 | D5 (`MerchantRuntimeUnitOfWork` 2627/2601 -> `ConflictException`) |
| REQ-2 | 2.6 | D5 (offline model assertion ใน `Architecture.Tests`) |
| REQ-2 | 2.7 | D5a (สะสางแถวซ้ำใน migration ก่อน `CreateIndex`, หยุดพร้อม `OrderId` ถ้าเหลือใบผูก charge หลายใบ) |
| REQ-3 | 3.1, 3.2, 3.6 | D3 (`EnsureEligible`, guard จุดเดียว, `Supports` มี call site) |
| REQ-3 | 3.3 | D4 ขั้น 4 |
| REQ-3 | 3.4 | D2 (`PaymentMethods.Normalize`) + D4 ขั้น 1 |
| REQ-3 | 3.5 | D6 ขั้น 4 (ก่อน claim) |
| REQ-3 | 3.7 | D9 (`ProvisionMerchantHandler` ใช้ vocabulary เดียวกัน) |
| REQ-4 | 4.1 | D7 (`WebhookUrlFor` + signature ใหม่) |
| REQ-4 | 4.2 | D7 (ลบ `BackendReturnUrl` ทุกที่) |
| REQ-4 | 4.3, 4.6 | D7 (placeholder ใน `appsettings.json` + `ProvisioningGuards` non-Development) |
| REQ-4 | 4.4 | D10 (ไม่แตะ frontend return URL) |
| REQ-4 | 4.5 | D7 (runbook `deploy-self-host.md`) |
| REQ-5 | 5.1, 5.2, 5.3 | task เอกสารปิดท้าย (tasks.md task 7) |
| REQ-6 | 6.1 | D2 (`IPspAdapter.SupportedMethods`) |
| REQ-6 | 6.2 | D4 ขั้น 6 |
| REQ-6 | 6.3, 6.4 | D7 (`paymentChannel` จาก method) + D4 ขั้น 6 กันไม่ให้ถึง adapter |
| REQ-6 | 6.5 | D9 (seed-demo) |
| REQ-7 | 7.1, 7.2 | D6 ขั้น 7 (`MarkFailed` + save + rethrow) |
| REQ-7 | 7.3 | D6 ขั้น 4 |
| REQ-7 | 7.4 | D6 + D5 (filter ของ index ไม่รวม `Failed`) + test fail-then-retry |
| REQ-7 | 7.5 | D6a (definitive vs ambiguous; ambiguous คง claim ไม่ `MarkFailed`) |
| REQ-7 | 7.6 | D6a (re-entry ของ `Redirected` + `RedirectUrl == null` เรียก PSP ซ้ำด้วย key เดิม) |
| REQ-8 | 8.1 | D8 (`PspChargeConfirmation` + 2 adapter) |
| REQ-8 | 8.2 | D8 (เทียบก่อน `MarkPaid`, mismatch -> `Ignored`) |
| REQ-8 | 8.3 | D8 (`Amount = null` -> status-only) |
| REQ-8 | 8.4 | D10 (ไม่แตะ idempotency/outbox/สัญญา) |

## Risks

1. **wire-contract break** — `POST /api/v1/payments/sessions` ไม่รับ `amount`. pre-prod ไม่มี consumer
   ภายนอก (ไม่มี test ใดสร้าง request นี้เลย) แต่ต้องแจ้งใน PR body ให้ทีม FE.
2. **`Psp:PublicBaseUrl` เป็น required ใหม่ใน non-Development** — deployment ที่ยังไม่ตั้งจะไม่ boot
   (เจตนา). ต้องเพิ่มใน compose/.env.prod.example/CI ทุก workflow ในเดียวกัน ไม่งั้น CI แดง.
3. **filtered index กับข้อมูลเดิม** — dev DB ที่มี open session ซ้ำต่อ order อยู่แล้ว migration จะ fail.
   `seed-demo.sql:381-405` มี 1 session ต่อ order จึงปลอดภัย; ถ้าเจอซ้ำให้ `docker compose down -v`.
4. **`FetchChargeAsync` เปลี่ยน return type** — เป็น breaking change ภายใน แตะ 2 adapter + webhook
   handler + adapter tests. ต้องไม่ทำให้ `Ignored` กลืน mismatch เงียบ: outcome ปรากฏใน response body.
5. **rename gate** — token เกษียณ (`PaymentSession`, `PspConnection`, `Line` เปล่า, `CartItem`,
   `CheckoutSession`, `MasterData*`, `OrderLine`) ห้ามโผล่เป็น identifier ใหม่. ชื่อที่ design นี้ใช้
   (`PayableOrder`, `PaymentMethods`, `EnsureEligible`, `GetOpenForOrderAsync`, `WebhookUrlFor`,
   `PspChargeConfirmation`, `SupportedMethods`) ตรวจแล้วไม่ชน.
6. **สอง `SessionConfiguration`** — ใส่ index ใบเดียวจะได้ unit test เขียวแต่ DDL ไม่มี index และ CI ก็ไม่
   จับ (integration job ถูกข้าม) -> REQ-2.6 มีไว้เพื่อกันข้อนี้.
</content>
