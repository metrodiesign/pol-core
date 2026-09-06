using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Payments.Domain;

namespace Persistence.MerchantRuntime.Payments;

internal sealed class ApprovalExecutionRecordConfiguration(MerchantRuntimeDbContext context)
    : IEntityTypeConfiguration<ApprovalExecutionRecord>
{
    public void Configure(EntityTypeBuilder<ApprovalExecutionRecord> builder)
    {
        builder.ToTable("ApprovalExecutionRecords", SchemaNames.Txn);
        builder.HasKey(x => x.EventId);
        builder.Property(x => x.EventId).ValueGeneratedNever();
        TenantKeyDescriptor.Require(builder.Metadata, nameof(ApprovalExecutionRecord.MerchantId));
        builder.HasQueryFilter(x => x.MerchantId == context.CurrentMerchant);
        builder.Property(x => x.TargetType).HasMaxLength(64).IsRequired();
        builder.Property(x => x.TargetId).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Decision).HasMaxLength(16).IsRequired();
        builder.Property(x => x.State).HasConversion<int>().IsRequired();
        builder.Property(x => x.Outcome).HasMaxLength(120);
        builder.HasIndex(x => x.ApprovalId).IsUnique();
        builder.HasIndex(x => new { x.MerchantId, x.CreatedAt });
    }
}
