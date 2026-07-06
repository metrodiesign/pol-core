using Admin.Application.ReactivateAdmin;
using Admin.Domain;
using BuildingBlocks.Application;

namespace Admin.Tests;

/// <summary>Write-side handler for admin-account-management REQ-3 (reactivate). Proves the idempotent domain
/// transition, the 404 on unknown target, that sessions are revoked ONLY on the Suspended->Active transition
/// (fresh-login guarantee), and that every accepted call audits.</summary>
public sealed class AdminAccountCommandTests
{
    private static readonly DateTime T0 = new(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Actor = Guid.NewGuid();

    // ===== domain =====
    [Fact]
    public void Reactivate_sets_status_active()
    {
        var a = AdminAccount.CreateScoped("x@x", T0);
        a.Suspend(Actor);
        Assert.Equal(AdminStatus.Suspended, a.Status);
        a.Reactivate();
        Assert.Equal(AdminStatus.Active, a.Status);
    }

    [Fact]
    public void Reactivate_on_active_account_is_idempotent()
    {
        var a = AdminAccount.CreateScoped("x@x", T0);   // Active from creation
        a.Reactivate();
        Assert.Equal(AdminStatus.Active, a.Status);
    }

    // ===== handler =====
    private static (ReactivateAdminHandler H, FakeAdminAccountRepository Accounts,
        FakeAdminSessionStore Sessions, FakeAdminAccountAuditWriter Audit) NewHandler()
    {
        var accounts = new FakeAdminAccountRepository();
        var sessions = new FakeAdminSessionStore();
        var audit = new FakeAdminAccountAuditWriter();
        var h = new ReactivateAdminHandler(accounts, sessions, audit, new FakeUnitOfWork(), new FixedClock());
        return (h, accounts, sessions, audit);
    }

    [Fact]
    public async Task Reactivate_unknown_id_throws_NotFound()
    {
        var (h, _, _, _) = NewHandler();
        await Assert.ThrowsAsync<NotFoundException>(() =>
            h.Handle(new ReactivateAdminCommand(Guid.NewGuid(), Actor, "corr"), default).AsTask());
    }

    [Fact]
    public async Task Reactivate_suspended_activates_revokes_sessions_and_audits()
    {
        var (h, accounts, sessions, audit) = NewHandler();
        var target = AdminAccount.CreateScoped("t@x", T0);
        target.Suspend(Actor);
        accounts.Add(target);

        var result = await h.Handle(new ReactivateAdminCommand(target.Id, Actor, "corr"), default);

        Assert.Equal(nameof(AdminStatus.Active), result.Status);
        Assert.Equal(AdminStatus.Active, target.Status);
        Assert.Equal(new[] { target.Id }, sessions.RevokedAdmins);          // fresh-login guarantee (REQ-3.5)
        Assert.Single(audit.Appended);                                      // every accepted call (REQ-3.2)
        Assert.Equal(AdminAuditAction.Reactivate, audit.Appended[0].Action);
        Assert.Equal(target.Id, audit.Appended[0].TargetAdminId);
    }

    [Fact]
    public async Task Reactivate_already_active_does_not_revoke_but_still_audits()
    {
        var (h, accounts, sessions, audit) = NewHandler();
        var target = AdminAccount.CreateScoped("t@x", T0);   // already Active
        accounts.Add(target);

        await h.Handle(new ReactivateAdminCommand(target.Id, Actor, "corr"), default);

        Assert.Empty(sessions.RevokedAdmins);               // idempotent: no revoke (REQ-3.6)
        Assert.Single(audit.Appended);                      // but still audits (REQ-3.3)
        Assert.Equal(AdminAuditAction.Reactivate, audit.Appended[0].Action);
    }
}
