using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Orders.Domain.Items;

namespace Persistence.MerchantRuntime.Orders.Items;

// Runtime mapping — mirrors Orders.Infrastructure.Items.RevealAuditConfiguration exactly for column/index
// shape, plus the tenant-key/query-filter/append-only wiring (mirrors VaultRevealAuditConfiguration's own
// runtime twin exactly): AppendOnlyDescriptor.Mark makes an UPDATE/DELETE against this table impossible
// through GuardedRuntimeDbContext regardless of what IWriteAuthorizer would otherwise allow.

internal sealed class RevealAuditConfiguration(MerchantRuntimeDbContext context) : IEntityTypeConfiguration<RevealAudit>
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

        TenantKeyDescriptor.Require(builder.Metadata, nameof(RevealAudit.MerchantId));
        builder.HasQueryFilter(x => x.MerchantId == context.CurrentMerchant);
        AppendOnlyDescriptor.Mark(builder.Metadata);
    }
}
