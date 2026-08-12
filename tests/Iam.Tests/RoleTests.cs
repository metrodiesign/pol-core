using Iam.Domain.Permissions;
using Iam.Domain.Roles;

namespace Iam.Tests;

public sealed class RoleTests
{
    private static readonly IReadOnlyDictionary<string, Scope> Catalog = Keys.KeySide;
    private static readonly Guid MerchantId = Guid.NewGuid();

    [Fact]
    public void Create_trims_inputs_and_keeps_a_deduped_valid_subset()
    {
        var role = Role.Create("  ops_admin ", "  Ops  ", " desc ", " blue ", RoleStatus.Active,
            Scope.Platform, null, ["txn.view", "txn.view", "  merchant.view  ", "  "], Catalog);

        Assert.Equal("ops_admin", role.Code);
        Assert.Equal("Ops", role.Name);
        Assert.Equal("desc", role.Description);
        Assert.Equal("blue", role.Color);
        Assert.Equal(RoleStatus.Active, role.Status);
        Assert.Equal(Scope.Platform, role.Scope);
        Assert.Null(role.MerchantId);
        Assert.Equal(new HashSet<string> { "txn.view", "merchant.view" }, role.PermissionKeys.ToHashSet());
    }

    [Fact]
    public void Create_rejects_an_unknown_permission_key() =>
        Assert.Throws<ArgumentException>(() =>
            Role.Create("r", "R", null, null, RoleStatus.Active, Scope.Platform, null, ["txn.view", "bogus.key"], Catalog));

    [Fact]
    public void Create_rejects_a_permission_key_from_the_wrong_side()
    {
        // payment.create is a Merchant-side key; granting it to a Platform-scope role is structurally impossible
        // (REQ-6.6) rather than merely undocumented.
        Assert.Throws<ArgumentException>(() =>
            Role.Create("r", "R", null, null, RoleStatus.Active, Scope.Platform, null, ["payment.create"], Catalog));
        Assert.Throws<ArgumentException>(() =>
            Role.Create("r", "R", null, null, RoleStatus.Active, Scope.Merchant, MerchantId, ["txn.view"], Catalog));
    }

    [Fact]
    public void Create_rejects_a_blank_or_overlong_code_and_a_blank_name()
    {
        Assert.Throws<ArgumentException>(() => Role.Create("   ", "R", null, null, RoleStatus.Active, Scope.Platform, null, [], Catalog));
        Assert.Throws<ArgumentException>(() => Role.Create(new string('x', 65), "R", null, null, RoleStatus.Active, Scope.Platform, null, [], Catalog));
        Assert.Throws<ArgumentException>(() => Role.Create("r", "  ", null, null, RoleStatus.Active, Scope.Platform, null, [], Catalog));
    }

    [Fact]
    public void Create_rejects_a_code_with_non_slug_characters()
    {
        Assert.Throws<ArgumentException>(() => Role.Create("ops admin", "R", null, null, RoleStatus.Active, Scope.Platform, null, [], Catalog));
        Assert.Throws<ArgumentException>(() => Role.Create("Ops_Admin", "R", null, null, RoleStatus.Active, Scope.Platform, null, [], Catalog));
        Assert.Throws<ArgumentException>(() => Role.Create("ops/admin", "R", null, null, RoleStatus.Active, Scope.Platform, null, [], Catalog));
    }

    // REQ-3.3: a Platform-scope role can never carry a MerchantId — the domain invariant the DB CHECK backstops.
    [Fact]
    public void Create_rejects_a_platform_role_with_a_merchant_id() =>
        Assert.Throws<ArgumentException>(() =>
            Role.Create("r", "R", null, null, RoleStatus.Active, Scope.Platform, MerchantId, [], Catalog));

    [Fact]
    public void Create_accepts_a_merchant_role_with_or_without_a_merchant_id()
    {
        var shared = Role.Create("shared", "Shared", null, null, RoleStatus.Active, Scope.Merchant, null, [], Catalog);
        Assert.Null(shared.MerchantId);

        var custom = Role.Create("custom", "Custom", null, null, RoleStatus.Active, Scope.Merchant, MerchantId, [], Catalog);
        Assert.Equal(MerchantId, custom.MerchantId);
    }

    [Fact]
    public void SetPermissions_replaces_the_set_and_rejects_unknown_or_wrong_side_keys()
    {
        var role = Role.Create("r", "R", null, null, RoleStatus.Active, Scope.Platform, null, ["txn.view"], Catalog);

        role.SetPermissions(["merchant.view", "merchant.view", "user.view"], Catalog);
        Assert.Equal(new HashSet<string> { "merchant.view", "user.view" }, role.PermissionKeys.ToHashSet());

        Assert.Throws<ArgumentException>(() => role.SetPermissions(["not.a.key"], Catalog));
        Assert.Throws<ArgumentException>(() => role.SetPermissions(["payment.create"], Catalog)); // Merchant-side key
    }

    [Theory]
    [InlineData(Role.PlatformAdminCode)]
    [InlineData(Role.MerchantManagerCode)]
    public void A_seed_anchor_cannot_be_deactivated_or_deleted_but_other_roles_can(string anchorCode)
    {
        var scope = anchorCode == Role.PlatformAdminCode ? Scope.Platform : Scope.Merchant;
        var anchor = Role.Create(anchorCode, "Anchor", null, null, RoleStatus.Active, scope, null, [], Catalog);
        Assert.True(anchor.IsSeedAnchor);
        Assert.Throws<InvalidOperationException>(anchor.Deactivate);
        Assert.Throws<InvalidOperationException>(anchor.EnsureDeletable);

        var other = Role.Create("ops", "Ops", null, null, RoleStatus.Active, Scope.Platform, null, [], Catalog);
        Assert.False(other.IsSeedAnchor);
        other.Deactivate();
        Assert.Equal(RoleStatus.Inactive, other.Status);
        other.EnsureDeletable(); // does not throw
    }

    [Theory]
    [InlineData(Role.PlatformAdminCode)]
    [InlineData(Role.MerchantManagerCode)]
    public void A_merchant_owned_role_reusing_an_anchor_code_is_not_a_seed_anchor(string anchorCode)
    {
        // The Platform seed is outside a merchant's visible set and the unique index buckets by MerchantId,
        // so a merchant CAN create a custom role with this code — it must stay deactivatable/deletable
        // (Codex P2: code-only anchor check made it permanently stuck behind the 409 guards).
        var custom = Role.Create(anchorCode, "Custom", null, null, RoleStatus.Active,
            Scope.Merchant, Guid.NewGuid(), [], Catalog);
        Assert.False(custom.IsSeedAnchor);
        custom.Deactivate();
        Assert.Equal(RoleStatus.Inactive, custom.Status);
        custom.EnsureDeletable(); // does not throw
    }

    [Fact]
    public void Activate_restores_an_inactive_role()
    {
        var role = Role.Create("r", "R", null, null, RoleStatus.Inactive, Scope.Platform, null, [], Catalog);
        Assert.Equal(RoleStatus.Inactive, role.Status);
        role.Activate();
        Assert.Equal(RoleStatus.Active, role.Status);
    }

    [Fact]
    public void Resource_version_starts_at_one_and_bumps_monotonically()
    {
        var role = Role.Create("r", "R", null, null, RoleStatus.Active, Scope.Platform, null, [], Catalog);

        Assert.Equal(1, role.Version);
        role.BumpVersion();
        Assert.Equal(2, role.Version);
    }
}
