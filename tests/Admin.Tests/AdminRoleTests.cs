using Admin.Domain;

namespace Admin.Tests;

public sealed class AdminRoleTests
{
    private static readonly IReadOnlySet<string> Catalog = AdminPermissions.AllKeys;

    [Fact]
    public void Create_trims_inputs_and_keeps_a_deduped_valid_subset()
    {
        var role = AdminRole.Create("  ops_admin ", "  Ops  ", " desc ", " blue ", AdminRoleStatus.Active,
            ["txn.view", "txn.view", "  merchant.view  ", "  "], Catalog);

        Assert.Equal("ops_admin", role.Code);
        Assert.Equal("Ops", role.Name);
        Assert.Equal("desc", role.Description);
        Assert.Equal("blue", role.Color);
        Assert.Equal(AdminRoleStatus.Active, role.Status);
        Assert.Equal(new HashSet<string> { "txn.view", "merchant.view" }, role.PermissionKeys.ToHashSet());
    }

    [Fact]
    public void Create_rejects_an_unknown_permission_key()
    {
        Assert.Throws<ArgumentException>(() =>
            AdminRole.Create("r", "R", null, null, AdminRoleStatus.Active, ["txn.view", "bogus.key"], Catalog));
    }

    [Fact]
    public void Create_rejects_a_blank_or_overlong_code_and_a_blank_name()
    {
        Assert.Throws<ArgumentException>(() => AdminRole.Create("   ", "R", null, null, AdminRoleStatus.Active, [], Catalog));
        Assert.Throws<ArgumentException>(() => AdminRole.Create(new string('x', 65), "R", null, null, AdminRoleStatus.Active, [], Catalog));
        Assert.Throws<ArgumentException>(() => AdminRole.Create("r", "  ", null, null, AdminRoleStatus.Active, [], Catalog));
    }

    [Fact]
    public void Create_rejects_a_code_with_non_slug_characters()
    {
        Assert.Throws<ArgumentException>(() => AdminRole.Create("ops admin", "R", null, null, AdminRoleStatus.Active, [], Catalog));
        Assert.Throws<ArgumentException>(() => AdminRole.Create("Ops_Admin", "R", null, null, AdminRoleStatus.Active, [], Catalog));
        Assert.Throws<ArgumentException>(() => AdminRole.Create("ops/admin", "R", null, null, AdminRoleStatus.Active, [], Catalog));
    }

    [Fact]
    public void SetPermissions_replaces_the_set_and_rejects_unknown_keys()
    {
        var role = AdminRole.Create("r", "R", null, null, AdminRoleStatus.Active, ["txn.view"], Catalog);

        role.SetPermissions(["merchant.view", "merchant.view", "user.view"], Catalog);
        Assert.Equal(new HashSet<string> { "merchant.view", "user.view" }, role.PermissionKeys.ToHashSet());

        Assert.Throws<ArgumentException>(() => role.SetPermissions(["not.a.key"], Catalog));
    }

    [Fact]
    public void The_super_admin_seed_cannot_be_deactivated_but_other_roles_can()
    {
        var seed = AdminRole.Create(AdminRole.SuperAdminCode, "Super", null, null, AdminRoleStatus.Active, [], Catalog);
        Assert.True(seed.IsSuperAdminSeed);
        Assert.Throws<InvalidOperationException>(seed.Deactivate);

        var other = AdminRole.Create("ops", "Ops", null, null, AdminRoleStatus.Active, [], Catalog);
        other.Deactivate();
        Assert.Equal(AdminRoleStatus.Inactive, other.Status);
    }

    [Fact]
    public async Task Effective_permissions_are_the_union_over_active_roles_only()
    {
        var repo = new FakeAdminRoleRepository();
        var adminId = Guid.NewGuid();
        var active = AdminRole.Create("a", "A", null, null, AdminRoleStatus.Active, ["txn.view", "user.view"], Catalog);
        var inactive = AdminRole.Create("b", "B", null, null, AdminRoleStatus.Inactive, ["settlement.run"], Catalog);
        repo.Add(active);
        repo.Add(inactive);
        repo.AddAssignment(AdminRoleAssignment.Create(adminId, active.Id, adminId, DateTime.UnixEpoch));
        repo.AddAssignment(AdminRoleAssignment.Create(adminId, inactive.Id, adminId, DateTime.UnixEpoch));

        var perms = await repo.ListEffectivePermissionsAsync(adminId, default);

        Assert.Equal(new HashSet<string> { "txn.view", "user.view" }, perms.ToHashSet());
    }

    [Fact]
    public void The_code_catalog_matches_the_advertised_shape()
    {
        // 16/6: producer-google-sso REQ-18.1 added the cross-catalog `producer` group + producer.approve/reject (S1).
        Assert.Equal(16, AdminPermissions.AllKeys.Count);
        Assert.Equal(6, AdminPermissions.GroupKeys.Count);
        Assert.All(AdminPermissions.All, p => Assert.Contains(p.GroupKey, AdminPermissions.GroupKeys));
    }
}
