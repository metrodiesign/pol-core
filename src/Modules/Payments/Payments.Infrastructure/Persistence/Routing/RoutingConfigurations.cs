using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Payments.Domain.Psp;
using Payments.Domain.Routing;

namespace Payments.Infrastructure.Persistence.Routing;

public sealed class RoutingRulesetConfiguration : IEntityTypeConfiguration<RoutingRuleset>
{
    public void Configure(EntityTypeBuilder<RoutingRuleset> builder)
    {
        builder.ToTable("RoutingRulesets", SchemaNames.Txn);
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Name).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Status).HasConversion<int>().IsRequired();
        builder.Property(x => x.Version).IsConcurrencyToken().IsRequired();
        builder.HasIndex(x => new { x.MerchantId, x.Status })
            .IsUnique().HasFilter("[Status] = 3");
        builder.HasAlternateKey(x => new { x.MerchantId, x.Id });
        builder.HasMany(x => x.Rules).WithOne()
            .HasForeignKey(x => new { x.MerchantId, x.RulesetId })
            .HasPrincipalKey(x => new { x.MerchantId, x.Id }).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class RoutingRuleConfiguration : IEntityTypeConfiguration<RoutingRule>
{
    public void Configure(EntityTypeBuilder<RoutingRule> builder)
    {
        builder.ToTable("RoutingRules", SchemaNames.Txn);
        builder.HasKey(x => x.Id);
        // RoutingRuleset.Replace mints child ids before EF discovers the new graph. Mark them client-owned
        // so replacement rows are INSERTed instead of UPDATEd as presumed store-generated existing rows.
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.Method).HasMaxLength(30).IsRequired();
        builder.Property(x => x.MinAmount).HasPrecision(18, 2);
        builder.Property(x => x.MaxAmount).HasPrecision(18, 2);
        builder.HasIndex(x => new { x.MerchantId, x.RulesetId, x.Priority }).IsUnique();
        builder.HasOne<Connection>().WithMany()
            .HasForeignKey(x => new { x.MerchantId, x.TargetConnectionId })
            .HasPrincipalKey(x => new { x.MerchantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Connection>().WithMany()
            .HasForeignKey(x => new { x.MerchantId, x.FallbackConnectionId })
            .HasPrincipalKey(x => new { x.MerchantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}
