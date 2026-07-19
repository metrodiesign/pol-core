using BuildingBlocks.Infrastructure.Persistence;
using Merchants.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Persistence.MerchantRuntime.Merchants;

// Runtime (scalar-only) mapping — mirrors Merchants.Infrastructure.Persistence.MerchantConfiguration
// exactly for column/index shape. Only the root Merchant aggregate — NOT anything under
// Merchants.Domain.Users (that's the sibling MerchantUser cluster).

internal sealed class MerchantConfiguration(MerchantRuntimeDbContext context) : IEntityTypeConfiguration<Merchant>
{
    public void Configure(EntityTypeBuilder<Merchant> builder)
    {
        builder.ToTable("Merchants", SchemaNames.Merch);
        builder.HasKey(x => x.Id);

        // Self-row filter (rls-to-query-filter REQ-1.2/2.7): Merchant has no separate MerchantId column —
        // its own Id IS the tenant key (REQ-2.6/2.9: also the write floor's concurrency token + immutable
        // + Empty-reject target for this entity).
        TenantKeyDescriptor.Require(builder.Metadata, nameof(Merchant.Id));
        builder.HasQueryFilter(x => x.Id == context.CurrentMerchant);

        builder.Property(x => x.Code).HasMaxLength(64).IsRequired();
        builder.Property(x => x.DisplayName).HasMaxLength(200).IsRequired();
        builder.Property(x => x.LegalEntityId).HasMaxLength(64).IsRequired();
        builder.Property(x => x.Status).HasConversion<int>().IsRequired();
        builder.Property(x => x.Country).HasMaxLength(2).IsRequired();
        builder.Property(x => x.Currency).HasMaxLength(3).IsRequired();
        builder.Property(x => x.EnabledChannels).HasMaxLength(256).IsRequired();
        // No explicit HasColumnType — see Payments.Psp.ConnectionConfiguration for why (Sqlite-portable,
        // identical real DDL under SQL Server).
        builder.Property(x => x.Metadata).IsRequired();
        builder.Property(x => x.CreatedAt).IsRequired();

        builder.HasIndex(x => x.Code).IsUnique();
    }
}
