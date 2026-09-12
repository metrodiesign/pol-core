using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Orders.Domain.Items;

namespace Orders.Infrastructure.Items;

/// <summary>
/// Migration-owner mapping for <see cref="RevealAudit"/> — mirrors <c>VaultRevealAuditConfiguration</c>'s
/// migration-owner shape (columns/indexes only; the tenant-key/query-filter/append-only wiring lives in the
/// runtime twin, <c>Persistence.MerchantRuntime.Orders.Items.RevealAuditConfiguration</c>, per the dual-config
/// pattern this codebase already uses everywhere else).
/// </summary>
public sealed class RevealAuditConfiguration : IEntityTypeConfiguration<RevealAudit>
{
    public void Configure(EntityTypeBuilder<RevealAudit> builder)
    {
        builder.ToTable("OrderItemRevealAudits", SchemaNames.Shop);
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();

        builder.Property(x => x.OrderItemId).IsRequired();
        builder.Property(x => x.MerchantId).IsRequired();
        builder.Property(x => x.ActorType).HasMaxLength(32).IsRequired();
        builder.Property(x => x.ActorId).HasMaxLength(200).IsRequired();
        builder.Property(x => x.CorrelationId).HasMaxLength(200).IsRequired();
        builder.Property(x => x.RevealedAt).IsRequired();

        builder.HasIndex(x => x.OrderItemId);
        builder.HasIndex(x => new { x.MerchantId, x.RevealedAt });
    }
}
