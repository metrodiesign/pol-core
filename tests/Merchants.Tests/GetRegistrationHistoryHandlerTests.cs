using BuildingBlocks.Application;
using Merchants.Application.Users;
using Merchants.Domain.Users;

namespace Merchants.Tests;

/// <summary>
/// The <see cref="GetRegistrationHistoryHandler"/> behaviors (registration-attempt-history REQ-2/3): attempts
/// ordered by AttemptNo, timeline from RegistrationAudits without <c>revealed</c> rows, PII masked by default
/// (constant <c>****</c> for short values — no length leak), full PII + ONE persisted <c>revealed</c> audit on
/// reveal (including an empty attempts list), 404 (null) without any audit, and fail-closed when the audit
/// save throws.
/// </summary>
public sealed class GetRegistrationHistoryHandlerTests
{
    private static readonly DateTime Now = new(2026, 8, 2, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Unknown_subject_returns_null_and_writes_no_audit()
    {
        var ctx = new Ctx();

        var result = await ctx.Handler.Handle(Query(Guid.NewGuid(), reveal: true), default);

        Assert.Null(result);
        Assert.Empty(ctx.Audits.Appended);       // REQ-3.6: a 404 reveal writes nothing
        Assert.Equal(0, ctx.Uow.SaveCalls);
    }

    [Fact]
    public async Task Default_response_masks_id_license_phone_and_email_but_never_names()
    {
        var ctx = new Ctx();
        var user = ctx.SeedUser("g-sub-1");
        ctx.SeedAttempt(user, 1, idNumber: "1234567890123", licenseNumber: "AB12", phone: "0812345678",
            email: "somchai@example.com");

        var result = await ctx.Handler.Handle(Query(user.Id), default);

        var attempt = Assert.Single(result!.Attempts);
        Assert.Equal("****0123", attempt.IdentityNumber);          // >4 → **** + last 4 (REQ-3.1)
        Assert.Equal("****", attempt.LicenseNumber);         // ≤4 → constant ****, no length leak
        Assert.Equal("****5678", attempt.Phone);
        Assert.Equal("s***@example.com", attempt.Email);     // REQ-3.2
        Assert.Equal("First", attempt.FirstName);            // names always full (REQ-3.3)
        Assert.Equal("Last", attempt.LastName);
        Assert.Empty(ctx.Audits.Appended);                   // masked read is not audited
    }

    [Fact]
    public async Task Null_pii_fields_stay_null_and_a_malformed_email_masks_entirely()
    {
        var ctx = new Ctx();
        var user = ctx.SeedUser("g-sub-1");
        ctx.SeedAttempt(user, 1, idNumber: null, licenseNumber: null, phone: null, email: "not-an-email");

        var result = await ctx.Handler.Handle(Query(user.Id), default);

        var attempt = Assert.Single(result!.Attempts);
        Assert.Null(attempt.IdentityNumber);                       // NULL → NULL (REQ-3.1)
        Assert.Null(attempt.LicenseNumber);
        Assert.Null(attempt.Phone);
        Assert.Equal("****", attempt.Email);                 // no '@' → fail-safe full mask
    }

    [Fact]
    public async Task Attempts_come_back_ordered_by_attempt_number()
    {
        var ctx = new Ctx();
        var user = ctx.SeedUser("g-sub-1");
        ctx.SeedAttempt(user, 2, email: "a@b.com");
        ctx.SeedAttempt(user, 1, email: "a@b.com");

        var result = await ctx.Handler.Handle(Query(user.Id), default);

        Assert.Equal([1, 2], result!.Attempts.Select(a => a.AttemptNo)); // REQ-2.2 (reader contract kept by the fake)
    }

    [Fact]
    public async Task Reveal_returns_full_pii_and_persists_exactly_one_revealed_audit()
    {
        var ctx = new Ctx();
        var user = ctx.SeedUser("g-sub-1");
        ctx.SeedAttempt(user, 1, idNumber: "1234567890123", licenseNumber: "AB12", phone: "0812345678",
            email: "somchai@example.com");

        var result = await ctx.Handler.Handle(Query(user.Id, reveal: true), default);

        var attempt = Assert.Single(result!.Attempts);
        Assert.Equal("1234567890123", attempt.IdentityNumber);     // REQ-3.4: full, response-wide
        Assert.Equal("AB12", attempt.LicenseNumber);
        Assert.Equal("0812345678", attempt.Phone);
        Assert.Equal("somchai@example.com", attempt.Email);

        var audit = Assert.Single(ctx.Audits.Appended);      // one row per request (REQ-3.5)
        Assert.Equal(RegistrationAuditAction.Revealed, audit.Action);
        Assert.Equal("admin-1", audit.ActorSubject);
        Assert.Equal("g-sub-1", audit.TargetSubject);
        Assert.Equal("corr-1", audit.CorrelationId);
        Assert.Equal(1, ctx.Uow.SaveCalls);                  // persisted, not merely staged
    }

    [Fact]
    public async Task Reveal_on_an_empty_attempts_list_still_persists_the_audit()
    {
        var ctx = new Ctx();
        var user = ctx.SeedUser("g-sub-1"); // registered before this feature deployed — no attempt rows (REQ-2.6)

        var result = await ctx.Handler.Handle(Query(user.Id, reveal: true), default);

        Assert.NotNull(result);
        Assert.Empty(result!.Attempts);
        Assert.Single(ctx.Audits.Appended);                  // G2: every revealed 200 is audited
        Assert.Equal(1, ctx.Uow.SaveCalls);
    }

    [Fact]
    public async Task A_failing_reveal_audit_save_fails_the_request_before_any_pii_is_built()
    {
        var ctx = new Ctx();
        var user = ctx.SeedUser("g-sub-1");
        ctx.SeedAttempt(user, 1, idNumber: "1234567890123", email: "somchai@example.com");
        ctx.Uow.ThrowOnSave = new InvalidOperationException("audit save failed");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ctx.Handler.Handle(Query(user.Id, reveal: true), default).AsTask()); // REQ-3.7 fail-closed
    }

    [Fact]
    public async Task Timeline_carries_lifecycle_rows_in_order_and_the_reader_excludes_revealed()
    {
        var ctx = new Ctx();
        var user = ctx.SeedUser("g-sub-1");
        ctx.SeedAttempt(user, 1, email: "a@b.com");
        ctx.History.Audits.Add(RegistrationAudit.For(
            RegistrationAuditAction.Registered, user.Id, "g-sub-1", "corr-a", Now));
        ctx.History.Audits.Add(RegistrationAudit.For(
            RegistrationAuditAction.Rejected, user.Id, "g-sub-1", "corr-b", Now.AddMinutes(5),
            actorAdminId: Guid.NewGuid(), actorSubject: "admin-1", reason: "photo unreadable"));

        var result = await ctx.Handler.Handle(Query(user.Id), default);

        Assert.Equal(
            [RegistrationAuditAction.Registered, RegistrationAuditAction.Rejected],
            result!.Timeline.Select(t => t.Action));
        Assert.Equal("photo unreadable", result.Timeline[1].Reason); // reject rationale rides along (REQ-2.3, G4)
    }

    // --- accessible-merchant floor (REQ-2.7, review PR #161) ---

    [Fact]
    public async Task A_scoped_admin_outside_the_targets_merchant_gets_null_with_no_audit_even_on_reveal()
    {
        var ctx = new Ctx();
        var merchant = Guid.NewGuid();
        var user = ctx.SeedUser("g-sub-1");
        ctx.SeedAttempt(user, 1, idNumber: "1234567890123", email: "somchai@example.com");
        user.Approve(merchant, Now); // Active, merchant-bound
        ctx.Accounts.Seed(user);     // re-project the snapshot with the bound MerchantId

        var result = await ctx.Handler.Handle(
            Query(user.Id, reveal: true, unrestricted: false, accessible: new HashSet<Guid> { Guid.NewGuid() }),
            default);

        Assert.Null(result);                 // same 404 as not-found — no existence leak
        Assert.Empty(ctx.Audits.Appended);   // the reveal branch is never reached
        Assert.Equal(0, ctx.Uow.SaveCalls);
    }

    [Fact]
    public async Task A_scoped_admin_inside_the_targets_merchant_reads_the_history()
    {
        var ctx = new Ctx();
        var merchant = Guid.NewGuid();
        var user = ctx.SeedUser("g-sub-1");
        ctx.SeedAttempt(user, 1, email: "somchai@example.com");
        user.Approve(merchant, Now);
        ctx.Accounts.Seed(user);

        var result = await ctx.Handler.Handle(
            Query(user.Id, unrestricted: false, accessible: new HashSet<Guid> { merchant }), default);

        Assert.NotNull(result);
        Assert.Single(result!.Attempts);
    }

    [Fact]
    public async Task A_scoped_admin_still_reads_a_pending_target_with_no_merchant_bound()
    {
        var ctx = new Ctx();
        var user = ctx.SeedUser("g-sub-1"); // PendingApproval — MerchantId NULL, not merchant-bound yet

        var result = await ctx.Handler.Handle(
            Query(user.Id, unrestricted: false, accessible: new HashSet<Guid>()), default);

        Assert.NotNull(result); // pending/rejected stay unrestricted (REQ-2.7)
    }

    private static GetRegistrationHistoryQuery Query(
        Guid merchantUserId, bool reveal = false, bool unrestricted = true, IReadOnlySet<Guid>? accessible = null) =>
        new(merchantUserId, reveal, ActorSubject: "admin-1", ActorAdminId: ActorId, CorrelationId: "corr-1",
            IsUnrestrictedAdmin: unrestricted, AccessibleMerchantIds: accessible ?? new HashSet<Guid>());

    private static readonly Guid ActorId = Guid.NewGuid();

    // --- fakes ---

    private sealed class Ctx
    {
        public FakeResolver Accounts { get; } = new();
        public FakeHistory History { get; } = new();
        public FakeAudits Audits { get; } = new();
        public FakeUow Uow { get; } = new();
        public GetRegistrationHistoryHandler Handler { get; }

        public Ctx() => Handler = new GetRegistrationHistoryHandler(
            Accounts, History, Audits, Uow, new FakeClock(Now));

        public User SeedUser(string subject)
        {
            var user = User.Register("google", subject, "somchai@example.com", Now);
            user.SetDetails("First", "Last", IdentityType.Individual, null, null, null, null);
            Accounts.Seed(user);
            return user;
        }

        public void SeedAttempt(User user, int attemptNo, string? idNumber = null, string? licenseNumber = null,
            string? phone = null, string email = "somchai@example.com")
        {
            user.SetDetails("First", "Last", IdentityType.Individual, idNumber, "PC-1", licenseNumber, phone);
            History.Attempts.Add(RegistrationAttempt.Capture(
                user, attemptNo, TicketPurpose.Registration, email, Now));
        }
    }

    private sealed class FakeClock(DateTime now) : IClock { public DateTime UtcNow => now; }

    private sealed class FakeResolver : IAccountResolver
    {
        private readonly Dictionary<string, AccountSnapshot> _bySubject = [];
        public void Seed(User user) => _bySubject[user.Subject] =
            new AccountSnapshot(user.Id, user.Subject, user.Email, user.MerchantId, user.Status);
        public Task<AccountSnapshot?> FindBySubjectAsync(string provider, string subject, CancellationToken ct) =>
            Task.FromResult(_bySubject.GetValueOrDefault(subject));
        public Task<AccountSnapshot?> FindByIdAsync(Guid id, CancellationToken ct) =>
            Task.FromResult(_bySubject.Values.FirstOrDefault(s => s.UserId == id));
    }

    /// <summary>Keeps the port's contract (AttemptNo order, no revealed rows) the way the real
    /// EF adapter's ORDER BY / WHERE do.</summary>
    private sealed class FakeHistory : IRegistrationHistoryReader
    {
        public List<RegistrationAttempt> Attempts { get; } = [];
        public List<RegistrationAudit> Audits { get; } = [];
        public Task<IReadOnlyList<RegistrationAttempt>> ListAttemptsAsync(Guid merchantUserId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<RegistrationAttempt>>(
                Attempts.Where(a => a.UserId == merchantUserId).OrderBy(a => a.AttemptNo).ToList());
        public Task<IReadOnlyList<RegistrationAudit>> ListAuditsAsync(Guid targetUserId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<RegistrationAudit>>(
                Audits.Where(a => a.TargetUserId == targetUserId && a.Action != RegistrationAuditAction.Revealed)
                    .OrderBy(a => a.OccurredAt).ToList());
    }

    private sealed class FakeAudits : IRegistrationAuditWriter
    {
        public List<RegistrationAudit> Appended { get; } = [];
        public void Append(RegistrationAudit audit) => Appended.Add(audit);
    }

    private sealed class FakeUow : IUserUnitOfWork
    {
        public int SaveCalls { get; private set; }
        public Exception? ThrowOnSave { get; set; }
        public Task<int> SaveChangesAsync(CancellationToken ct)
        {
            if (ThrowOnSave is not null) throw ThrowOnSave;
            SaveCalls++;
            return Task.FromResult(1);
        }
        public Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct) =>
            operation(ct);
    }
}
