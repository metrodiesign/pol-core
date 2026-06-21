using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BuildingBlocks.Infrastructure.Idempotency;

public sealed class IdempotencyRecordConfiguration : IEntityTypeConfiguration<IdempotencyRecord>
{
    public void Configure(EntityTypeBuilder<IdempotencyRecord> builder)
    {
        builder.ToTable("IdempotencyRecords");
        builder.HasKey(x => x.Key);
        builder.Property(x => x.TenantId).IsRequired();
        builder.Property(x => x.Key).HasMaxLength(400);
        builder.Property(x => x.Context).HasMaxLength(256).IsRequired();
    }
}
