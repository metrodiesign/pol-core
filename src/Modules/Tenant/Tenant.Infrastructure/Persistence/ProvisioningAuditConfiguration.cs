using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Tenant.Domain;

namespace Tenant.Infrastructure.Persistence;

/// <summary>
/// Maps <see cref="ProvisioningAudit"/> onto the producer schema. Control-plane append-only log of
/// pol_admin bypass provisioning; not under the tenant RLS predicate (it has no tenant-scoped read path).
/// </summary>
public sealed class ProvisioningAuditConfiguration : IEntityTypeConfiguration<ProvisioningAudit>
{
    public void Configure(EntityTypeBuilder<ProvisioningAudit> builder)
    {
        builder.ToTable("ProvisioningAudits");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.TenantId).IsRequired();
        builder.Property(x => x.TenantCode).HasMaxLength(64).IsRequired();
        builder.Property(x => x.AdminSubject).HasMaxLength(256).IsRequired();
        builder.Property(x => x.CorrelationId).HasMaxLength(128).IsRequired();
        builder.Property(x => x.OccurredAtUtc).IsRequired();
    }
}
