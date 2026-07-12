using Admins.Domain.Permissions;
using Admins.Domain.Roles;
using Admins.Domain.Users;

namespace Admins.Tests;

public sealed class AdminRoleTests
{
    private static readonly IReadOnlySet<string> Catalog = Keys.AllKeys;

    [Fact]
    public void Create_trims_inputs_and_keeps_a_deduped_valid_subset()
    {
        var role = Role.Create("  ops_admin ", "  Ops  ", " desc ", " blue ", RoleStatus.Active,
            ["txn.view", "txn.view", "  merchant.view  ", "  "], Catalog);

        Assert.Equal("ops_admin", role.Code);
        Assert.Equal("Ops", role.Name);
        Assert.Equal("desc", role.Description);
        Assert.Equal("blue", role.Color);
        Assert.Equal(RoleStatus.Active, role.Status);
        Assert.Equal(new HashSet<string> { "txn.view", "merchant.view" }, role.PermissionKeys.ToHashSet());
    }

    [Fact]
    public void Create_rejects_an_unknown_permission_key()
    {
        Assert.Throws<ArgumentException>(() =>
            Role.Create("r", "R", null, null, RoleStatus.Active, ["txn.view", "bogus.key"], Catalog));
    }

    [Fact]
    public void Create_rejects_a_blank_or_overlong_code_and_a_blank_name()
    {
        Assert.Throws<ArgumentException>(() => Role.Create("   ", "R", null, null, RoleStatus.Active, [], Catalog));
        Assert.Throws<ArgumentException>(() => Role.Create(new string('x', 65), "R", null, null, RoleStatus.Active, [], Catalog));
        Assert.Throws<ArgumentException>(() => Role.Create("r", "  ", null, null, RoleStatus.Active, [], Catalog));
    }

    [Fact]
    public void Create_rejects_a_code_with_non_slug_characters()
    {
        Assert.Throws<ArgumentException>(() => Role.Create("ops admin", "R", null, null, RoleStatus.Active, [], Catalog));
        Assert.Throws<ArgumentException>(() => Role.Create("Ops_Admin", "R", null, null, RoleStatus.Active, [], Catalog));
        Assert.Throws<ArgumentException>(() => Role.Create("ops/admin", "R", null, null, RoleStatus.Active, [], Catalog));
    }

    [Fact]
    public void SetPermissions_replaces_the_set_and_rejects_unknown_keys()
    {
        var role = Role.Create("r", "R", null, null, RoleStatus.Active, ["txn.view"], Catalog);

        role.SetPermissions(["merchant.view", "merchant.view", "user.view"], Catalog);
        Assert.Equal(new HashSet<string> { "merchant.view", "user.view" }, role.PermissionKeys.ToHashSet());

        Assert.Throws<ArgumentException>(() => role.SetPermissions(["not.a.key"], Catalog));
    }

    [Fact]
    public void The_super_admin_seed_cannot_be_deactivated_but_other_roles_can()
    {
        var seed = Role.Create(Role.SuperAdminCode, "Super", null, null, RoleStatus.Active, [], Catalog);
        Assert.True(seed.IsSuperAdminSeed);
        Assert.Throws<InvalidOperationException>(seed.Deactivate);

        var other = Role.Create("ops", "Ops", null, null, RoleStatus.Active, [], Catalog);
        other.Deactivate();
        Assert.Equal(RoleStatus.Inactive, other.Status);
    }

    [Fact]
    public async Task Effective_permissions_are_the_union_over_active_roles_only()
    {
        var repo = new FakeAdminRoleRepository();
        var adminId = Guid.NewGuid();
        var active = Role.Create("a", "A", null, null, RoleStatus.Active, ["txn.view", "user.view"], Catalog);
        var inactive = Role.Create("b", "B", null, null, RoleStatus.Inactive, ["settlement.run"], Catalog);
        repo.Add(active);
        repo.Add(inactive);
        repo.AddAssignment(RoleAssignment.Create(adminId, active.Id, adminId, DateTime.UnixEpoch));
        repo.AddAssignment(RoleAssignment.Create(adminId, inactive.Id, adminId, DateTime.UnixEpoch));

        var perms = await repo.ListEffectivePermissionsAsync(adminId, default);

        Assert.Equal(new HashSet<string> { "txn.view", "user.view" }, perms.ToHashSet());
    }

    [Fact]
    public void The_code_catalog_matches_the_advertised_shape()
    {
        // 16/6: producer-google-sso REQ-18.1 added the cross-catalog `producer` group + producer.approve/reject (S1).
        Assert.Equal(16, Keys.AllKeys.Count);
        Assert.Equal(6, Keys.GroupKeys.Count);
        Assert.All(Keys.All, p => Assert.Contains(p.GroupKey, Keys.GroupKeys));
    }
}
