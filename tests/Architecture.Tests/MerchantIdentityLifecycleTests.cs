using BuildingBlocks.Application;
using Merchants.Application.Users;
using Merchants.Application.Users.Roles;
using Merchants.Domain.Users;
using Merchants.Domain.Users.Roles;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Persistence.MerchantUsers;
using Persistence.MerchantUsers.Outbox;
using Persistence.MerchantUsers.Users;
using MerchantUserAccount = Merchants.Domain.Users.User;

namespace Architecture.Tests;

/// <summary>
/// bugfix-merchant-prebind-wiring T1: the reject → resubmit → approve lifecycle driven through the REAL
/// handlers + the REAL <c>Persistence.MerchantUsers</c> adapters that <c>MerchantUserPersistenceRegistration</c>
/// registers, on SQLite (which evaluates the EF global query filter), with the actor UNBOUND — exactly the
/// production shape of the OIDC callback, the anonymous register endpoint, and the admin plane. The write
/// floor is <see cref="GuardedRuntimeDbContext"/> itself; its <see cref="IWriteAuthorizer"/> is a mirror of
/// the Api host's composition (<c>Program.cs ResolveMerchantWriteAuthorizer</c>) because the real authorizer
/// classes are Api-internal and only visible to Hosts.Tests — the real classes are unit-tested there; this
/// file proves the flows END TO END (F1/F2/F3/F4/F6 of bugfix.md).
/// </summary>
public sealed class MerchantIdentityLifecycleTests : IDisposable
{
    private static readonly Guid MerchantA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid MerchantB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid ManagerRoleId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private static readonly Guid ActingAdminId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");

    private readonly SqliteConnection _connection;

    public MerchantIdentityLifecycleTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var setup = NewContext(FakeActorContext.Unbound, FakeWriteAuthorizer.AllowAll);
        setup.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    // ---------------------------------------------------------------- composition

    /// <summary>Mirror of the write floor an unbound HTTP request gets today
    /// (<c>Api.Persistence.MerchantRequestWriteAuthorizer</c> via <c>Program.cs ResolveMerchantWriteAuthorizer</c>):
    /// NULL/Empty tenant key allowed, the registration-outbox sentinel allowed, any other target requires a
    /// matching bound merchant actor.</summary>
    private sealed class HttpRequestFloor(IActorContext actor) : IWriteAuthorizer
    {
        public bool CanWrite(Type entityType, WriteOperation operation, Guid targetMerchant) =>
            targetMerchant == Guid.Empty
            || targetMerchant == MerchantRegistrationOutboxSentinel.MerchantId
            || (actor.HasActor && targetMerchant == actor.MerchantId);
    }

    /// <summary>The floor a self-service (anonymous registration/correction) request runs under.</summary>
    private static IWriteAuthorizer SelfServiceFloor(IActorContext actor) => new HttpRequestFloor(actor);

    /// <summary>Mirror of the floor an admin-plane approve/reject request runs under since T3
    /// (<c>Api.Persistence.AdminApprovalWriteAuthorizer</c> selected per call by
    /// <c>HttpMerchantWriteAuthorizer</c> when <c>IAdminScope.IsBound</c>): exactly the approve/reject write
    /// set, here for an unrestricted admin — the Scoped-admin accessible-set confinement matrix is
    /// unit-tested against the REAL class in Hosts.Tests.</summary>
    private sealed class AdminApprovalFloor : IWriteAuthorizer
    {
        public bool CanWrite(Type entityType, WriteOperation operation, Guid targetMerchant) =>
            (entityType == typeof(MerchantUserAccount) && operation == WriteOperation.Update)
            || (entityType == typeof(RoleAssignment) && operation == WriteOperation.Insert)
            || (entityType == typeof(RegistrationAudit) && operation == WriteOperation.Insert);
    }

    private static IWriteAuthorizer AdminPlaneFloor(IActorContext actor) => new AdminApprovalFloor();

    private MerchantUserDbContext NewContext(IActorContext actor, IWriteAuthorizer authorizer) =>
        new(new DbContextOptionsBuilder<MerchantUserDbContext>().UseSqlite(_connection).Options,
            actor, authorizer, NoOpSecurityTelemetry.Instance);

    private sealed class FixedClock : IClock
    {
        public DateTime UtcNow { get; } = new(2026, 07, 26, 12, 0, 0, DateTimeKind.Utc);
    }

    private sealed class NullPhotoStore : IPhotoStore
    {
        public Task<string> PutAsync(byte[] bytes, string contentType, CancellationToken cancellationToken) =>
            Task.FromResult("photo-key");
        public Task<(byte[] Bytes, string ContentType)?> GetAsync(string objectKey, CancellationToken cancellationToken) =>
            Task.FromResult<(byte[], string)?>(null);
        public Task<(string Key, bool CreatedNew)> PutStagedAsync(Guid operationId, ReadOnlyMemory<byte> bytes,
            string contentType, CancellationToken cancellationToken) =>
            Task.FromResult(($"{operationId:N}.jpg", true));
        public Task CommitAsync(string objectKey, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DiscardStagedAsync(string objectKey, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DeleteAsync(string objectKey, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>Mirror of <c>Api.Merchants.HostMerchantRoleRepository</c>: the 5 assignment members delegate
    /// to the REAL <see cref="MerchantUserRoleRepository"/>; the 4 iam-catalog members (a different runtime
    /// context) are stubbed the way the host composes them from <c>Persistence.ControlPlane</c>.</summary>
    private sealed class TestHostRoleRepository(MerchantUserDbContext db) : IRoleRepository
    {
        private readonly MerchantUserRoleRepository _partial = new(db);

        public void AddAssignment(RoleAssignment assignment) => _partial.AddAssignment(assignment);
        public void RemoveAssignment(RoleAssignment assignment) => _partial.RemoveAssignment(assignment);
        public Task<IReadOnlySet<Guid>> ListRoleIdsForUserAsync(Guid merchantUserId, CancellationToken ct) =>
            _partial.ListRoleIdsForUserAsync(merchantUserId, ct);
        public Task<RoleAssignment?> GetAssignmentAsync(Guid merchantUserId, Guid roleId, CancellationToken ct) =>
            _partial.GetAssignmentAsync(merchantUserId, roleId, ct);
        public Task<bool> AssignmentExistsAsync(Guid merchantUserId, Guid roleId, CancellationToken ct) =>
            _partial.AssignmentExistsAsync(merchantUserId, roleId, ct);

        public Task<IReadOnlyDictionary<string, Guid>> GetRoleIdsByCodesAsync(
            Guid merchantId, IReadOnlyCollection<string> codes, CancellationToken ct) =>
            GetActiveRoleIdsByCodesAsync(merchantId, codes, ct);

        public Task<IReadOnlyDictionary<string, Guid>> GetActiveRoleIdsByCodesAsync(
            Guid merchantId, IReadOnlyCollection<string> codes, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<string, Guid>>(
                codes.Where(c => c == "merchant_manager").ToDictionary(c => c, _ => ManagerRoleId));

        public Task<IReadOnlySet<string>> ListEffectivePermissionsAsync(
            Guid merchantUserId, Guid merchantId, CancellationToken ct) =>
            Task.FromResult<IReadOnlySet<string>>(new HashSet<string>());

        public Task<IReadOnlyList<string>> ListActiveRoleCodesForUserAsync(
            Guid merchantUserId, Guid merchantId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }

    /// <summary>One "request": a fresh context + the REAL adapters over it, composed exactly the way
    /// <c>MerchantUserPersistenceRegistration.AddMerchantUserPersistence</c> + <c>HostWiring</c> do.</summary>
    private sealed class Scope : IDisposable
    {
        public MerchantUserDbContext Db { get; }
        public IAccountResolver Resolver { get; }
        public IAccountStore Store { get; }
        public IRoleRepository Roles { get; }
        public MerchantUserUnitOfWork UnitOfWork { get; }
        public MerchantRegistrationAuditWriter Audits { get; }

        public Scope(MerchantUserDbContext db)
        {
            Db = db;
            Resolver = new MerchantAccountResolver(db);
            Store = new MerchantAccountStore(db);
            Roles = new TestHostRoleRepository(db);
            UnitOfWork = new MerchantUserUnitOfWork(db, NoOpSecurityTelemetry.Instance);
            Audits = new MerchantRegistrationAuditWriter(db);
        }

        public SubmitRegistrationHandler SubmitHandler() => new(
            Store, new MerchantExternalLoginRepository(Db), Audits, new MerchantRegistrationAttemptWriter(Db),
            new MerchantRegistrationOutboxWriter(Db, new FixedClock()), UnitOfWork,
            new NullPhotoStore(), new FixedClock());

        public ResolveLoginHandler ResolveLoginHandler() => new(Resolver, Roles);
        public GetRegistrationHistoryHandler HistoryHandler() => new(
            Resolver, new MerchantRegistrationHistoryReader(Db), Audits, UnitOfWork, new FixedClock());
        public ResolveByIdHandler ResolveByIdHandler() => new(Resolver, Roles);
        public ApproveHandler ApproveHandler() => new(Store, Roles, Audits, UnitOfWork, new FixedClock());
        public RejectHandler RejectHandler() => new(
            Store, new MerchantUserSessionStore(Db, NoOpSecurityTelemetry.Instance), Audits, UnitOfWork, new FixedClock());

        public void Dispose() => Db.Dispose();
    }

    private Scope SelfServiceScope() =>
        new(NewContext(FakeActorContext.Unbound, SelfServiceFloor(FakeActorContext.Unbound)));

    private Scope AdminPlaneScope() =>
        new(NewContext(FakeActorContext.Unbound, AdminPlaneFloor(FakeActorContext.Unbound)));

    // ---------------------------------------------------------------- helpers

    private static SubmitRegistrationCommand Submission(string subject, TicketPurpose purpose) => new(
        subject, $"{subject}@example.com", HostedDomain: null, purpose,
        new RegistrationForm("First", purpose == TicketPurpose.Registration ? "Version" : "Corrected", IdentityType.Individual),
        PhotoBytes: null, PhotoContentType: null, CorrelationId: $"corr-{subject}-{purpose}");

    private async Task<Guid> SeedViaSubmitAsync(string subject)
    {
        using var scope = SelfServiceScope();
        var result = await scope.SubmitHandler().Handle(Submission(subject, TicketPurpose.Registration), CancellationToken.None);
        return result.UserId;
    }

    /// <summary>Direct state seeding for tests that must not depend on the handlers under test — uses the
    /// domain methods on an AllowAll context (fixture setup, not a production path).</summary>
    private async Task MutateSeededAsync(string subject, Action<MerchantUserAccount> mutate)
    {
        using var db = NewContext(FakeActorContext.Unbound, FakeWriteAuthorizer.AllowAll);
        var account = await db.Users.IgnoreQueryFilters().SingleAsync(u => u.Subject == subject);
        mutate(account);
        await db.SaveChangesAsync();
    }

    private async Task<MerchantUserAccount> LoadAsync(string subject)
    {
        using var db = NewContext(FakeActorContext.Unbound, FakeWriteAuthorizer.AllowAll);
        return await db.Users.IgnoreQueryFilters().AsNoTracking().SingleAsync(u => u.Subject == subject);
    }

    // ---------------------------------------------------------------- F1: pre-bind resolve sees the truth

    [Fact]
    public async Task Resolve_login_sees_a_pending_registration_before_any_actor_is_bound()
    {
        await SeedViaSubmitAsync("lc-pending");

        using var scope = SelfServiceScope();
        var result = await scope.ResolveLoginHandler().Handle(new ResolveLoginQuery("google", "lc-pending"), CancellationToken.None);

        Assert.Equal(LoginOutcome.PendingApproval, result.Outcome);
    }

    [Fact]
    public async Task Resolve_login_reports_rejected_so_the_host_can_mint_a_correction_ticket()
    {
        await SeedViaSubmitAsync("lc-rejected");
        await MutateSeededAsync("lc-rejected", a => a.Reject(DateTime.UtcNow));

        using var scope = SelfServiceScope();
        var result = await scope.ResolveLoginHandler().Handle(new ResolveLoginQuery("google", "lc-rejected"), CancellationToken.None);

        Assert.Equal(LoginOutcome.Rejected, result.Outcome);
    }

    // ---------------------------------------------------------------- F2: correction resubmits the SAME row

    [Fact]
    public async Task A_correction_resubmission_flips_the_same_rejected_row_back_to_pending()
    {
        var id = await SeedViaSubmitAsync("lc-correct");
        await MutateSeededAsync("lc-correct", a => a.Reject(DateTime.UtcNow));

        using var scope = SelfServiceScope();
        var result = await scope.SubmitHandler().Handle(Submission("lc-correct", TicketPurpose.Correction), CancellationToken.None);

        Assert.Equal(id, result.UserId); // same row, never a second account
        var row = await LoadAsync("lc-correct");
        Assert.Equal(UserStatus.PendingApproval, row.Status);
        Assert.Equal("Corrected", row.LastName); // the corrected form values landed

        using var db = NewContext(FakeActorContext.Unbound, FakeWriteAuthorizer.AllowAll);
        Assert.Equal(1, await db.Users.IgnoreQueryFilters().CountAsync(u => u.Subject == "lc-correct"));
        Assert.True(await db.RegistrationAudits.AnyAsync(
            a => a.TargetSubject == "lc-correct" && a.Action == RegistrationAuditAction.Resubmitted));
    }

    // ---------------------------------------------------------------- F4: admin reject finds its target

    [Fact]
    public async Task An_admin_reject_finds_the_pending_target_and_records_the_reason()
    {
        var targetId = await SeedViaSubmitAsync("lc-adm-reject");

        using var scope = AdminPlaneScope();
        var result = await scope.RejectHandler().Handle(
            new RejectCommand(targetId, "incomplete documents", "admin-sub", "corr-reject"), CancellationToken.None);

        Assert.Equal(UserStatus.Rejected, result.Status);
        var row = await LoadAsync("lc-adm-reject");
        Assert.Equal(UserStatus.Rejected, row.Status);

        using var db = NewContext(FakeActorContext.Unbound, FakeWriteAuthorizer.AllowAll);
        Assert.True(await db.RegistrationAudits.AnyAsync(
            a => a.TargetSubject == "lc-adm-reject"
                 && a.Action == RegistrationAuditAction.Rejected && a.Reason == "incomplete documents"));
    }

    // ---------------------------------------------------------------- F3: admin approve completes under the floor

    [Fact]
    public async Task An_admin_approve_activates_binds_the_merchant_and_assigns_the_role()
    {
        var targetId = await SeedViaSubmitAsync("lc-adm-approve");

        using var scope = AdminPlaneScope();
        var result = await scope.ApproveHandler().Handle(
            new ApproveCommand(targetId, MerchantA, ["merchant_manager"], "admin-sub", ActingAdminId, "corr-approve"),
            CancellationToken.None);

        Assert.Equal(UserStatus.Active, result.Status);
        var row = await LoadAsync("lc-adm-approve");
        Assert.Equal(UserStatus.Active, row.Status);
        Assert.Equal(MerchantA, row.MerchantId);

        using var db = NewContext(FakeActorContext.Unbound, FakeWriteAuthorizer.AllowAll);
        Assert.Equal(1, await db.RoleAssignments.IgnoreQueryFilters()
            .CountAsync(a => a.UserId == row.Id && a.RoleId == ManagerRoleId));
    }

    // ---------------------------------------------------------------- F6: session re-resolution by id

    [Fact]
    public async Task Session_re_resolution_by_id_finds_the_callers_active_account_before_binding()
    {
        var id = await SeedViaSubmitAsync("lc-by-id");
        await MutateSeededAsync("lc-by-id", a => a.Approve(MerchantA, DateTime.UtcNow));

        using var scope = SelfServiceScope(); // the auth handler runs BEFORE any claim/actor exists
        var result = await scope.ResolveByIdHandler().Handle(new ResolveByIdQuery(id), CancellationToken.None);

        Assert.Equal(ByIdOutcome.Resolved, result.Outcome);
        Assert.Equal(MerchantA, result.Resolution!.MerchantId);
    }

    // ---------------------------------------------------------------- the full loop (bugfix.md repro)

    [Fact]
    public async Task Full_lifecycle_register_reject_resubmit_approve_ends_active()
    {
        Guid fullId;
        using (var s = SelfServiceScope())
            fullId = (await s.SubmitHandler().Handle(
                Submission("lc-full", TicketPurpose.Registration), CancellationToken.None)).UserId;

        using (var s = SelfServiceScope())
            Assert.Equal(LoginOutcome.PendingApproval,
                (await s.ResolveLoginHandler().Handle(new ResolveLoginQuery("google", "lc-full"), CancellationToken.None)).Outcome);

        using (var s = AdminPlaneScope())
            await s.RejectHandler().Handle(
                new RejectCommand(fullId, "photo unreadable", "admin-sub", "corr-1"), CancellationToken.None);

        using (var s = SelfServiceScope())
            Assert.Equal(LoginOutcome.Rejected,
                (await s.ResolveLoginHandler().Handle(new ResolveLoginQuery("google", "lc-full"), CancellationToken.None)).Outcome);

        using (var s = SelfServiceScope())
            await s.SubmitHandler().Handle(Submission("lc-full", TicketPurpose.Correction), CancellationToken.None);

        using (var s = SelfServiceScope())
            Assert.Equal(LoginOutcome.PendingApproval,
                (await s.ResolveLoginHandler().Handle(new ResolveLoginQuery("google", "lc-full"), CancellationToken.None)).Outcome);

        using (var s = AdminPlaneScope())
            await s.ApproveHandler().Handle(
                new ApproveCommand(fullId, MerchantB, ["merchant_manager"], "admin-sub", ActingAdminId, "corr-2"),
                CancellationToken.None);

        using (var s = SelfServiceScope())
        {
            var final = await s.ResolveLoginHandler().Handle(new ResolveLoginQuery("google", "lc-full"), CancellationToken.None);
            Assert.Equal(LoginOutcome.Active, final.Outcome);
            Assert.Equal(MerchantB, final.Resolution!.MerchantId);
        }
    }

    // registration-attempt-history REQ-1.3/1.4/2.1-adjacent/2.3: the same lifecycle, proven through the REAL
    // EF adapters — every submit freezes one attempt row under the SAME UserId, and the history
    // handler returns them in order with the full lifecycle timeline (reject reason included).
    [Fact]
    public async Task Lifecycle_captures_one_attempt_per_submit_bound_to_the_same_user_and_serves_the_timeline()
    {
        Guid userId;
        using (var s = SelfServiceScope())
            userId = (await s.SubmitHandler().Handle(
                Submission("lc-history", TicketPurpose.Registration), CancellationToken.None)).UserId;

        using (var s = AdminPlaneScope())
            await s.RejectHandler().Handle(
                new RejectCommand(userId, "photo unreadable", "admin-sub", "corr-1"), CancellationToken.None);

        using (var s = SelfServiceScope())
            await s.SubmitHandler().Handle(Submission("lc-history", TicketPurpose.Correction), CancellationToken.None);

        using (var s = AdminPlaneScope())
        {
            var history = await s.HistoryHandler().Handle(
                new GetRegistrationHistoryQuery(userId, Reveal: false, "admin-sub", ActingAdminId, "corr-h",
                    IsUnrestrictedAdmin: true, AccessibleMerchantIds: new HashSet<Guid>()),
                CancellationToken.None);

            Assert.NotNull(history);
            Assert.Equal(2, history!.Attempts.Count);                       // one snapshot per submit
            Assert.Equal([1, 2], history.Attempts.Select(a => a.AttemptNo)); // sequential, ordered
            Assert.Equal(TicketPurpose.Registration, history.Attempts[0].Purpose);
            Assert.Equal(TicketPurpose.Correction, history.Attempts[1].Purpose);
            Assert.Equal("Version", history.Attempts[0].LastName);           // attempt 1 froze the ORIGINAL form
            Assert.Equal("Corrected", history.Attempts[1].LastName);         // attempt 2 froze the resubmitted form

            // Both rows hang off the SAME user id — the whole history is the one user's record.
            var attempts = await s.Db.RegistrationAttempts.AsNoTracking().ToListAsync();
            Assert.All(attempts, a => Assert.Equal(userId, a.UserId));

            // Timeline from RegistrationAudits: registered + rejected(reason) + resubmitted. Order-insensitive
            // here — the shared FixedClock stamps every row with the SAME OccurredAt, so ORDER BY OccurredAt
            // has no tie-breaker to observe (real requests get distinct clock reads).
            Assert.Equal(3, history.Timeline.Count);
            Assert.Equal(
                new[] { RegistrationAuditAction.Registered, RegistrationAuditAction.Rejected, RegistrationAuditAction.Resubmitted }.ToHashSet(),
                history.Timeline.Select(t => t.Action).ToHashSet());
            Assert.Equal("photo unreadable",
                Assert.Single(history.Timeline, t => t.Action == RegistrationAuditAction.Rejected).Reason);
        }
    }

    // ---------------------------------------------------------------- Codex P1: stale pre-bind transitions must not interleave

    [Fact]
    public async Task A_stale_reject_loses_to_a_committed_approve_and_rolls_back_whole()
    {
        // Two admin requests load the SAME pending subject concurrently. The reject's snapshot is stale by
        // the time it saves (the approve committed first) — the Status/MerchantId concurrency tokens must
        // fail its WHOLE transaction, never let it stamp Rejected over an Active, merchant-bound account.
        var raceRejectId = await SeedViaSubmitAsync("lc-race-reject");

        using var staleScope = AdminPlaneScope();
        var stale = await staleScope.Store.FindBySubjectAsync("google", "lc-race-reject", CancellationToken.None);

        using (var winner = AdminPlaneScope())
            await winner.ApproveHandler().Handle(
                new ApproveCommand(raceRejectId, MerchantA, ["merchant_manager"], "admin-1", ActingAdminId, "corr-w"),
                CancellationToken.None);

        stale!.Reject(DateTime.UtcNow);
        staleScope.Audits.Append(RegistrationAudit.For(
            RegistrationAuditAction.Rejected, stale.Id, "lc-race-reject", "corr-l", DateTime.UtcNow,
            actorAdminId: ActingAdminId, actorSubject: "admin-2"));
        await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => staleScope.UnitOfWork.SaveChangesAsync(CancellationToken.None));

        var row = await LoadAsync("lc-race-reject");
        Assert.Equal(UserStatus.Active, row.Status);
        Assert.Equal(MerchantA, row.MerchantId);
        using var db = NewContext(FakeActorContext.Unbound, FakeWriteAuthorizer.AllowAll);
        Assert.False(await db.RegistrationAudits.AnyAsync(
            a => a.TargetSubject == "lc-race-reject" && a.Action == RegistrationAuditAction.Rejected));
    }

    [Fact]
    public async Task A_stale_second_approve_cannot_double_bind_merchants()
    {
        var raceApproveId = await SeedViaSubmitAsync("lc-race-approve");

        using var staleScope = AdminPlaneScope();
        var stale = await staleScope.Store.FindBySubjectAsync("google", "lc-race-approve", CancellationToken.None);

        using (var winner = AdminPlaneScope())
            await winner.ApproveHandler().Handle(
                new ApproveCommand(raceApproveId, MerchantA, ["merchant_manager"], "admin-1", ActingAdminId, "corr-w"),
                CancellationToken.None);

        // A DIFFERENT role id than the winner's: the (UserId, RoleId) unique index therefore cannot
        // catch this race — only the Status/MerchantId concurrency tokens can (same-role races already die on
        // the unique index as a 409).
        var otherRoleId = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");
        stale!.Approve(MerchantB, DateTime.UtcNow);
        staleScope.Roles.AddAssignment(RoleAssignment.Create(stale.Id, otherRoleId, MerchantB, ActingAdminId, DateTime.UtcNow));
        await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => staleScope.UnitOfWork.SaveChangesAsync(CancellationToken.None));

        var row = await LoadAsync("lc-race-approve");
        Assert.Equal(MerchantA, row.MerchantId); // the committed merchant, never overwritten to B
        using var db = NewContext(FakeActorContext.Unbound, FakeWriteAuthorizer.AllowAll);
        Assert.Equal(1, await db.RoleAssignments.IgnoreQueryFilters().CountAsync(a => a.UserId == row.Id));
    }

    // ---------------------------------------------------------------- pin: WHY the seam split exists (green today)

    [Fact]
    public async Task The_filtered_repository_hides_null_merchant_rows_from_every_actor_by_design()
    {
        var pendingId = await SeedViaSubmitAsync("lc-pin-pending");

        // Unbound (CurrentMerchant == Guid.Empty) and bound both miss a NULL-MerchantId row: this is the
        // deny-default read floor working as specified — which is exactly why pre-bind flows need their own
        // filter-free seam instead of IUserRepository. (Lookup is by id — the subject-only repository lookup
        // was retired with the (Provider, Subject) discriminator; the filter, not the key, is the pin here.)
        using (var unbound = NewContext(FakeActorContext.Unbound, FakeWriteAuthorizer.AllowAll))
            Assert.Null(await new MerchantUserRepository(unbound).FindByIdAsync(pendingId, CancellationToken.None));
        using (var bound = NewContext(FakeActorContext.For(MerchantA), FakeWriteAuthorizer.AllowAll))
            Assert.Null(await new MerchantUserRepository(bound).FindByIdAsync(pendingId, CancellationToken.None));

        // B2: a bound in-session actor sees its OWN merchant's rows and nobody else's.
        var activeId = await SeedViaSubmitAsync("lc-pin-active");
        await MutateSeededAsync("lc-pin-active", a => a.Approve(MerchantA, DateTime.UtcNow));

        using (var own = NewContext(FakeActorContext.For(MerchantA), FakeWriteAuthorizer.AllowAll))
            Assert.NotNull(await new MerchantUserRepository(own).FindByIdAsync(activeId, CancellationToken.None));
        using (var other = NewContext(FakeActorContext.For(MerchantB), FakeWriteAuthorizer.AllowAll))
            Assert.Null(await new MerchantUserRepository(other).FindByIdAsync(activeId, CancellationToken.None));
    }
}
