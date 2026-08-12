using BuildingBlocks.Infrastructure.Idempotency;
using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Persistence.MerchantRuntime.Idempotency;

internal sealed class AdminOperationRecordConfiguration(MerchantRuntimeDbContext context)
    : IEntityTypeConfiguration<AdminOperationRecord>
{
    public void Configure(EntityTypeBuilder<AdminOperationRecord> builder)
    {
        builder.ToTable("AdminOperationRecords", SchemaNames.Txn);
        builder.HasKey(x => x.Id);
        TenantKeyDescriptor.Require(builder.Metadata, nameof(AdminOperationRecord.MerchantId));
        builder.HasQueryFilter(x => x.MerchantId == context.CurrentMerchant);
        builder.Property(x => x.Operation).HasMaxLength(120).IsRequired();
        builder.Property(x => x.IdempotencyKey).HasMaxLength(200).IsRequired();
        builder.Property(x => x.IntentHash).HasMaxLength(64).IsRequired();
        builder.Property(x => x.State).HasConversion<int>().IsRequired();
        builder.Property(x => x.Result).HasMaxLength(16_384);
        builder.Property(x => x.ResourceId).HasMaxLength(200);
        builder.HasIndex(x => new { x.MerchantId, x.ActorId, x.Operation, x.IdempotencyKey }).IsUnique();
        builder.HasIndex(x => x.ExpiresAt);
    }
}
