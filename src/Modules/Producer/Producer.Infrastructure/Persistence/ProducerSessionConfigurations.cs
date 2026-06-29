using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Producer.Domain;

namespace Producer.Infrastructure.Persistence;

// EF mappings for the producer BFF session tables onto the producer schema (discovered via ModuleAssemblies.Producer).
// Control-plane: NO tenant RLS predicate, granted to pol_admin only (see AddProducerSessionTables). The raw session
// token is NEVER stored — only its SHA-256 hash (REQ-10.1).
// ponytail: DUPLICATE of Admin.Infrastructure.Persistence.AdminSession/AuthAudit configs — deliberate debt, do not refactor into a shared base.

public sealed class ProducerSessionConfiguration : IEntityTypeConfiguration<ProducerSession>
{
    public void Configure(EntityTypeBuilder<ProducerSession> builder)
    {
        builder.ToTable("ProducerSessions");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.FamilyId).IsRequired();
        builder.Property(x => x.TokenHash).IsRequired().HasMaxLength(32); // varbinary(32) — SHA-256 digest
        builder.Property(x => x.TenantUserId).IsRequired();
        builder.Property(x => x.Status).HasConversion<int>().IsRequired();
        builder.Property(x => x.IssuedAt).IsRequired();
        builder.Property(x => x.IdleExpiresAt).IsRequired();
        builder.Property(x => x.AbsoluteExpiresAt).IsRequired();
        builder.Property(x => x.SupersededAt);
        builder.Property(x => x.SupersededBySessionId);
        builder.Property(x => x.CreatedIp).HasMaxLength(45);
        builder.Property(x => x.UserAgent).HasMaxLength(256);
        builder.HasIndex(x => x.TokenHash).IsUnique();       // O(1) lookup by hashed id (REQ-11.4)
        builder.HasIndex(x => x.FamilyId);                   // family-wide revoke (REQ-11.3)
        builder.HasIndex(x => x.TenantUserId);               // logout-all + suspend propagation (REQ-12.2/12.3)
        builder.HasIndex(x => x.AbsoluteExpiresAt);          // prune sweep (REQ-10.4)
    }
}

public sealed class ProducerAuthAuditConfiguration : IEntityTypeConfiguration<ProducerAuthAudit>
{
    public void Configure(EntityTypeBuilder<ProducerAuthAudit> builder)
    {
        builder.ToTable("ProducerAuthAudits");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.EventType).HasMaxLength(32).IsRequired();
        builder.Property(x => x.TenantUserId);               // null when no user was resolved (REQ-12.4)
        builder.Property(x => x.Subject).HasMaxLength(256);
        builder.Property(x => x.Reason).HasMaxLength(128);
        builder.Property(x => x.CorrelationId).HasMaxLength(128).IsRequired();
        builder.Property(x => x.OccurredAt).IsRequired();
        builder.HasIndex(x => x.TenantUserId);
    }
}
