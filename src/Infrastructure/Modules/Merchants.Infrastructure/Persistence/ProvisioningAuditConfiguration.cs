using BuildingBlocks.Infrastructure.Persistence;
using Merchants.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Merchants.Infrastructure.Persistence;

/// <summary>
/// Maps <see cref="ProvisioningAudit"/> onto the merch schema. Control-plane append-only log of
/// pol_admin bypass provisioning; not under the merchant RLS predicate (it has no merchant-scoped read path).
/// </summary>
public sealed class ProvisioningAuditConfiguration : IEntityTypeConfiguration<ProvisioningAudit>
{
    public void Configure(EntityTypeBuilder<ProvisioningAudit> builder)
    {
        builder.ToTable("ProvisioningAudits", SchemaNames.Merch);
        builder.HasKey(x => x.Id);

        builder.Property(x => x.MerchantId).IsRequired();
        builder.Property(x => x.MerchantCode).HasMaxLength(64).IsRequired();
        builder.Property(x => x.AdminSubject).HasMaxLength(256).IsRequired();
        builder.Property(x => x.CorrelationId).HasMaxLength(128).IsRequired();
        builder.Property(x => x.OccurredAt).IsRequired();
    }
}
