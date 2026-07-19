using BuildingBlocks.Infrastructure.Persistence;
using Merchants.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Persistence.MerchantRuntime.Merchants;

// Runtime (scalar-only) mapping — mirrors
// Merchants.Infrastructure.Persistence.ProvisioningAuditConfiguration exactly for column/index shape.

internal sealed class ProvisioningAuditConfiguration(MerchantRuntimeDbContext context) : IEntityTypeConfiguration<ProvisioningAudit>
{
    public void Configure(EntityTypeBuilder<ProvisioningAudit> builder)
    {
        builder.ToTable("ProvisioningAudits", SchemaNames.Merch);
        builder.HasKey(x => x.Id);

        builder.Property(x => x.MerchantId).IsRequired();

        TenantKeyDescriptor.Require(builder.Metadata, nameof(ProvisioningAudit.MerchantId));
        builder.HasQueryFilter(x => x.MerchantId == context.CurrentMerchant);
        AppendOnlyDescriptor.Mark(builder.Metadata); // rls-to-query-filter REQ-2.4: append-only
        builder.Property(x => x.MerchantCode).HasMaxLength(64).IsRequired();
        builder.Property(x => x.AdminSubject).HasMaxLength(256).IsRequired();
        builder.Property(x => x.CorrelationId).HasMaxLength(128).IsRequired();
        builder.Property(x => x.OccurredAt).IsRequired();
    }
}
