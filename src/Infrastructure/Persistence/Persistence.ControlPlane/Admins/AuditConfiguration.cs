using Admins.Domain.Users;
using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Persistence.ControlPlane.Admins;

// Runtime (scalar-only) mapping for the admin audit sink. The migration-owner (PolDbContext) keeps the
// canonical IEntityTypeConfiguration; this copy is what ControlPlaneDbContext (the runtime chokepoint)
// actually uses. admin.UserAudits stays live as the audit sink for the canonical /api/v1/accounts/* and
// role endpoints (the legacy admin identity plane was retired around it).
public sealed class AuditConfiguration : IEntityTypeConfiguration<Audit>
{
    public void Configure(EntityTypeBuilder<Audit> builder)
    {
        builder.ToTable("UserAudits", SchemaNames.Admin);
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Action).HasMaxLength(64).IsRequired();
        builder.Property(x => x.ActorType).HasMaxLength(16).IsRequired();
        builder.Property(x => x.ActorId).IsRequired();
        builder.Property(x => x.TargetAdminId);
        builder.Property(x => x.MerchantId);
        builder.Property(x => x.TargetRoleId);
        builder.Property(x => x.CorrelationId).HasMaxLength(128).IsRequired();
        builder.Property(x => x.OccurredAt).IsRequired();
        AppendOnlyDescriptor.Mark(builder.Metadata); // rls-to-query-filter REQ-2.4-adjacent: append-only admin audit
    }
}
