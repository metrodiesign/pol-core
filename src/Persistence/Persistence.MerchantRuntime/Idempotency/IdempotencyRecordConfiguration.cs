using BuildingBlocks.Infrastructure.Idempotency;
using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Persistence.MerchantRuntime.Idempotency;

// Filtered mapping — mirrors BuildingBlocks.Infrastructure.Idempotency.IdempotencyRecordConfiguration
// exactly for column/index shape, but is a FRESH class (see Outbox/OutboxMessageConfiguration.cs for why).

internal sealed class IdempotencyRecordConfiguration(MerchantRuntimeDbContext context) : IEntityTypeConfiguration<IdempotencyRecord>
{
    public void Configure(EntityTypeBuilder<IdempotencyRecord> builder)
    {
        builder.ToTable("IdempotencyRecords", SchemaNames.Txn);
        builder.HasKey(x => x.Key);
        builder.Property(x => x.MerchantId).IsRequired();
        builder.Property(x => x.Key).HasMaxLength(400);
        builder.Property(x => x.Context).HasMaxLength(256).IsRequired();

        TenantKeyDescriptor.Require(builder.Metadata, nameof(IdempotencyRecord.MerchantId));
        builder.HasQueryFilter(x => x.MerchantId == context.CurrentMerchant);
    }
}
