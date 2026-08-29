# Tasks: bugfix-producer-ticket-dedup
> Status: superseded 2026-07-01 by producer-google-sso

- [x] T1. เพิ่ม `HasPendingAsync(subject, email, now, ct)` ใน port + impl
  - `src/Modules/Producer/Producer.Application/ProducerPorts.cs`: เพิ่ม method ใน
    `IRegistrationTicketRepository`
  - `src/Modules/Producer/Producer.Infrastructure/Persistence/ProducerRepositories.cs`:
    impl ใน `RegistrationTicketRepository` — `AnyAsync` filter
    `UsedAt == null && ExpiresAt > now && (Subject == subject || Email == email)`, `AsNoTracking`

- [x] T2. guard ใน `IssueTicketAndRedirectAsync` + 409 helper
  - `src/Hosts/Api/ProducerLoginService.cs`: ก่อน `RegistrationTicket.Issue(...)` เช็ค
    `HasPendingAsync`; ถ้า true -> `RespondRegistrationPendingAsync` (409 plain text) แล้ว return
  - helper mirror `RespondAwaitingApprovalAsync` (`:190-197`) — ไม่ออก ticket, ไม่ audit

- [x] T3. tests (repro RED->GREEN + B-IDs 1:1)
  - `tests/Hosts.Tests/ProducerLoginServiceTests.cs` (+ fake `ctx.Tickets` รองรับ
    `HasPendingAsync` + seed pending ticket)
  - repro: callback NotFound subject เดิมรอบ 2 (pending จากรอบแรก) -> `Tickets.Added` 1 row,
    response 409, ไม่มี session cookie (F1, F2)
  - assertion: subject ต่าง แต่ email ตรง -> ยัง block (F1 secondary)
  - B3: seed expired pending -> ออก ticket ใหม่ได้ (Added +1)
  - B1/B2/B4: เทสเดิม NotFound/Rejected/Pending ยัง green (no pending seeded)
  - B5/B6: ไม่แตะ — Producer.Tests `SubmitRegistrationHandlerTests` / `RegistrationTicketTests` เดิมยัง green

- [x] T4. verify: `dotnet build` 0 error; `dotnet test tests/Hosts.Tests` + `dotnet test tests/Producer.Tests` green
