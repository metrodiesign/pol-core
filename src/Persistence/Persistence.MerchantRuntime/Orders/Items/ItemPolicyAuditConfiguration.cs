using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Orders.Domain.Items;

namespace Persistence.MerchantRuntime.Orders.Items;

// Runtime mapping — mirrors Orders.Infrastructure.Items.ItemPolicyAuditConfiguration exactly for column/index
// shape, plus the tenant-key/query-filter/append-only wiring (mirrors the OrderItemRevealAudit runtime twin):
// AppendOnlyDescriptor.Mark makes an UPDATE/DELETE against this table impossible through
// GuardedRuntimeDbContext regardless of what IWriteAuthorizer would otherwise allow.

internal sealed class ItemPolicyAuditConfiguration(MerchantRuntimeDbContext context)
    : IEntityTypeConfiguration<ItemPolicyAudit>
{
    public void Configure(EntityTypeBuilder<ItemPolicyAudit> builder)
    {
        builder.ToTable("OrderItemPolicyAudits", SchemaNames.Shop);
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();

        builder.Property(x => x.OrderItemId).IsRequired();
        builder.Property(x => x.MerchantId).IsRequired();
        builder.Property(x => x.ActorId).HasMaxLength(200).IsRequired();
        builder.Property(x => x.ActorKind).IsRequired();
        builder.Property(x => x.Operation).IsRequired();
        builder.Property(x => x.ChangeSummary).HasMaxLength(500).IsRequired();
        builder.Property(x => x.CorrelationId).HasMaxLength(200).IsRequired();
        builder.Property(x => x.OccurredAt).IsRequired();

        builder.HasIndex(x => x.OrderItemId);
        builder.HasIndex(x => new { x.MerchantId, x.OccurredAt });

        TenantKeyDescriptor.Require(builder.Metadata, nameof(ItemPolicyAudit.MerchantId));
        builder.HasQueryFilter(x => x.MerchantId == context.CurrentMerchant);
        AppendOnlyDescriptor.Mark(builder.Metadata);
    }
}
