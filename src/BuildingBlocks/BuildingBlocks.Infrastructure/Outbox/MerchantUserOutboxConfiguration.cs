using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BuildingBlocks.Infrastructure.Outbox;

// Migration-owner mapping — mirrors Persistence.MerchantUser.Outbox.MerchantUserOutboxConfiguration
// exactly for column/index shape. PolDbContext is scalar-only (no CurrentMerchant to filter by), same
// pattern as OutboxMessageConfiguration alongside it.

public sealed class MerchantUserOutboxConfiguration : IEntityTypeConfiguration<MerchantUserOutbox>
{
    public void Configure(EntityTypeBuilder<MerchantUserOutbox> builder)
    {
        builder.ToTable("UserOutbox", SchemaNames.Merch);
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
