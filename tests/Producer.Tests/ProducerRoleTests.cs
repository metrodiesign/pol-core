using Producer.Domain;

namespace Producer.Tests;

/// <summary>The <see cref="ProducerRole"/> aggregate rules (REQ-16) and the <see cref="ProducerPermissions"/>
/// catalog vocabulary (REQ-15): grants are validated against the catalog, the <c>tenant_owner</c> anchor is
/// undeletable/undeactivatable, ordinary roles are not, and the code vocabulary is internally consistent.</summary>
public sealed class ProducerRoleTests
{
    private static readonly IReadOnlySet<string> Catalog = ProducerPermissions.AllKeys;

    private static ProducerRole NewRole(string code, params string[] keys) =>
        ProducerRole.Create(code, code, null, null, ProducerRoleStatus.Active, keys, Catalog);

    [Fact]
    public void Create_keeps_only_the_granted_subset()
    {
        var role = NewRole("editor", ProducerPermissions.ProductCreate, ProducerPermissions.ProductUpdate);

        Assert.Equal(
            new[] { ProducerPermissions.ProductCreate, ProducerPermissions.ProductUpdate }.OrderBy(x => x),
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
            ProducerRole.Create(code, "Name", null, null, ProducerRoleStatus.Active, [], Catalog));

    [Fact]
    public void SetPermissions_replaces_and_dedupes()
    {
        var role = NewRole("editor", ProducerPermissions.ProductCreate);

        role.SetPermissions(
            [ProducerPermissions.PaymentCreate, ProducerPermissions.PaymentCreate, ProducerPermissions.PaymentRedirect],
            Catalog);

        Assert.Equal(
            new[] { ProducerPermissions.PaymentCreate, ProducerPermissions.PaymentRedirect }.OrderBy(x => x),
            role.PermissionKeys.OrderBy(x => x));
    }

    [Fact]
    public void Tenant_owner_anchor_cannot_be_deactivated_or_deleted()
    {
        var owner = NewRole(ProducerRole.TenantOwnerCode, [.. Catalog]);

        Assert.True(owner.IsTenantOwnerSeed);
        Assert.Throws<InvalidOperationException>(() => owner.Deactivate());
        Assert.Throws<InvalidOperationException>(() => owner.EnsureDeletable());
        Assert.Equal(ProducerRoleStatus.Active, owner.Status); // Deactivate threw -> unchanged
    }

    [Fact]
    public void An_ordinary_role_can_be_deactivated_and_deleted()
    {
        var member = NewRole(ProducerRole.TenantMemberCode, ProducerPermissions.ProductCreate);

        member.EnsureDeletable();          // does not throw
        member.Deactivate();
        Assert.Equal(ProducerRoleStatus.Inactive, member.Status);
    }

    [Fact]
    public void Catalog_vocabulary_is_internally_consistent()
    {
        // The seven seeded keys, each mapped to a real group — the in-memory half of the code<->DB parity guard
        // (the DB half lives in ProducerRoleRbacGrantsTests).
        Assert.Equal(7, ProducerPermissions.AllKeys.Count);
        Assert.Equal(ProducerPermissions.All.Count, ProducerPermissions.AllKeys.Count); // no duplicate keys
        Assert.All(ProducerPermissions.All, p => Assert.Contains(p.GroupKey, ProducerPermissions.GroupKeys));
    }
}
