using Admins.Application;
using Admins.Application.Roles;
using Admins.Application.Users;
using Admins.Domain.Roles;
using Admins.Domain.Users;
using Admins.Infrastructure.Persistence;
using Admins.Infrastructure.Persistence.Roles;
using Admins.Infrastructure.Persistence.Users;
using BuildingBlocks.Application;

namespace Admins.Tests;

/// <summary>Session-management handlers for admin-account-management REQ-4 (list) and REQ-5 (revoke). Proves the
/// 404 existence checks, the read-time isLive projection with no token material, the ownership guard (foreign /
/// unknown session -> 404, no existence leak), the whole-family revoke, and that the revoked FamilyId is surfaced
/// for the host to security-log. (The host emits the log line — the Application layer stays logging-free.)</summary>
public sealed class PlatformUserSessionManagementTests
{
    private static readonly Guid Actor = Guid.NewGuid();
    private static readonly SessionPolicy Policy =
        new(TimeSpan.FromHours(1), TimeSpan.FromHours(8), TimeSpan.FromMinutes(15), TimeSpan.FromSeconds(30));

    private sealed class TestClock(DateTime now) : IClock { public DateTime UtcNow { get; } = now; }

    private static Session MakeSession(Guid adminId, DateTime issuedAt) =>
        Session.Start(adminId, new byte[32], issuedAt, Policy, "1.2.3.4", "agent");

    // ===== REQ-4: list sessions =====
    [Fact]
    public async Task ListSessions_unknown_admin_returns_null()
    {
        var handler = new ListSessionsHandler(new FakePlatformUserRepository(), new FakePlatformUserSessionStore(),
            new TestClock(new DateTime(2026, 7, 6, 0, 0, 0, DateTimeKind.Utc)));
        Assert.Null(await handler.Handle(new ListSessionsQuery(Guid.NewGuid()), default));
    }

    [Fact]
    public async Task ListSessions_projects_isLive_newest_first_without_token()
    {
        var now = new DateTime(2026, 7, 6, 0, 0, 0, DateTimeKind.Utc);
        var accounts = new FakePlatformUserRepository();
        var sessions = new FakePlatformUserSessionStore();
        var admin = User.SelfProvision("google", "sub", "a@x", now);
        accounts.Add(admin);
        sessions.Add(MakeSession(admin.Id, now));                 // live (issued now)
        sessions.Add(MakeSession(admin.Id, now.AddHours(-10)));   // absolute-expired (now-10h + 8h < now) -> not live

        var views = await new ListSessionsHandler(accounts, sessions, new TestClock(now))
            .Handle(new ListSessionsQuery(admin.Id), default);

        Assert.NotNull(views);
        Assert.Equal(2, views!.Count);
        Assert.True(views[0].IsLive);                          // newest first, still within windows
        Assert.False(views[1].IsLive);                         // older one is past absolute expiry
        Assert.All(views, v => Assert.Equal(SessionStatus.Active, v.Status));
        // SessionView has no token field at all — the "no hash on the wire" guarantee is structural.
    }

    [Fact]
    public async Task ListSessions_real_admin_with_no_sessions_is_empty_not_null()
    {
        var accounts = new FakePlatformUserRepository();
        var admin = User.CreateScoped("a@x", new DateTime(2026, 7, 6, 0, 0, 0, DateTimeKind.Utc));
        accounts.Add(admin);
        var views = await new ListSessionsHandler(accounts, new FakePlatformUserSessionStore(),
            new TestClock(new DateTime(2026, 7, 6, 0, 0, 0, DateTimeKind.Utc)))
            .Handle(new ListSessionsQuery(admin.Id), default);
        Assert.NotNull(views);
        Assert.Empty(views!);
    }

    // ===== REQ-5: revoke session =====
    private static (RevokeSessionHandler H, FakePlatformUserRepository Accounts, FakePlatformUserSessionStore Sessions,
        FakePlatformUserAuditWriter Audit, FakeAdminOperationStore Operations) NewHandler()
    {
        var accounts = new FakePlatformUserRepository();
        var sessions = new FakePlatformUserSessionStore();
        var audit = new FakePlatformUserAuditWriter();
        var operations = new FakeAdminOperationStore();
        var h = new RevokeSessionHandler(
            accounts, sessions, audit, operations, new FakeUnitOfWork(), new FixedClock());
        return (h, accounts, sessions, audit, operations);
    }

    [Fact]
    public async Task Revoke_unknown_admin_throws_NotFound()
    {
        var (h, _, _, _, _) = NewHandler();
        await Assert.ThrowsAsync<NotFoundException>(() =>
            h.Handle(new RevokeSessionCommand(
                Guid.NewGuid(), Guid.NewGuid(), Actor, "corr", "key-1"), default).AsTask());
    }

    [Fact]
    public async Task Revoke_unknown_session_throws_NotFound()
    {
        var (h, accounts, _, _, _) = NewHandler();
        var admin = User.CreateScoped("a@x", new DateTime(2026, 7, 6, 0, 0, 0, DateTimeKind.Utc));
        accounts.Add(admin);
        await Assert.ThrowsAsync<NotFoundException>(() =>
            h.Handle(new RevokeSessionCommand(
                admin.Id, Guid.NewGuid(), Actor, "corr", "key-1"), default).AsTask());
    }

    [Fact]
    public async Task Revoke_session_owned_by_another_admin_throws_NotFound()
    {
        var (h, accounts, sessions, _, _) = NewHandler();
        var routeAdmin = User.CreateScoped("route@x", new DateTime(2026, 7, 6, 0, 0, 0, DateTimeKind.Utc));
        var otherAdmin = User.CreateScoped("other@x", new DateTime(2026, 7, 6, 0, 0, 0, DateTimeKind.Utc));
        accounts.Add(routeAdmin);
        accounts.Add(otherAdmin);
        var foreign = MakeSession(otherAdmin.Id, new DateTime(2026, 7, 6, 0, 0, 0, DateTimeKind.Utc));
        sessions.Add(foreign);

        // route admin exists, but the session belongs to otherAdmin -> 404, no existence leak.
        await Assert.ThrowsAsync<NotFoundException>(() =>
            h.Handle(new RevokeSessionCommand(
                routeAdmin.Id, foreign.Id, Actor, "corr", "key-1"), default).AsTask());
        Assert.Empty(sessions.RevokedFamilies);
    }

    [Fact]
    public async Task Revoke_revokes_whole_family_audits_and_surfaces_familyId()
    {
        var (h, accounts, sessions, audit, operations) = NewHandler();
        var admin = User.CreateScoped("a@x", new DateTime(2026, 7, 6, 0, 0, 0, DateTimeKind.Utc));
        accounts.Add(admin);
        var session = MakeSession(admin.Id, new DateTime(2026, 7, 6, 0, 0, 0, DateTimeKind.Utc));
        sessions.Add(session);

        var result = await h.Handle(new RevokeSessionCommand(
            admin.Id, session.Id, Actor, "corr-42", "key-1"), default);

        Assert.Equal(new[] { session.FamilyId }, sessions.RevokedFamilies);   // whole family (REQ-5.1)
        Assert.Single(audit.Appended);
        Assert.Equal(AuditAction.SessionRevoke, audit.Appended[0].Action);
        Assert.Equal(admin.Id, audit.Appended[0].TargetAdminId);
        // The result surfaces the data the host security-logs (sessionId/familyId/targetAdminId) — REQ-5.2.
        Assert.Equal(session.Id, result.SessionId);
        Assert.Equal(session.FamilyId, result.FamilyId);
        Assert.Equal(admin.Id, result.AdminId);
        Assert.Equal(1, operations.Count);
        Assert.Equal(204, operations.LastResponseStatus);
        Assert.Equal(new FixedClock().UtcNow.AddHours(24), operations.LastExpiresAt);
    }

    [Fact]
    public async Task Revoke_is_idempotent_across_repeated_calls()
    {
        var (h, accounts, sessions, audit, _) = NewHandler();
        var admin = User.CreateScoped("a@x", new DateTime(2026, 7, 6, 0, 0, 0, DateTimeKind.Utc));
        accounts.Add(admin);
        var session = MakeSession(admin.Id, new DateTime(2026, 7, 6, 0, 0, 0, DateTimeKind.Utc));
        sessions.Add(session);

        await h.Handle(new RevokeSessionCommand(admin.Id, session.Id, Actor, "c", "same-key"), default);
        await h.Handle(new RevokeSessionCommand(admin.Id, session.Id, Actor, "c", "same-key"), default);

        Assert.Single(sessions.RevokedFamilies);
        Assert.Single(audit.Appended);
    }

    [Fact]
    public async Task Revoke_rejects_reusing_a_key_for_a_different_session()
    {
        var (h, accounts, sessions, _, _) = NewHandler();
        var admin = User.CreateScoped("a@x", new DateTime(2026, 7, 6, 0, 0, 0, DateTimeKind.Utc));
        accounts.Add(admin);
        var first = MakeSession(admin.Id, new DateTime(2026, 7, 6, 0, 0, 0, DateTimeKind.Utc));
        var second = MakeSession(admin.Id, new DateTime(2026, 7, 6, 1, 0, 0, DateTimeKind.Utc));
        sessions.Add(first);
        sessions.Add(second);

        await h.Handle(new RevokeSessionCommand(admin.Id, first.Id, Actor, "c", "same-key"), default);
        var error = await Assert.ThrowsAsync<ConflictException>(() => h.Handle(
            new RevokeSessionCommand(admin.Id, second.Id, Actor, "c", "same-key"), default).AsTask());

        Assert.Equal("idempotency_key_reused", error.Code);
        Assert.Equal(new[] { first.FamilyId }, sessions.RevokedFamilies);
    }
}
