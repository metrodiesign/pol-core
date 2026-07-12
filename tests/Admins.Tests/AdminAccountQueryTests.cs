using Admins.Application;
using Admins.Application.MasterData;
using Admins.Application.Permissions;
using Admins.Application.Roles;
using Admins.Application.Users;
using Admins.Domain.MasterData;
using Admins.Domain.Permissions;
using Admins.Domain.Roles;
using Admins.Domain.Users;

namespace Admins.Tests;

/// <summary>Read-side handlers for admin-account-management (REQ-2 detail, REQ-6 effective-permissions). Proves the
/// 404-on-unknown existence checks, that detail carries ALL assigned role codes (incl. Inactive) with the correct
/// accessible shape, and that effective-permissions is ACTIVE-only and ordinal-ascending even for a suspended
/// target.</summary>
public sealed class PlatformUserQueryTests
{
    private static readonly DateTime T0 = new(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Actor = Guid.NewGuid();

    private static Role MakeRole(string code, RoleStatus status, params string[] keys) =>
        Role.Create(code, code, null, null, status, keys, Keys.AllKeys);

    // ===== REQ-2: detail =====
    [Fact]
    public async Task GetAdminById_returns_null_for_unknown_id()
    {
        var handler = new GetAdminByIdHandler(new FakePlatformUserRepository(), new FakeAdminRoleRepository(), new FakeMasterDataStore());
        Assert.Null(await handler.Handle(new GetAdminByIdQuery(Guid.NewGuid()), default));
    }

    [Fact]
    public async Task GetAdminById_super_is_unrestricted_with_all_role_codes_incl_inactive()
    {
        var accounts = new FakePlatformUserRepository();
        var roles = new FakeAdminRoleRepository();
        var super = User.SelfProvision("sub-1", "super@x", T0);
        accounts.Add(super);

        var active = MakeRole("ops", RoleStatus.Active, "txn.view");
        var inactive = MakeRole("legacy", RoleStatus.Inactive, "audit.view");
        roles.Roles.AddRange([active, inactive]);
        roles.AddAssignment(RoleAssignment.Create(super.Id, active.Id, Actor, T0));
        roles.AddAssignment(RoleAssignment.Create(super.Id, inactive.Id, Actor, T0));

        var detail = await new GetAdminByIdHandler(accounts, roles, new FakeMasterDataStore()).Handle(new GetAdminByIdQuery(super.Id), default);

        Assert.NotNull(detail);
        Assert.True(detail!.SubjectBound);                 // Super's subject is bound
        Assert.Equal(Tier.Super, detail.Tier);
        Assert.True(detail.Accessible.IsUnrestricted);     // /me shape for a Super
        Assert.Equal(new[] { "legacy", "ops" }, detail.RoleCodes);   // ALL assigned, incl. Inactive, code-sorted
    }

    [Fact]
    public async Task GetAdminById_scoped_carries_assigned_merchant_set_and_unbound_flag()
    {
        var accounts = new FakePlatformUserRepository();
        var scoped = User.CreateScoped("scoped@x", T0);   // subject unbound (pending invite)
        accounts.Add(scoped);
        var merchant = Guid.NewGuid();
        accounts.AddAssignment(MerchantAccess.Create(scoped.Id, merchant, Actor, T0));

        var detail = await new GetAdminByIdHandler(accounts, new FakeAdminRoleRepository(), new FakeMasterDataStore())
            .Handle(new GetAdminByIdQuery(scoped.Id), default);

        Assert.NotNull(detail);
        Assert.False(detail!.SubjectBound);                // invite not yet claimed
        Assert.False(detail.Accessible.IsUnrestricted);
        Assert.Equal(new[] { merchant }, detail.Accessible.Merchants);
        Assert.Empty(detail.RoleCodes);
    }

    // ===== REQ-6: effective permissions =====
    [Fact]
    public async Task GetEffectivePermissions_returns_null_for_unknown_id()
    {
        var handler = new GetEffectivePermissionsHandler(new FakePlatformUserRepository(), new FakeAdminRoleRepository());
        Assert.Null(await handler.Handle(new GetEffectivePermissionsQuery(Guid.NewGuid()), default));
    }

    [Fact]
    public async Task GetEffectivePermissions_is_active_only_and_ordinal_ascending()
    {
        var accounts = new FakePlatformUserRepository();
        var roles = new FakeAdminRoleRepository();
        var admin = User.SelfProvision("sub-2", "a@x", T0);
        accounts.Add(admin);

        // Active role grants keys in NON-sorted order; an Inactive role's key must NOT appear.
        var active = MakeRole("ops", RoleStatus.Active, "txn.view", "audit.view", "merchant.view");
        var inactive = MakeRole("legacy", RoleStatus.Inactive, "settings.manage");
        roles.Roles.AddRange([active, inactive]);
        roles.AddAssignment(RoleAssignment.Create(admin.Id, active.Id, Actor, T0));
        roles.AddAssignment(RoleAssignment.Create(admin.Id, inactive.Id, Actor, T0));

        var perms = await new GetEffectivePermissionsHandler(accounts, roles)
            .Handle(new GetEffectivePermissionsQuery(admin.Id), default);

        Assert.Equal(new[] { "audit.view", "merchant.view", "txn.view" }, perms);   // ascending, no settings.manage
    }

    [Fact]
    public async Task GetEffectivePermissions_works_for_a_suspended_target()
    {
        var accounts = new FakePlatformUserRepository();
        var roles = new FakeAdminRoleRepository();
        var admin = User.SelfProvision("sub-3", "s@x", T0);
        admin.Suspend(Actor);                              // suspension blocks sign-in, not role grants
        accounts.Add(admin);
        var active = MakeRole("ops", RoleStatus.Active, "txn.view");
        roles.Roles.Add(active);
        roles.AddAssignment(RoleAssignment.Create(admin.Id, active.Id, Actor, T0));

        var perms = await new GetEffectivePermissionsHandler(accounts, roles)
            .Handle(new GetEffectivePermissionsQuery(admin.Id), default);

        Assert.Equal(new[] { "txn.view" }, perms);
    }
}
