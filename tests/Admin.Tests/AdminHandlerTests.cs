using Admin.Application.AssignTenant;
using Admin.Application.BindInvitedAdmin;
using Admin.Application.CreateScopedAdmin;
using Admin.Application.ResolveAdmin;
using Admin.Application.SelfProvisionSuperAdmin;
using Admin.Application.SuspendAdmin;
using Admin.Application.UnassignTenant;
using Admin.Domain;
using BuildingBlocks.Application;

namespace Admin.Tests;

public sealed class AdminHandlerTests
{
    private static readonly DateTime Now = new(2026, 6, 23, 0, 0, 0, DateTimeKind.Utc);

    // ---- ResolveAdmin ----

    [Fact]
    public async Task Resolve_returns_unrestricted_for_an_active_super()
    {
        var admins = new FakeAdminAccountRepository();
        admins.Add(AdminAccount.SelfProvision("super-1", "ops@org.com", Now));
        var handler = new ResolveAdminHandler(admins);

        var result = await handler.Handle(new ResolveAdminQuery("super-1"), default);

        Assert.Equal(AdminResolveOutcome.Resolved, result.Outcome);
        Assert.True(result.Resolution!.Accessible.IsUnrestricted);
        Assert.Equal(AdminTier.Super, result.Resolution.Tier);
    }

    [Fact]
    public async Task Resolve_returns_the_assigned_set_for_an_active_scoped_admin()
    {
        var admins = new FakeAdminAccountRepository();
        var scoped = AdminAccount.CreateScoped("scoped@org.com", Now);
        scoped.BindSubject("scoped-1");
        admins.Add(scoped);
        var tenantX = Guid.NewGuid();
        admins.AddAssignment(AdminTenantAssignment.Create(scoped.Id, tenantX, Guid.NewGuid(), Now));
        var handler = new ResolveAdminHandler(admins);

        var result = await handler.Handle(new ResolveAdminQuery("scoped-1"), default);

        Assert.Equal(AdminResolveOutcome.Resolved, result.Outcome);
        Assert.False(result.Resolution!.Accessible.IsUnrestricted);
        Assert.True(result.Resolution.Accessible.Allows(tenantX));
        Assert.False(result.Resolution.Accessible.Allows(Guid.NewGuid()));
    }

    [Fact]
    public async Task Resolve_denies_a_suspended_admin_and_reports_unknown_as_not_found()
    {
        var admins = new FakeAdminAccountRepository();
        var suspended = AdminAccount.SelfProvision("suspended-1", "s@org.com", Now);
        suspended.Suspend(Guid.NewGuid());
        admins.Add(suspended);
        var handler = new ResolveAdminHandler(admins);

        Assert.Equal(AdminResolveOutcome.Suspended, (await handler.Handle(new ResolveAdminQuery("suspended-1"), default)).Outcome);
        Assert.Equal(AdminResolveOutcome.NotFound, (await handler.Handle(new ResolveAdminQuery("ghost"), default)).Outcome);
    }

    // ---- SelfProvisionSuperAdmin ----

    [Fact]
    public async Task SelfProvision_creates_a_super_and_audits()
    {
        var admins = new FakeAdminAccountRepository();
        var audit = new FakeAdminAccountAuditWriter();
        var handler = new SelfProvisionSuperAdminHandler(admins, audit, new FakeUnitOfWork(), new FixedClock());

        var resolution = await handler.Handle(new SelfProvisionSuperAdminCommand("super-1", "ops@org.com", "corr"), default);

        var account = Assert.Single(admins.Accounts);
        Assert.Equal(AdminTier.Super, account.Tier);
        Assert.Equal(account.Id, resolution.AdminId);
        Assert.True(resolution.Accessible.IsUnrestricted);
        var row = Assert.Single(audit.Appended);
        Assert.Equal(AdminAuditAction.SelfProvision, row.Action);
        Assert.Equal(account.Id, row.ActorId);
        Assert.Equal(account.Id, row.TargetAdminId);
    }

    [Fact]
    public async Task SelfProvision_is_idempotent_under_a_concurrent_race()
    {
        // The other request won the insert; the unit of work surfaces a ConflictException -> re-read existing.
        var admins = new FakeAdminAccountRepository();
        var existing = AdminAccount.SelfProvision("super-1", "ops@org.com", Now);
        admins.Add(existing);
        var handler = new SelfProvisionSuperAdminHandler(admins, new FakeAdminAccountAuditWriter(), new ConflictingUnitOfWork(), new FixedClock());

        var resolution = await handler.Handle(new SelfProvisionSuperAdminCommand("super-1", "ops@org.com", "corr"), default);

        Assert.Equal(existing.Id, resolution.AdminId);
        Assert.True(resolution.Accessible.IsUnrestricted);
        Assert.Single(admins.Accounts); // no duplicate row
    }

    // ---- BindInvitedAdmin ----

    [Fact]
    public async Task BindInvited_binds_an_unbound_invite_by_email()
    {
        var admins = new FakeAdminAccountRepository();
        admins.Add(AdminAccount.CreateScoped("scoped@org.com", Now));
        var handler = new BindInvitedAdminHandler(admins, new FakeUnitOfWork());

        var result = await handler.Handle(new BindInvitedAdminCommand("scoped-1", "scoped@org.com", "corr"), default);

        Assert.Equal(AdminResolveOutcome.Resolved, result.Outcome);
        Assert.Equal("scoped-1", admins.Accounts[0].Subject);
        Assert.Equal(AdminTier.Scoped, result.Resolution!.Tier);
    }

    [Fact]
    public async Task BindInvited_reports_not_found_when_no_invite_or_already_bound()
    {
        var admins = new FakeAdminAccountRepository();
        var bound = AdminAccount.CreateScoped("bound@org.com", Now);
        bound.BindSubject("someone-else");
        admins.Add(bound);
        var handler = new BindInvitedAdminHandler(admins, new FakeUnitOfWork());

        Assert.Equal(AdminResolveOutcome.NotFound, (await handler.Handle(new BindInvitedAdminCommand("x", "missing@org.com", "c"), default)).Outcome);
        Assert.Equal(AdminResolveOutcome.NotFound, (await handler.Handle(new BindInvitedAdminCommand("x", "bound@org.com", "c"), default)).Outcome);
    }

    [Fact]
    public async Task BindInvited_binds_but_denies_a_suspended_invite()
    {
        var admins = new FakeAdminAccountRepository();
        var invite = AdminAccount.CreateScoped("scoped@org.com", Now);
        invite.Suspend(Guid.NewGuid());
        admins.Add(invite);
        var handler = new BindInvitedAdminHandler(admins, new FakeUnitOfWork());

        var result = await handler.Handle(new BindInvitedAdminCommand("scoped-1", "scoped@org.com", "corr"), default);

        Assert.Equal(AdminResolveOutcome.Suspended, result.Outcome);
        Assert.Equal("scoped-1", admins.Accounts[0].Subject); // subject bound so future logins resolve by subject
    }

    // ---- CreateScopedAdmin ----

    [Fact]
    public async Task CreateScoped_creates_an_invite_and_audits()
    {
        var admins = new FakeAdminAccountRepository();
        var audit = new FakeAdminAccountAuditWriter();
        var actingSuper = Guid.NewGuid();
        var handler = new CreateScopedAdminHandler(admins, audit, new FakeUnitOfWork(), new FixedClock());

        var result = await handler.Handle(new CreateScopedAdminCommand("scoped@org.com", actingSuper, "corr"), default);

        var account = Assert.Single(admins.Accounts);
        Assert.Equal(AdminTier.Scoped, account.Tier);
        Assert.Null(account.Subject);
        Assert.Equal(account.Id, result.AdminId);
        var row = Assert.Single(audit.Appended);
        Assert.Equal(AdminAuditAction.CreateScoped, row.Action);
        Assert.Equal(actingSuper, row.ActorId);
        Assert.Equal(account.Id, row.TargetAdminId);
    }

    [Fact]
    public async Task CreateScoped_rejects_a_duplicate_email()
    {
        var admins = new FakeAdminAccountRepository();
        admins.Add(AdminAccount.CreateScoped("scoped@org.com", Now));
        var handler = new CreateScopedAdminHandler(admins, new FakeAdminAccountAuditWriter(), new FakeUnitOfWork(), new FixedClock());

        await Assert.ThrowsAsync<ConflictException>(async () =>
            await handler.Handle(new CreateScopedAdminCommand("scoped@org.com", Guid.NewGuid(), "corr"), default));
    }

    // ---- AssignTenant ----

    [Fact]
    public async Task Assign_creates_an_assignment_and_audits()
    {
        var admins = new FakeAdminAccountRepository();
        var scoped = AdminAccount.CreateScoped("scoped@org.com", Now);
        admins.Add(scoped);
        var audit = new FakeAdminAccountAuditWriter();
        var tenantId = Guid.NewGuid();
        var actingSuper = Guid.NewGuid();
        var handler = new AssignTenantHandler(admins, new FakeAdminTenantDirectory { ActiveResult = true }, audit, new FakeUnitOfWork(), new FixedClock());

        await handler.Handle(new AssignTenantCommand(scoped.Id, tenantId, actingSuper, "corr"), default);

        var assignment = Assert.Single(admins.Assignments);
        Assert.Equal(tenantId, assignment.TenantId);
        var row = Assert.Single(audit.Appended);
        Assert.Equal(AdminAuditAction.AssignTenant, row.Action);
        Assert.Equal(tenantId, row.TenantId);
        Assert.Equal(scoped.Id, row.TargetAdminId);
    }

    [Fact]
    public async Task Assign_rejects_an_inactive_tenant_a_duplicate_a_super_target_and_an_unknown_admin()
    {
        var admins = new FakeAdminAccountRepository();
        var scoped = AdminAccount.CreateScoped("scoped@org.com", Now);
        admins.Add(scoped);
        var super = AdminAccount.SelfProvision("super-1", "ops@org.com", Now);
        admins.Add(super);
        var tenantId = Guid.NewGuid();

        // Inactive/unknown tenant -> 409 (REQ-4.3)
        var inactive = new AssignTenantHandler(admins, new FakeAdminTenantDirectory { ActiveResult = false }, new FakeAdminAccountAuditWriter(), new FakeUnitOfWork(), new FixedClock());
        await Assert.ThrowsAsync<ConflictException>(async () =>
            await inactive.Handle(new AssignTenantCommand(scoped.Id, tenantId, Guid.NewGuid(), "corr"), default));

        var active = new AssignTenantHandler(admins, new FakeAdminTenantDirectory { ActiveResult = true }, new FakeAdminAccountAuditWriter(), new FakeUnitOfWork(), new FixedClock());

        // Assigning to a Super -> 409 (a Super already has unrestricted reach)
        await Assert.ThrowsAsync<ConflictException>(async () =>
            await active.Handle(new AssignTenantCommand(super.Id, tenantId, Guid.NewGuid(), "corr"), default));

        // Unknown admin -> 404
        await Assert.ThrowsAsync<NotFoundException>(async () =>
            await active.Handle(new AssignTenantCommand(Guid.NewGuid(), tenantId, Guid.NewGuid(), "corr"), default));

        // Duplicate (AdminAccountId, TenantId) -> 409 (REQ-4.4)
        await active.Handle(new AssignTenantCommand(scoped.Id, tenantId, Guid.NewGuid(), "corr"), default);
        await Assert.ThrowsAsync<ConflictException>(async () =>
            await active.Handle(new AssignTenantCommand(scoped.Id, tenantId, Guid.NewGuid(), "corr"), default));
    }

    // ---- UnassignTenant ----

    [Fact]
    public async Task Unassign_hard_deletes_the_assignment_and_audits()
    {
        var admins = new FakeAdminAccountRepository();
        var scoped = AdminAccount.CreateScoped("scoped@org.com", Now);
        admins.Add(scoped);
        var tenantId = Guid.NewGuid();
        admins.AddAssignment(AdminTenantAssignment.Create(scoped.Id, tenantId, Guid.NewGuid(), Now));
        var audit = new FakeAdminAccountAuditWriter();
        var handler = new UnassignTenantHandler(admins, audit, new FakeUnitOfWork(), new FixedClock());

        await handler.Handle(new UnassignTenantCommand(scoped.Id, tenantId, Guid.NewGuid(), "corr"), default);

        Assert.Empty(admins.Assignments);
        Assert.Equal(AdminAuditAction.UnassignTenant, Assert.Single(audit.Appended).Action);
    }

    [Fact]
    public async Task Unassign_rejects_an_unknown_assignment()
    {
        var admins = new FakeAdminAccountRepository();
        var handler = new UnassignTenantHandler(admins, new FakeAdminAccountAuditWriter(), new FakeUnitOfWork(), new FixedClock());

        await Assert.ThrowsAsync<NotFoundException>(async () =>
            await handler.Handle(new UnassignTenantCommand(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "corr"), default));
    }

    // ---- SuspendAdmin ----

    [Fact]
    public async Task Suspend_revokes_another_admin_and_audits()
    {
        var admins = new FakeAdminAccountRepository();
        var target = AdminAccount.CreateScoped("scoped@org.com", Now);
        admins.Add(target);
        var audit = new FakeAdminAccountAuditWriter();
        var handler = new SuspendAdminHandler(admins, audit, new FakeUnitOfWork(), new FixedClock());

        var result = await handler.Handle(new SuspendAdminCommand(target.Id, Guid.NewGuid(), "corr"), default);

        Assert.Equal("Suspended", result.Status);
        Assert.Equal(AdminStatus.Suspended, admins.Accounts[0].Status);
        Assert.Equal(AdminAuditAction.Suspend, Assert.Single(audit.Appended).Action);
    }

    [Fact]
    public async Task Suspend_rejects_self_suspension_and_an_unknown_target()
    {
        var admins = new FakeAdminAccountRepository();
        var self = AdminAccount.SelfProvision("super-1", "ops@org.com", Now);
        admins.Add(self);
        var handler = new SuspendAdminHandler(admins, new FakeAdminAccountAuditWriter(), new FakeUnitOfWork(), new FixedClock());

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await handler.Handle(new SuspendAdminCommand(self.Id, self.Id, "corr"), default));
        await Assert.ThrowsAsync<NotFoundException>(async () =>
            await handler.Handle(new SuspendAdminCommand(Guid.NewGuid(), Guid.NewGuid(), "corr"), default));
    }
}
