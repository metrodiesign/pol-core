# Plan Review Log: Admin Account Management (six additive endpoints)

Act 1 (grill) complete — plan locked with the user. MAX_ROUNDS=5.

Act 1 record: scope + workflow locked in plan mode (6 endpoints, no
hard-DELETE/email/tier, full spec gates); /spec-analyze resolved 9 findings (all
recommendations accepted); grill codebase pass corrected REQ-1.5 to the SFS
silent-drop convention (finding 10); grill Q1 decided reactivate revokes all target
sessions on the Suspended→Active transition (finding 11). Spec:
`.ai/specs/admin-account-management/requirements.md`.

## Round 1 — Codex

พบ material problems:

1. PLAN:52 — list endpoint วางบน `admin` group ไม่ได้: group root ให้ trailing slash; ต้อง `api.MapGet("/admins")` + metadata เอง (precedent Program.cs:1304).
2. Route ไม่ระบุ `{id:guid}` — group มี `/me`,`/roles`,`/permissions` literal อยู่; บังคับ guid constraint + convention tests.
3. PLAN:48 — `ListEffectivePermissionsAsync` คืน empty set สำหรับ id ที่ไม่มีจริง (ไม่เช็ค account existence) → 404 ของ REQ-6.3 ต้องเช็ค `GetByIdAsync` ก่อน.
4. PLAN:56 — logout precedent ปัจจุบัน revoke แล้วค่อย audit ไม่มี explicit txn; ต้องทำเป็น Application commands (`ReactivateAdminCommand`/`RevokeAdminSessionCommand`) ใช้ keyed `IUnitOfWork.ExecuteInTransactionAsync` ห้าม host compose.
5. PLAN:98 — transaction "open question" จริงๆ ปิดได้แล้ว: AdminScopedServices.cs:77 + AdminHostWiring.cs:179 ยืนยัน DbContext keyed ตัวเดียว → เปลี่ยนจาก verify เป็น enforce + rollback test.
6. PLAN:28 — id tiebreak มีแค่ default sort; explicit sort ต้อง `ThenBy(Id)` ทุก chain กัน paging unstable.
7. PLAN:67 — rationale ขัดกัน: `user.roles`-only holder มองรายชื่อไม่ได้ทั้งที่ต้อง assign; เสนอ OR-gate หรือ document role composition.
8. Audit schema ไม่มีช่อง sessionId/familyId — migration หรือยอมรับ + structured log.
9. Integration tests "only if grants touched" ต่ำไป — ExecuteUpdate/txn/route binding อยู่ใต้ fakes.

VERDICT: REVISE

### Claude's response (round 1)

- ACCEPT 1,2: list/root maps on `api` (per-endpoint CSRF+auth), ทุก id route = `{id:guid}`, host tests ครอบ no-trailing-slash.
- ACCEPT 3: effective-permissions handler เช็ค existence ก่อน (REQ-6.3 เดิมบังคับ 404 อยู่แล้ว — แก้ที่ระดับ design/plan ไม่ใช่ REQ).
- ACCEPT 4,5: บังคับเป็น Mediator commands + keyed UoW ทุก mutation; ย้าย transaction จาก Risks → decided (wiring ยืนยันแล้ว); เพิ่ม rollback integration test.
- ACCEPT 6: `AdminAccountSfs` append `ThenBy(Id)` ท้ายทุก sort chain; amend REQ-1.3.
- PARTIAL 7: คง `user.view` gate เดี่ยว — REJECT OR-gate (RequirePermission เป็น single-key filter; เพิ่ม OR variant = infra ใหม่เกินเหตุ). แก้ rationale + เพิ่มคำแนะนำ role composition (role ที่ให้ user.roles ควรรวม user.view) ลง docs task.
- ACCEPT 8 แบบ (b): ไม่ migration — audit ตอบ "ใคร revoke session ของใคร เมื่อไร" ผ่าน action+target+correlation; session id อยู่ใน structured security log ผูก correlation id. Documented ใน plan + spec edge note.
- ACCEPT 9: เพิ่ม Integration.Tests: reactivate atomic rollback (revoke+status+audit), revoke-family ownership/idempotency; Hosts.Tests: root route.

## Round 2 — Codex

Findings รอบ 1: ปิดครบ 9 ข้อ (root route, {id:guid}, effective-perms 404, session/audit atomicity via command+keyed UoW, stable-sort tiebreak, user.roles-vs-user.view role composition, audit granularity zero-migration log fallback, integration coverage).

New non-blocking hardening:
- A. DELETE session command รับ orphan `AdminSession.AdminAccountId` ได้ เพราะ `AdminSessions` ไม่มี FK ไป `AdminAccounts` (AdminSessionConfigurations.cs:19). Fix: เช็ค route admin exists ก่อน accepted revoke/audit.
- B. structured security log เป็น observability source ของ "session ไหน" แต่ test พูดถึงแค่ revoke behavior. Fix: เพิ่ม assertion ครอบ sessionId/familyId/targetAdminId/correlationId.

VERDICT: APPROVED

### Claude's response (round 2)

- ACCEPT A: RevokeAdminSessionCommand handler เช็ค account existence ก่อน (เหมือน sessions-list + effective-perms) — 404 ถ้า route admin ไม่มี, ก่อนแตะ session/audit. เข้ากับ existence-check pattern ที่รับไปแล้ว.
- ACCEPT B: เพิ่ม test assert structured security log carries sessionId/familyId/targetAdminId/correlationId บน revoke.
- อัปเดต PLAN §6 + tests; ทั้งคู่ additive ไม่กระทบ scope/REQ.

Converged round 2/5. Awaiting user sign-off.
