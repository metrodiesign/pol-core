using Merchants.Domain;

namespace Merchants.Tests;

/// <summary>The <see cref="MerchantUserRoleDefinition"/> aggregate rules (REQ-16) and the <see cref="MerchantUserPermissions"/>
/// catalog vocabulary (REQ-15): grants are validated against the catalog, the <c>merchant_owner</c> anchor is
/// undeletable/undeactivatable, ordinary roles are not, and the code vocabulary is internally consistent.</summary>
public sealed class MerchantUserRoleTests
{
    private static readonly IReadOnlySet<string> Catalog = MerchantUserPermissions.AllKeys;

    private static MerchantUserRoleDefinition NewRole(string code, params string[] keys) =>
        MerchantUserRoleDefinition.Create(code, code, null, null, MerchantUserRoleStatus.Active, keys, Catalog);

    [Fact]
    public void Create_keeps_only_the_granted_subset()
    {
        var role = NewRole("editor", MerchantUserPermissions.ProductCreate, MerchantUserPermissions.ProductUpdate);

        Assert.Equal(
            new[] { MerchantUserPermissions.ProductCreate, MerchantUserPermissions.ProductUpdate }.OrderBy(x => x),
            role.PermissionKeys.OrderBy(x => x));
    }

    [Fact]
    public void Create_rejects_a_permission_outside_the_catalog()
    {
        var ex = Assert.Throws<ArgumentException>(() => NewRole("editor", "bogus.key"));
        Assert.Contains("bogus.key", ex.Message);
    }

    [Theory]
    [InlineData("Bad Code")]
    [InlineData("UPPER")]
    [InlineData("has/slash")]
    [InlineData("")]
    public void Create_rejects_a_code_outside_the_slug_pattern(string code) =>
        Assert.ThrowsAny<ArgumentException>(() =>
            MerchantUserRoleDefinition.Create(code, "Name", null, null, MerchantUserRoleStatus.Active, [], Catalog));

    [Fact]
    public void SetPermissions_replaces_and_dedupes()
    {
        var role = NewRole("editor", MerchantUserPermissions.ProductCreate);

        role.SetPermissions(
            [MerchantUserPermissions.PaymentCreate, MerchantUserPermissions.PaymentCreate, MerchantUserPermissions.PaymentRedirect],
            Catalog);

        Assert.Equal(
            new[] { MerchantUserPermissions.PaymentCreate, MerchantUserPermissions.PaymentRedirect }.OrderBy(x => x),
            role.PermissionKeys.OrderBy(x => x));
    }

    [Fact]
    public void Merchant_owner_anchor_cannot_be_deactivated_or_deleted()
    {
        var owner = NewRole(MerchantUserRoleDefinition.MerchantOwnerCode, [.. Catalog]);

        Assert.True(owner.IsMerchantOwnerSeed);
        Assert.Throws<InvalidOperationException>(() => owner.Deactivate());
        Assert.Throws<InvalidOperationException>(() => owner.EnsureDeletable());
        Assert.Equal(MerchantUserRoleStatus.Active, owner.Status); // Deactivate threw -> unchanged
    }

    [Fact]
    public void An_ordinary_role_can_be_deactivated_and_deleted()
    {
        var member = NewRole(MerchantUserRoleDefinition.MerchantMemberCode, MerchantUserPermissions.ProductCreate);

        member.EnsureDeletable();          // does not throw
        member.Deactivate();
        Assert.Equal(MerchantUserRoleStatus.Inactive, member.Status);
    }

    [Fact]
    public void Catalog_vocabulary_is_internally_consistent()
    {
        // The seven seeded keys, each mapped to a real group — the in-memory half of the code<->DB parity guard
        // (the DB half lives in the RBAC grants integration suite).
        Assert.Equal(7, MerchantUserPermissions.AllKeys.Count);
        Assert.Equal(MerchantUserPermissions.All.Count, MerchantUserPermissions.AllKeys.Count); // no duplicate keys
        Assert.All(MerchantUserPermissions.All, p => Assert.Contains(p.GroupKey, MerchantUserPermissions.GroupKeys));
    }
}
