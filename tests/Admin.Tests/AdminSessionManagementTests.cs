using Admin.Application.AdminAccountQueries;
using Admin.Application.RevokeAdminSession;
using Admin.Domain;
using BuildingBlocks.Application;

namespace Admin.Tests;

/// <summary>Session-management handlers for admin-account-management REQ-4 (list) and REQ-5 (revoke). Proves the
/// 404 existence checks, the read-time isLive projection with no token material, the ownership guard (foreign /
/// unknown session -> 404, no existence leak), the whole-family revoke, and that the revoked FamilyId is surfaced
/// for the host to security-log. (The host emits the log line — the Application layer stays logging-free.)</summary>
public sealed class AdminSessionManagementTests
{
    private static readonly Guid Actor = Guid.NewGuid();
    private static readonly AdminSessionPolicy Policy =
        new(TimeSpan.FromHours(1), TimeSpan.FromHours(8), TimeSpan.FromMinutes(15), TimeSpan.FromSeconds(30));

    private sealed class TestClock(DateTime now) : IClock { public DateTime UtcNow { get; } = now; }

    private static AdminSession Session(Guid adminId, DateTime issuedAt) =>
        AdminSession.Start(adminId, new byte[32], issuedAt, Policy, "1.2.3.4", "agent");

    // ===== REQ-4: list sessions =====
    [Fact]
    public async Task ListSessions_unknown_admin_returns_null()
    {
        var handler = new ListAdminSessionsHandler(new FakeAdminAccountRepository(), new FakeAdminSessionStore(),
            new TestClock(new DateTime(2026, 7, 6, 0, 0, 0, DateTimeKind.Utc)));
        Assert.Null(await handler.Handle(new ListAdminSessionsQuery(Guid.NewGuid()), default));
    }

    [Fact]
    public async Task ListSessions_projects_isLive_newest_first_without_token()
    {
        var now = new DateTime(2026, 7, 6, 0, 0, 0, DateTimeKind.Utc);
        var accounts = new FakeAdminAccountRepository();
        var sessions = new FakeAdminSessionStore();
        var admin = AdminAccount.SelfProvision("sub", "a@x", now);
        accounts.Add(admin);
        sessions.Add(Session(admin.Id, now));                 // live (issued now)
        sessions.Add(Session(admin.Id, now.AddHours(-10)));   // absolute-expired (now-10h + 8h < now) -> not live

        var views = await new ListAdminSessionsHandler(accounts, sessions, new TestClock(now))
            .Handle(new ListAdminSessionsQuery(admin.Id), default);

        Assert.NotNull(views);
        Assert.Equal(2, views!.Count);
        Assert.True(views[0].IsLive);                          // newest first, still within windows
        Assert.False(views[1].IsLive);                         // older one is past absolute expiry
        Assert.All(views, v => Assert.Equal(AdminSessionStatus.Active, v.Status));
        // AdminSessionView has no token field at all — the "no hash on the wire" guarantee is structural.
    }

    [Fact]
    public async Task ListSessions_real_admin_with_no_sessions_is_empty_not_null()
    {
        var accounts = new FakeAdminAccountRepository();
        var admin = AdminAccount.CreateScoped("a@x", new DateTime(2026, 7, 6, 0, 0, 0, DateTimeKind.Utc));
        accounts.Add(admin);
        var views = await new ListAdminSessionsHandler(accounts, new FakeAdminSessionStore(),
            new TestClock(new DateTime(2026, 7, 6, 0, 0, 0, DateTimeKind.Utc)))
            .Handle(new ListAdminSessionsQuery(admin.Id), default);
        Assert.NotNull(views);
        Assert.Empty(views!);
    }

    // ===== REQ-5: revoke session =====
    private static (RevokeAdminSessionHandler H, FakeAdminAccountRepository Accounts, FakeAdminSessionStore Sessions,
        FakeAdminAccountAuditWriter Audit) NewHandler()
    {
        var accounts = new FakeAdminAccountRepository();
        var sessions = new FakeAdminSessionStore();
        var audit = new FakeAdminAccountAuditWriter();
        var h = new RevokeAdminSessionHandler(accounts, sessions, audit, new FakeUnitOfWork(), new FixedClock());
        return (h, accounts, sessions, audit);
    }

    [Fact]
    public async Task Revoke_unknown_admin_throws_NotFound()
    {
        var (h, _, _, _) = NewHandler();
        await Assert.ThrowsAsync<NotFoundException>(() =>
            h.Handle(new RevokeAdminSessionCommand(Guid.NewGuid(), Guid.NewGuid(), Actor, "corr"), default).AsTask());
    }

    [Fact]
    public async Task Revoke_unknown_session_throws_NotFound()
    {
        var (h, accounts, _, _) = NewHandler();
        var admin = AdminAccount.CreateScoped("a@x", new DateTime(2026, 7, 6, 0, 0, 0, DateTimeKind.Utc));
        accounts.Add(admin);
        await Assert.ThrowsAsync<NotFoundException>(() =>
            h.Handle(new RevokeAdminSessionCommand(admin.Id, Guid.NewGuid(), Actor, "corr"), default).AsTask());
    }

    [Fact]
    public async Task Revoke_session_owned_by_another_admin_throws_NotFound()
    {
        var (h, accounts, sessions, _) = NewHandler();
        var routeAdmin = AdminAccount.CreateScoped("route@x", new DateTime(2026, 7, 6, 0, 0, 0, DateTimeKind.Utc));
        var otherAdmin = AdminAccount.CreateScoped("other@x", new DateTime(2026, 7, 6, 0, 0, 0, DateTimeKind.Utc));
        accounts.Add(routeAdmin);
        accounts.Add(otherAdmin);
        var foreign = Session(otherAdmin.Id, new DateTime(2026, 7, 6, 0, 0, 0, DateTimeKind.Utc));
        sessions.Add(foreign);

        // route admin exists, but the session belongs to otherAdmin -> 404, no existence leak.
        await Assert.ThrowsAsync<NotFoundException>(() =>
            h.Handle(new RevokeAdminSessionCommand(routeAdmin.Id, foreign.Id, Actor, "corr"), default).AsTask());
        Assert.Empty(sessions.RevokedFamilies);
    }

    [Fact]
    public async Task Revoke_revokes_whole_family_audits_and_surfaces_familyId()
    {
        var (h, accounts, sessions, audit) = NewHandler();
        var admin = AdminAccount.CreateScoped("a@x", new DateTime(2026, 7, 6, 0, 0, 0, DateTimeKind.Utc));
        accounts.Add(admin);
        var session = Session(admin.Id, new DateTime(2026, 7, 6, 0, 0, 0, DateTimeKind.Utc));
        sessions.Add(session);

        var result = await h.Handle(new RevokeAdminSessionCommand(admin.Id, session.Id, Actor, "corr-42"), default);

        Assert.Equal(new[] { session.FamilyId }, sessions.RevokedFamilies);   // whole family (REQ-5.1)
        Assert.Single(audit.Appended);
        Assert.Equal(AdminAuditAction.SessionRevoke, audit.Appended[0].Action);
        Assert.Equal(admin.Id, audit.Appended[0].TargetAdminId);
        // The result surfaces the data the host security-logs (sessionId/familyId/targetAdminId) — REQ-5.2.
        Assert.Equal(session.Id, result.SessionId);
        Assert.Equal(session.FamilyId, result.FamilyId);
        Assert.Equal(admin.Id, result.AdminId);
    }

    [Fact]
    public async Task Revoke_is_idempotent_across_repeated_calls()
    {
        var (h, accounts, sessions, _) = NewHandler();
        var admin = AdminAccount.CreateScoped("a@x", new DateTime(2026, 7, 6, 0, 0, 0, DateTimeKind.Utc));
        accounts.Add(admin);
        var session = Session(admin.Id, new DateTime(2026, 7, 6, 0, 0, 0, DateTimeKind.Utc));
        sessions.Add(session);

        await h.Handle(new RevokeAdminSessionCommand(admin.Id, session.Id, Actor, "c"), default);
        await h.Handle(new RevokeAdminSessionCommand(admin.Id, session.Id, Actor, "c"), default);   // no throw

        Assert.Equal(2, sessions.RevokedFamilies.Count);   // family-revoke is a no-op-safe repeat (REQ-5.5)
    }
}
