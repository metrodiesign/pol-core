using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Architecture.Tests;

/// <summary>
/// Proves <see cref="TenantKeyDescriptor"/> actually REJECTS a misconfigured tenant key (rls-to-query-filter
/// REQ-11.7, Codex round-2/round-3 findings) rather than silently accepting a typo, a shadow property, the
/// wrong CLR type, or an unintended nullable — the positive case (every real entity accepted) is already
/// proven by every runtime context building successfully in <see cref="ModelDisjointnessTests"/>.
/// </summary>
public sealed class TenantKeyDescriptorTests
{
    private sealed class Fixture
    {
        public Guid Id { get; set; }
        public Guid MerchantId { get; set; }
        public Guid? NullableMerchantId { get; set; }
        public string StringMerchantId { get; set; } = "";
    }

    // Mirrors real usage: every config calls builder.Property(x => x.Y) BEFORE TenantKeyDescriptor.Require —
    // without an explicit Property() call (or a real database provider's conventions), ModelBuilder does not
    // eagerly discover a bare CLR property, so FindProperty would report "not found" for reasons unrelated
    // to what this test is proving. Declare every property the fixture might be asked about up front.
    private static EntityTypeBuilder<Fixture> NewFixtureEntity()
    {
        var entity = new ModelBuilder().Entity<Fixture>();
        entity.Property(x => x.MerchantId);
        entity.Property(x => x.NullableMerchantId);
        entity.Property(x => x.StringMerchantId);
        return entity;
    }

    [Fact]
    public void Accepts_a_real_required_Guid_property()
    {
        var entity = NewFixtureEntity();
        TenantKeyDescriptor.Require(entity.Metadata, nameof(Fixture.MerchantId)); // must not throw
    }

    [Fact]
    public void Rejects_a_typo_or_missing_property()
    {
        var entity = NewFixtureEntity();
        var ex = Assert.Throws<InvalidOperationException>(() =>
            TenantKeyDescriptor.Require(entity.Metadata, "MerchantIdTypo"));
        Assert.Contains("not found", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rejects_a_nullable_Guid_unless_explicitly_allowed()
    {
        var entity = NewFixtureEntity();
        var ex = Assert.Throws<InvalidOperationException>(() =>
            TenantKeyDescriptor.Require(entity.Metadata, nameof(Fixture.NullableMerchantId)));
        Assert.Contains("nullable", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Allows_a_nullable_Guid_when_explicitly_opted_in_for_the_pending_carveout()
    {
        var entity = NewFixtureEntity();
        TenantKeyDescriptor.Require(entity.Metadata, nameof(Fixture.NullableMerchantId), allowNullable: true); // must not throw
    }

    [Fact]
    public void Rejects_the_wrong_CLR_type()
    {
        var entity = NewFixtureEntity();
        var ex = Assert.Throws<InvalidOperationException>(() =>
            TenantKeyDescriptor.Require(entity.Metadata, nameof(Fixture.StringMerchantId)));
        Assert.Contains("must be Guid", ex.Message);
    }

    [Fact]
    public void Rejects_a_shadow_property()
    {
        var builder = new ModelBuilder();
        var entity = builder.Entity<Fixture>();
        entity.Property<Guid>("ShadowMerchantId"); // no CLR property on Fixture backs this — shadow by definition

        var ex = Assert.Throws<InvalidOperationException>(() =>
            TenantKeyDescriptor.Require(entity.Metadata, "ShadowMerchantId"));
        Assert.Contains("shadow", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
