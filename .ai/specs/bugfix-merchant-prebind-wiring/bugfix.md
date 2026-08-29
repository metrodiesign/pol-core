# Bugfix: merchant-user identity flows มองไม่เห็นแถว pending/rejected — pre-bind ports ไม่เคยถูก wire เข้า DI
> Status: approved 2026-07-26

## Current Behavior (Defect)

สาม defect เกี่ยวเนื่องกัน ทั้งหมดถูกกลบใน dev ด้วย `Merchant:DevMerchantId` fallback
(`src/Hosts/Api/HttpActorContext.cs:34-39`) จึงไม่เคยโผล่ใน manual test บนเครื่อง dev
บน production config (ไม่มี DevMerchantId) merchant-user identity plane ใช้งานไม่ได้ทั้งเส้น
ตั้งแต่ rls-to-query-filter task 8 (PR #112)

### D1 — read: query filter ซ่อนแถว pending/rejected จากทุก pre-bind caller

WHEN caller ที่ยังไม่มี bound merchant actor (OIDC callback, admin plane, session
authentication) resolve บัญชี merch.Users ที่ `Status = PendingApproval | Rejected`
(ซึ่ง `MerchantId IS NULL` เสมอ) THEN แถวนั้นล่องหน — `FindBySubjectAsync` /
`FindByIdAsync` คืน null

- Root cause: global query filter `MerchantId == CurrentMerchant`
  (`src/Persistence/Persistence.MerchantUsers/Users/UserConfiguration.cs:28`) โดยที่
  `CurrentMerchant = _actor.HasActor ? _actor.MerchantId : Guid.Empty`
  (`MerchantUserDbContext.cs:35`); NULL ไม่เท่ากับค่าใดใน SQL
- Pre-bind ports ที่ rls-to-query-filter task 5 สร้างไว้แก้เรื่องนี้
  (`MerchantResolveLoginBySubject.cs:15`, `MerchantRegistrationSubmitWriter.cs:50`,
  `MerchantRegistrationWriter.cs:39,44`) ไม่เคยถูก register ใน DI — task 8 register
  `IUserRepository = MerchantUserRepository` ตัวติด filter แทน
  (`MerchantUserPersistenceRegistration.cs:42`, `MerchantUserRepositories.cs:27-31`)
  spec บันทึกการ "defer the DI flip" ไว้แล้วหล่นหาย
  (`.ai/specs/rls-to-query-filter/tasks.md:290-295`)
- Consequence chain:
  - rejected user login ใหม่ -> `ResolveLoginHandler` เห็นเป็น NotFound -> mint
    `TicketPurpose.Registration` แทน Correction -> submit ชน `UNIQUE(Subject)` ->
    **409 ทางตัน** (resubmit ตาม REQ-5 ของ producer-google-sso ใช้ไม่ได้จริง)
  - admin `POST /api/v1/admins/merchants/users/{subject}/approve|reject` ->
    `FindBySubjectAsync` คืน null -> **404** ทุก subject ที่ pending
    (`ApproveReject.cs:53,131`)
  - `ResolveByIdHandler` ที่ session auth handler เรียกทุก request
    (`UserSessionAuthenticationHandler.cs:106`) ก็วิ่งผ่าน filter เดียวกัน

### D2 — write: write floor ปฏิเสธ admin approve

WHEN admin approve ผ่านขั้น read ได้ (สมมุติ D1 ถูกแก้) THEN commit ล้มเหลวด้วย
`WriteGuardException` — approve ตั้ง `User.MerchantId` เป็นค่าจริง + insert
`RoleAssignment` (tenant-keyed) แต่ `MerchantRequestWriteAuthorizer.CanWrite`
ยอมรับ non-empty `targetMerchant` เฉพาะเมื่อมี bound merchant actor ตรงกัน
(`src/Hosts/Api/Persistence/WriteAuthorizers.cs:106-115`) ซึ่ง admin-plane request
ไม่มีวันมี (`ResolveMerchantWriteAuthorizer`, `Program.cs:171-173`)
failure class เดียวกับ admin-login-write-guard (PR #124) — repo เขียน comment เตือน
เรื่องนี้ไว้เองที่ `Program.cs:176-180` (`AdminItemPolicyWriteAuthorizer` precedent)
(reject ไม่โดน D2 เพราะ tenant key ของแถว pending ยัง NULL -> targetMerchant = Guid.Empty)

### D3 — actor: HttpActorContext snapshot claims ก่อน authentication

WHEN request ที่ authenticate ด้วย merchant-user session cookie ถูกประมวลผล THEN
`CurrentMerchant` ค้างเป็น `Guid.Empty` ตลอด request แม้ principal มี `merchant_id` claim

- Root cause: `HttpActorContext` อ่าน claims ใน constructor
  (`HttpActorContext.cs:29-43`) และเป็น Scoped — มันถูก construct ระหว่าง session
  authentication (auth handler ctor -> `ISessionStore` -> `MerchantUserDbContext` ->
  `IActorContext`) ก่อนที่ handler จะตั้ง principal ที่
  `UserSessionAuthenticationHandler.cs:122` -> snapshot ว่างค้างทั้ง scope
- Consequence: post-login bound flows (products/carts/roles ที่พึ่ง
  `CurrentMerchant`) อ่านไม่เห็นข้อมูล merchant ตัวเอง + write โดน deny บน production

### Repro (รันได้จริง)

Programmatic (จะกลายเป็น RED test ของ spec นี้): lifecycle test ที่ประกอบ real DI
adapters จาก `MerchantUserPersistenceRegistration` + real `MerchantRequestWriteAuthorizer`
บน SQLite in-memory (SQLite ประเมิน EF query filter ได้ — พิสูจน์แล้วโดย
`MerchantRegistrationWriterTests`) ด้วย actor ที่ `HasActor = false`:
1. `SubmitRegistrationHandler` (Registration) สร้างบัญชี -> สำเร็จ (insert ไม่ผ่าน filter)
2. `ResolveLoginHandler` ด้วย subject เดิม -> **observed: NotFound** (expected: PendingApproval)
3. `ApproveHandler` / `RejectHandler` ด้วย subject เดิม -> **observed: NotFoundException/404**
   (expected: สำเร็จ)
4. (หลังแก้ D1) `ApproveHandler` -> **observed: WriteGuardException** (expected: Active + role)

Manual บน dev: ลบ `Merchant:DevMerchantId` ออกจาก `appsettings.Development.json` แล้ว
เดิน flow register -> admin reject -> login ใหม่ -> observed: redirect ไปหน้า register
พร้อม Registration ticket (ไม่ใช่ Correction) -> submit -> 409

## Expected Behavior

- F-1 WHEN caller ใด ๆ (bound หรือไม่) resolve login ด้วย subject ที่มีบัญชีอยู่ THE SYSTEM SHALL คืนสถานะจริงของบัญชี (PendingApproval/Active/Rejected/Suspended) โดยไม่ขึ้นกับค่า `MerchantId` ของแถว — rejected user ได้ `TicketPurpose.Correction` ตาม REQ-5 เดิม
- F-2 WHEN rejected user ส่งคำขอลงทะเบียนซ้ำ (Correction submit) THE SYSTEM SHALL resubmit แถวเดิม (`Rejected -> PendingApproval` ผ่าน `User.Resubmit()`) และตอบ 201 — ไม่ใช่ 409
- F-3 WHEN admin ที่ถือ `merchants.users.approve` และ merchant เป้าหมายอยู่ใน accessible set approve บัญชี pending THE SYSTEM SHALL ทำงานครบใน tx เดียว: `Status = Active`
     + set `MerchantId` + insert `RoleAssignment` + append `RegistrationAudits` โดย
     write floor อนุญาต
- F-4 WHEN admin ที่ถือ `merchants.users.reject` reject บัญชี pending THE SYSTEM SHALL ทำงานครบ: `Status = Rejected` + revoke ทุก session + append audit พร้อม reason (read ไม่ 404 อีก)
- F-5 WHEN session authentication ตั้ง principal ที่มี `merchant_id` claim แล้ว THE SYSTEM SHALL เห็น claim นั้นในส่วนที่เหลือของ request เดียวกัน (`IActorContext.HasActor = true`, `MerchantId` = ค่า claim) — การอ่าน claims ต้อง lazy ไม่ snapshot ที่ constructor
- F-6 WHEN session auth handler re-resolve บัญชีของ caller ด้วย id (pre-bind by construction) THE SYSTEM SHALL พบบัญชีเสมอไม่ว่าสถานะ/merchant ใด

## Unchanged Behavior

- B-1 WHEN subject เดิม submit Registration ซ้ำ (ไม่ใช่ Correction) THE SYSTEM SHALL CONTINUE TO ตอบ 409 ผ่าน `UNIQUE(Subject)` -> `ConflictException` mapping เดิม
- B-2 WHEN bound in-session flow (เช่น `SetUserRolesHandler`) อ่านผ่าน `IUserRepository` THE SYSTEM SHALL CONTINUE TO เห็นเฉพาะแถวของ merchant ตัวเอง (query filter คงเดิม สำหรับ bound reads)
- B-3 WHEN admin-plane write แตะ entity นอกชุด approve (product plane ฯลฯ) หรือ merchant นอก accessible set THE SYSTEM SHALL CONTINUE TO deny ที่ write floor
- B-4 WHEN unbound self-service registration เขียน entity ที่ tenant key เป็น NULL/Empty THE SYSTEM SHALL CONTINUE TO อนุญาตตาม carve-out เดิม และ non-empty target อื่น ยังต้องมี bound actor ตรงกัน (ยกเว้น outbox sentinel เดิม)
- B-5 WHEN suspended user พยายาม login THE SYSTEM SHALL CONTINUE TO deny พร้อม `?reason=suspended` + append auth-denied audit
- B-6 WHEN pending user login THE SYSTEM SHALL CONTINUE TO redirect `?reason=awaiting-approval` โดยไม่สร้าง session
- B-7 WHEN Correction ticket ถูก submit ให้บัญชีที่ไม่ใช่ Rejected THE SYSTEM SHALL CONTINUE TO ปฏิเสธ (guard ใน `User.Resubmit()` คงเดิม)
- B-8 WHEN admin approve บัญชีที่ไม่ pending หรือ subject ไม่รู้จัก หรือ merchant เป้าหมายอยู่นอก accessible set THEN THE SYSTEM SHALL ปฏิเสธด้วย 409, 404, 404 (no-existence-leak) ตาม host guard เดิม
      merchant เป้าหมายนอก accessible set -> 404 (no-existence-leak) — พฤติกรรม host
      guard เดิมทั้งหมด (`Program.cs:1459-1496`)
- B-9 WHEN spec นี้ถูก implement THE SYSTEM SHALL คง outbox event `MerchantUserRegistrationSubmitted`, routes, config keys, auth schemes และ DB schema เดิมทั้งหมด โดยไม่เพิ่มตารางใหม่หรือ migration
      routes, config keys, auth schemes, DB schema — spec นี้ไม่มีตารางใหม่/ไม่มี migration
- B-10 WHEN webhook bind `AmbientActor` หรือ background dispatch ใช้ scope-discriminated authorizer THE SYSTEM SHALL CONTINUE TO ทำงานเดิม (precedence `AmbientActor` > claims > dev fallback คงเดิม)
- B-11 WHEN dev config ตั้ง `Merchant:DevMerchantId` และ request ไม่มี merchant_id claim THE SYSTEM SHALL CONTINUE TO ใช้ fallback merchant เดิม (dev-only convenience)

## Scope constraints (hard — งานใดแตะไฟล์กลุ่มนี้ = spec conflict)

- ห้ามแตะ: EF migrations ทุกไฟล์, `Merchants.Domain/Users/User.cs` state machine,
  outbox contract (`MerchantUserRegistrationSubmitted`), OIDC/session scheme config,
  `.env*`
- แก้ที่: application ports + persistence adapters + DI composition + write authorizers
  + `HttpActorContext` + tests เท่านั้น (รายการไฟล์ตาม approved plan
  `/Users/king_developer/.claude/plans/harmonic-conjuring-treasure.md` Phase 1)
