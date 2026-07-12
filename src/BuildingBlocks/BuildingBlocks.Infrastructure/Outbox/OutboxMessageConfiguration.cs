using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BuildingBlocks.Infrastructure.Outbox;

public sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
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

        // Dispatcher hot path: unprocessed rows ordered by arrival.
        builder.HasIndex(x => new { x.ProcessedAt, x.LeaseExpiresAt });
    }
}
