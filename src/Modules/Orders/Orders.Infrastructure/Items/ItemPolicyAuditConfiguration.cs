using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Orders.Domain.Items;

namespace Orders.Infrastructure.Items;

/// <summary>
/// Migration-owner mapping for <see cref="ItemPolicyAudit"/> — mirrors <see cref="RevealAuditConfiguration"/>'s
/// migration-owner shape (columns/indexes only; the tenant-key/query-filter/append-only wiring lives in the
/// runtime twin, <c>Persistence.MerchantRuntime.Orders.Items.ItemPolicyAuditConfiguration</c>).
/// </summary>
public sealed class ItemPolicyAuditConfiguration : IEntityTypeConfiguration<ItemPolicyAudit>
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
    }
}
