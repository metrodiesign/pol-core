using BuildingBlocks.Infrastructure.Outbox;
using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Persistence.MerchantUsers.Outbox;

// Mirrors BuildingBlocks.Infrastructure.Outbox.OutboxMessageConfiguration exactly for column/index shape,
// mapped onto the NEW merch.UserOutbox table (task 8's migration creates it + atomically moves the legacy
// registration-sentinel rows in from txn.OutboxMessages). Filtered per REQ-1.6 (fail-closed, same as every
// other MerchantUserDbContext entity) — the drain port suppresses this explicitly per owner.

internal sealed class MerchantUserOutboxConfiguration(MerchantUserDbContext context) : IEntityTypeConfiguration<MerchantUserOutbox>
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

        TenantKeyDescriptor.Require(builder.Metadata, nameof(MerchantUserOutbox.MerchantId));
        builder.HasQueryFilter(x => x.MerchantId == context.CurrentMerchant);

        // Dispatcher hot path: unprocessed rows ordered by arrival (mirrors OutboxMessageConfiguration).
        builder.HasIndex(x => new { x.ProcessedAt, x.LeaseExpiresAt });
    }
}
