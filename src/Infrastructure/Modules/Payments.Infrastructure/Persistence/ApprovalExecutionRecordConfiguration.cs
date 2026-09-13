using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Payments.Domain;

namespace Payments.Infrastructure.Persistence;

public sealed class ApprovalExecutionRecordConfiguration : IEntityTypeConfiguration<ApprovalExecutionRecord>
{
    public void Configure(EntityTypeBuilder<ApprovalExecutionRecord> builder)
    {
        builder.ToTable("ApprovalExecutionRecords", SchemaNames.Txn);
        builder.HasKey(x => x.EventId);
        builder.Property(x => x.EventId).ValueGeneratedNever();
        builder.Property(x => x.MerchantId).IsRequired();
        builder.Property(x => x.TargetType).HasMaxLength(64).IsRequired();
        builder.Property(x => x.TargetId).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Decision).HasMaxLength(16).IsRequired();
        builder.Property(x => x.State).HasConversion<int>().IsRequired();
        builder.Property(x => x.Outcome).HasMaxLength(120);
        builder.HasIndex(x => x.ApprovalId).IsUnique();
        builder.HasIndex(x => new { x.MerchantId, x.CreatedAt });
    }
}
