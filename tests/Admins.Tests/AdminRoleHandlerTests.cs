using Admins.Application.DeleteRole;
using Admins.Domain;
using BuildingBlocks.Application;

namespace Admins.Tests;

/// <summary>Delete-role guards (admin-role-rbac REQ-4.4 / REQ-8.3). The super_admin seed is the recovery anchor:
/// deleting it would re-open the Super-tier lockout the deactivation guard already prevents, so it is blocked even
/// with zero bound users. A normal, unbound role deletes cleanly.</summary>
public sealed class AdminRoleHandlerTests
{
    private static AdminRole Seed(string code) =>
        AdminRole.Create(code, code, null, null, AdminRoleStatus.Active, [], AdminPermissions.AllKeys);

    private static DeleteRoleHandler Handler(FakeAdminRoleRepository roles) =>
        new(roles, new FakePlatformUserAuditWriter(), new FakeUnitOfWork(), new FixedClock());

    [Fact]
    public async Task Deleting_the_super_admin_seed_is_blocked_even_with_no_bound_users()
    {
        var roles = new FakeAdminRoleRepository();
        roles.Add(Seed(AdminRole.SuperAdminCode));

        await Assert.ThrowsAsync<ConflictException>(() =>
            Handler(roles).Handle(new DeleteRoleCommand(AdminRole.SuperAdminCode, Guid.NewGuid(), "corr"), default).AsTask());

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
}
