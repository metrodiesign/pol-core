using Admins.Domain.Users;
using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Persistence.ControlPlane.Admins;

// admin.AuthAudits is the append-only login audit of the retired admin session cookie. Nothing writes it since
// employee login moved to the platform token; the mapping stays so the history remains readable.
public sealed class AuthAuditConfiguration : IEntityTypeConfiguration<AuthAudit>
{
    public void Configure(EntityTypeBuilder<AuthAudit> builder)
    {
        builder.ToTable("AuthAudits", SchemaNames.Admin);
        builder.HasKey(x => x.Id);
        builder.Property(x => x.EventType).HasMaxLength(32).IsRequired();
        builder.Property(x => x.AdminUserId);
        builder.Property(x => x.Subject).HasMaxLength(256);
        builder.Property(x => x.Reason).HasMaxLength(128);
        builder.Property(x => x.CorrelationId).HasMaxLength(128).IsRequired();
        builder.Property(x => x.OccurredAt).IsRequired();
        builder.HasIndex(x => x.AdminUserId);
        AppendOnlyDescriptor.Mark(builder.Metadata); // rls-to-query-filter REQ-2.4-adjacent: append-only admin audit
    }
}
