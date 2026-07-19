using BuildingBlocks.Infrastructure.Outbox;
using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Persistence.MerchantRuntime.Outbox;

// Filtered mapping — mirrors BuildingBlocks.Infrastructure.Outbox.OutboxMessageConfiguration exactly for
// column/index shape, but is a FRESH class (not a reuse of the migration-owner's config): the read filter
// (rls-to-query-filter REQ-1.6, fail-closed) needs THIS context's CurrentMerchant instance member, which
// the migration-owner's shared config (used unfiltered by PolDbContext) cannot reference.

internal sealed class OutboxMessageConfiguration(MerchantRuntimeDbContext context) : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("OutboxMessages", SchemaNames.Txn);
        builder.HasKey(x => x.Id);
        builder.Property(x => x.MerchantId).IsRequired();
        builder.Property(x => x.Type).HasMaxLength(256).IsRequired();
        builder.Property(x => x.Payload).IsRequired();
        builder.Property(x => x.LeaseOwner).HasMaxLength(256);
        builder.Property(x => x.Error).HasMaxLength(2048);

        builder.HasIndex(x => new { x.ProcessedAt, x.LeaseExpiresAt });

        TenantKeyDescriptor.Require(builder.Metadata, nameof(OutboxMessage.MerchantId));
        builder.HasQueryFilter(x => x.MerchantId == context.CurrentMerchant);
    }
}
