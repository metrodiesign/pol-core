using Admin.Application.DeleteRole;
using Admin.Application.UpdateRole;
using Admin.Domain;
using BuildingBlocks.Application;

namespace Admin.Tests;

/// <summary>Role-mutation guards (admin-role-rbac REQ-4.4 / REQ-8.3). The super_admin seed is the recovery anchor:
/// deleting or deactivating it would re-open the Super-tier lockout, so both are blocked. Every conflict carries a
/// precise, caller-safe <see cref="ConflictException.SafeDetail"/> so the frontend can tell WHY a 409 happened
/// (the bug behind the 409-on-edit report). A normal edit that keeps its permissions must NOT 409.</summary>
public sealed class AdminRoleHandlerTests
{
    private static AdminRole Seed(string code) =>
        AdminRole.Create(code, code, null, null, AdminRoleStatus.Active, [], AdminPermissions.AllKeys);

    private static DeleteRoleHandler Handler(FakeAdminRoleRepository roles) =>
        new(roles, new FakeAdminAccountAuditWriter(), new FakeUnitOfWork(), new FixedClock());

    private static UpdateRoleHandler UpdateHandler(FakeAdminRoleRepository roles) =>
        new(roles, new FakeAdminAccountAuditWriter(), new FakeUnitOfWork(), new FixedClock());

    [Fact]
    public async Task Deleting_the_super_admin_seed_is_blocked_even_with_no_bound_users()
    {
        var roles = new FakeAdminRoleRepository();
        roles.Add(Seed(AdminRole.SuperAdminCode));

        var ex = await Assert.ThrowsAsync<ConflictException>(() =>
            Handler(roles).Handle(new DeleteRoleCommand(AdminRole.SuperAdminCode, Guid.NewGuid(), "corr"), default).AsTask());

        Assert.Equal("The super_admin role cannot be deleted.", ex.SafeDetail);
        Assert.Single(roles.Roles); // anchor not removed
    }

    [Fact]
    public async Task A_normal_role_with_no_bound_users_is_deleted()
    {
        var roles = new FakeAdminRoleRepository();
        roles.Add(Seed("ops_admin"));

        var result = await Handler(roles).Handle(new DeleteRoleCommand("ops_admin", Guid.NewGuid(), "corr"), default);

        Assert.Equal("ops_admin", result.Code);
        Assert.Empty(roles.Roles);
    }

    [Fact]
    public async Task Deleting_a_role_with_bound_users_is_blocked_with_a_precise_detail()
    {
        var roles = new FakeAdminRoleRepository();
        var role = Seed("ops_admin");
        roles.Add(role);
        roles.AddAssignment(AdminRoleAssignment.Create(Guid.NewGuid(), role.Id, Guid.NewGuid(), new FixedClock().UtcNow));

        var ex = await Assert.ThrowsAsync<ConflictException>(() =>
            Handler(roles).Handle(new DeleteRoleCommand("ops_admin", Guid.NewGuid(), "corr"), default).AsTask());

        Assert.Equal("A role with bound users cannot be deleted; reassign or remove its users first.", ex.SafeDetail);
        Assert.Single(roles.Roles); // not removed
    }

    [Fact]
    public async Task Deactivating_the_super_admin_seed_is_blocked_with_a_precise_detail()
    {
        var roles = new FakeAdminRoleRepository();
        roles.Add(Seed(AdminRole.SuperAdminCode));

        var ex = await Assert.ThrowsAsync<ConflictException>(() =>
            UpdateHandler(roles).Handle(new UpdateRoleCommand(
                AdminRole.SuperAdminCode, "Super", null, null, AdminRoleStatus.Inactive, [],
                Guid.NewGuid(), "corr"), default).AsTask());

        Assert.Equal("The super_admin role cannot be deactivated.", ex.SafeDetail);
    }

    [Fact]
    public async Task Editing_a_role_that_keeps_its_permission_set_does_not_conflict()
    {
        // The frontend's report: a plain PUT that re-sends the role's existing permissions must succeed, not 409.
        // SetPermissions is a delta over the tracked child rows, so an unchanged set writes nothing.
        var roles = new FakeAdminRoleRepository();
        var keys = AdminPermissions.AllKeys.Take(2).ToArray();
        roles.Add(AdminRole.Create("ops_admin", "Ops", null, null, AdminRoleStatus.Active, keys, AdminPermissions.AllKeys));

        var result = await UpdateHandler(roles).Handle(new UpdateRoleCommand(
            "ops_admin", "Ops Team", "desc", "#abc", AdminRoleStatus.Active, keys,
            Guid.NewGuid(), "corr"), default);

        Assert.Equal("ops_admin", result.Code);
        Assert.Equal("Ops Team", result.Name);
        Assert.Equal(keys.Length, result.PermissionKeys.Count);
        Assert.All(keys, k => Assert.Contains(k, result.PermissionKeys));
    }
}
