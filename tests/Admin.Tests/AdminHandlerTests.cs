using Admin.Application.AssignMerchant;
using Admin.Application.BindInvitedAdmin;
using Admin.Application.CreateScopedAdmin;
using Admin.Application.ResolveAdmin;
using Admin.Application.SelfProvisionSuperAdmin;
using Admin.Application.SuspendAdmin;
using Admin.Application.UnassignMerchant;
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
        var admins = new FakePlatformUserRepository();
        admins.Add(PlatformUser.SelfProvision("super-1", "ops@org.com", Now));
        var handler = new ResolveAdminHandler(admins, new FakeAdminRoleRepository());

        var result = await handler.Handle(new ResolveAdminQuery("super-1"), default);

        Assert.Equal(AdminResolveOutcome.Resolved, result.Outcome);
        Assert.True(result.Resolution!.Accessible.IsUnrestricted);
        Assert.Equal(PlatformUserTier.Super, result.Resolution.Tier);
    }

    [Fact]
    public async Task Resolve_returns_the_assigned_set_for_an_active_scoped_admin()
    {
        var admins = new FakePlatformUserRepository();
        var scoped = PlatformUser.CreateScoped("scoped@org.com", Now);
        scoped.BindSubject("scoped-1");
        admins.Add(scoped);
        var merchantX = Guid.NewGuid();
        admins.AddAssignment(PlatformMerchantAccess.Create(scoped.Id, merchantX, Guid.NewGuid(), Now));
        var handler = new ResolveAdminHandler(admins, new FakeAdminRoleRepository());

        var result = await handler.Handle(new ResolveAdminQuery("scoped-1"), default);

        Assert.Equal(AdminResolveOutcome.Resolved, result.Outcome);
        Assert.False(result.Resolution!.Accessible.IsUnrestricted);
        Assert.True(result.Resolution.Accessible.Allows(merchantX));
        Assert.False(result.Resolution.Accessible.Allows(Guid.NewGuid()));
    }

    [Fact]
    public async Task Resolve_denies_a_suspended_admin_and_reports_unknown_as_not_found()
    {
        var admins = new FakePlatformUserRepository();
        var suspended = PlatformUser.SelfProvision("suspended-1", "s@org.com", Now);
        suspended.Suspend(Guid.NewGuid());
        admins.Add(suspended);
        var handler = new ResolveAdminHandler(admins, new FakeAdminRoleRepository());

        Assert.Equal(AdminResolveOutcome.Suspended, (await handler.Handle(new ResolveAdminQuery("suspended-1"), default)).Outcome);
        Assert.Equal(AdminResolveOutcome.NotFound, (await handler.Handle(new ResolveAdminQuery("ghost"), default)).Outcome);
    }

    // ---- SelfProvisionSuperAdmin ----

    [Fact]
    public async Task SelfProvision_creates_a_super_and_audits()
    {
        var admins = new FakePlatformUserRepository();
        var audit = new FakePlatformUserAuditWriter();
        var handler = new SelfProvisionSuperAdminHandler(admins, new FakeAdminRoleRepository(), audit, new FakeUnitOfWork(), new FixedClock());

        var resolution = await handler.Handle(new SelfProvisionSuperAdminCommand("super-1", "ops@org.com", "corr"), default);

        var account = Assert.Single(admins.Accounts);
        Assert.Equal(PlatformUserTier.Super, account.Tier);
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
        var admins = new FakePlatformUserRepository();
        var existing = PlatformUser.SelfProvision("super-1", "ops@org.com", Now);
        admins.Add(existing);
        var handler = new SelfProvisionSuperAdminHandler(admins, new FakeAdminRoleRepository(), new FakePlatformUserAuditWriter(), new ConflictingUnitOfWork(), new FixedClock());

        var resolution = await handler.Handle(new SelfProvisionSuperAdminCommand("super-1", "ops@org.com", "corr"), default);

        Assert.Equal(existing.Id, resolution.AdminId);
        Assert.True(resolution.Accessible.IsUnrestricted);
        Assert.Single(admins.Accounts); // no duplicate row
    }

    [Fact]
    public async Task SelfProvision_binds_the_seed_super_admin_role_and_audits_it()
    {
        var admins = new FakePlatformUserRepository();
        var roles = new FakeAdminRoleRepository();
        var seed = AdminRole.Create(AdminRole.SuperAdminCode, "Super", null, "red", AdminRoleStatus.Active,
            ["user.roles"], AdminPermissions.AllKeys);
        roles.Add(seed);
        var audit = new FakePlatformUserAuditWriter();
        var handler = new SelfProvisionSuperAdminHandler(admins, roles, audit, new FakeUnitOfWork(), new FixedClock());

        var resolution = await handler.Handle(new SelfProvisionSuperAdminCommand("super-1", "ops@org.com", "corr"), default);

        var assignment = Assert.Single(roles.Assignments);
        Assert.Equal(resolution.AdminId, assignment.PlatformUserId);
        Assert.Equal(seed.Id, assignment.RoleId);
        Assert.Contains(audit.Appended, a => a.Action == AdminAuditAction.RoleAssigned && a.TargetRoleId == seed.Id);
    }

    // ---- BindInvitedAdmin ----

    [Fact]
    public async Task BindInvited_binds_an_unbound_invite_by_email()
    {
        var admins = new FakePlatformUserRepository();
        admins.Add(PlatformUser.CreateScoped("scoped@org.com", Now));
        var handler = new BindInvitedAdminHandler(admins, new FakeUnitOfWork());

        var result = await handler.Handle(new BindInvitedAdminCommand("scoped-1", "scoped@org.com", "corr"), default);

        Assert.Equal(AdminResolveOutcome.Resolved, result.Outcome);
        Assert.Equal("scoped-1", admins.Accounts[0].Subject);
        Assert.Equal(PlatformUserTier.Scoped, result.Resolution!.Tier);
    }

    [Fact]
    public async Task BindInvited_reports_not_found_when_no_invite_or_already_bound()
    {
        var admins = new FakePlatformUserRepository();
        var bound = PlatformUser.CreateScoped("bound@org.com", Now);
        bound.BindSubject("someone-else");
        admins.Add(bound);
        var handler = new BindInvitedAdminHandler(admins, new FakeUnitOfWork());

        Assert.Equal(AdminResolveOutcome.NotFound, (await handler.Handle(new BindInvitedAdminCommand("x", "missing@org.com", "c"), default)).Outcome);
        Assert.Equal(AdminResolveOutcome.NotFound, (await handler.Handle(new BindInvitedAdminCommand("x", "bound@org.com", "c"), default)).Outcome);
    }

    [Fact]
    public async Task BindInvited_binds_but_denies_a_suspended_invite()
    {
        var admins = new FakePlatformUserRepository();
        var invite = PlatformUser.CreateScoped("scoped@org.com", Now);
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
        var admins = new FakePlatformUserRepository();
        var audit = new FakePlatformUserAuditWriter();
        var actingSuper = Guid.NewGuid();
        var handler = new CreateScopedAdminHandler(admins, audit, new FakeMasterDataStore(), new FakeUnitOfWork(), new FixedClock());

        var result = await handler.Handle(new CreateScopedAdminCommand("scoped@org.com", actingSuper, "corr"), default);

        var account = Assert.Single(admins.Accounts);
        Assert.Equal(PlatformUserTier.Scoped, account.Tier);
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
        var admins = new FakePlatformUserRepository();
        admins.Add(PlatformUser.CreateScoped("scoped@org.com", Now));
        var handler = new CreateScopedAdminHandler(admins, new FakePlatformUserAuditWriter(), new FakeMasterDataStore(), new FakeUnitOfWork(), new FixedClock());

        await Assert.ThrowsAsync<ConflictException>(async () =>
            await handler.Handle(new CreateScopedAdminCommand("scoped@org.com", Guid.NewGuid(), "corr"), default));
    }

    // ---- AssignMerchant ----

    [Fact]
    public async Task Assign_creates_an_assignment_and_audits()
    {
        var admins = new FakePlatformUserRepository();
        var scoped = PlatformUser.CreateScoped("scoped@org.com", Now);
        admins.Add(scoped);
        var audit = new FakePlatformUserAuditWriter();
        var merchantId = Guid.NewGuid();
        var actingSuper = Guid.NewGuid();
        var handler = new AssignMerchantHandler(admins, new FakeAdminMerchantDirectory { ActiveResult = true }, audit, new FakeUnitOfWork(), new FixedClock());

        await handler.Handle(new AssignMerchantCommand(scoped.Id, merchantId, actingSuper, "corr"), default);

        var assignment = Assert.Single(admins.Assignments);
        Assert.Equal(merchantId, assignment.MerchantId);
        var row = Assert.Single(audit.Appended);
        Assert.Equal(AdminAuditAction.AssignMerchant, row.Action);
        Assert.Equal(merchantId, row.MerchantId);
        Assert.Equal(scoped.Id, row.TargetAdminId);
    }

    [Fact]
    public async Task Assign_rejects_an_inactive_merchant_a_duplicate_a_super_target_and_an_unknown_admin()
    {
        var admins = new FakePlatformUserRepository();
        var scoped = PlatformUser.CreateScoped("scoped@org.com", Now);
        admins.Add(scoped);
        var super = PlatformUser.SelfProvision("super-1", "ops@org.com", Now);
        admins.Add(super);
        var merchantId = Guid.NewGuid();

        // Inactive/unknown merchant -> 409 (REQ-4.3)
        var inactive = new AssignMerchantHandler(admins, new FakeAdminMerchantDirectory { ActiveResult = false }, new FakePlatformUserAuditWriter(), new FakeUnitOfWork(), new FixedClock());
        await Assert.ThrowsAsync<ConflictException>(async () =>
            await inactive.Handle(new AssignMerchantCommand(scoped.Id, merchantId, Guid.NewGuid(), "corr"), default));

        var active = new AssignMerchantHandler(admins, new FakeAdminMerchantDirectory { ActiveResult = true }, new FakePlatformUserAuditWriter(), new FakeUnitOfWork(), new FixedClock());

        // Assigning to a Super -> 409 (a Super already has unrestricted reach)
        await Assert.ThrowsAsync<ConflictException>(async () =>
            await active.Handle(new AssignMerchantCommand(super.Id, merchantId, Guid.NewGuid(), "corr"), default));

        // Unknown admin -> 404
        await Assert.ThrowsAsync<NotFoundException>(async () =>
            await active.Handle(new AssignMerchantCommand(Guid.NewGuid(), merchantId, Guid.NewGuid(), "corr"), default));

        // Duplicate (PlatformUserId, MerchantId) -> 409 (REQ-4.4)
        await active.Handle(new AssignMerchantCommand(scoped.Id, merchantId, Guid.NewGuid(), "corr"), default);
        await Assert.ThrowsAsync<ConflictException>(async () =>
            await active.Handle(new AssignMerchantCommand(scoped.Id, merchantId, Guid.NewGuid(), "corr"), default));
    }

    // ---- UnassignMerchant ----

    [Fact]
    public async Task Unassign_hard_deletes_the_assignment_and_audits()
    {
        var admins = new FakePlatformUserRepository();
        var scoped = PlatformUser.CreateScoped("scoped@org.com", Now);
        admins.Add(scoped);
        var merchantId = Guid.NewGuid();
        admins.AddAssignment(PlatformMerchantAccess.Create(scoped.Id, merchantId, Guid.NewGuid(), Now));
        var audit = new FakePlatformUserAuditWriter();
        var handler = new UnassignMerchantHandler(admins, audit, new FakeUnitOfWork(), new FixedClock());

        await handler.Handle(new UnassignMerchantCommand(scoped.Id, merchantId, Guid.NewGuid(), "corr"), default);

        Assert.Empty(admins.Assignments);
        Assert.Equal(AdminAuditAction.UnassignMerchant, Assert.Single(audit.Appended).Action);
    }

    [Fact]
    public async Task Unassign_rejects_an_unknown_assignment()
    {
        var admins = new FakePlatformUserRepository();
        var handler = new UnassignMerchantHandler(admins, new FakePlatformUserAuditWriter(), new FakeUnitOfWork(), new FixedClock());

        await Assert.ThrowsAsync<NotFoundException>(async () =>
            await handler.Handle(new UnassignMerchantCommand(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "corr"), default));
    }

    // ---- SuspendAdmin ----

    [Fact]
    public async Task Suspend_revokes_another_admin_and_audits()
    {
        var admins = new FakePlatformUserRepository();
        var target = PlatformUser.CreateScoped("scoped@org.com", Now);
        admins.Add(target);
        var audit = new FakePlatformUserAuditWriter();
        var handler = new SuspendAdminHandler(admins, audit, new FakeUnitOfWork(), new FixedClock());

        var result = await handler.Handle(new SuspendAdminCommand(target.Id, Guid.NewGuid(), "corr"), default);

        Assert.Equal("Suspended", result.Status);
        Assert.Equal(AdminStatus.Suspended, admins.Accounts[0].Status);
        Assert.Equal(AdminAuditAction.Suspend, Assert.Single(audit.Appended).Action);
    }

    [Fact]
    public async Task Suspend_rejects_self_suspension_and_an_unknown_target()
    {
        var admins = new FakePlatformUserRepository();
        var self = PlatformUser.SelfProvision("super-1", "ops@org.com", Now);
        admins.Add(self);
        var handler = new SuspendAdminHandler(admins, new FakePlatformUserAuditWriter(), new FakeUnitOfWork(), new FixedClock());

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await handler.Handle(new SuspendAdminCommand(self.Id, self.Id, "corr"), default));
        await Assert.ThrowsAsync<NotFoundException>(async () =>
            await handler.Handle(new SuspendAdminCommand(Guid.NewGuid(), Guid.NewGuid(), "corr"), default));
    }
}
