using Admins.Domain.Users;
using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Admins.Infrastructure.Persistence.Users;

// EF mappings for the admin BFF session tables onto the admin schema (discovered via HostModuleAssemblies.All).
// Control-plane: NO merchant RLS predicate, granted to pol_admin only (see AddPlatformUserSessionTables). The raw session
// token is NEVER stored — only its SHA-256 hash (REQ-11.2).

public sealed class SessionConfiguration : IEntityTypeConfiguration<Session>
{
    public void Configure(EntityTypeBuilder<Session> builder)
    {
        builder.ToTable("Sessions", SchemaNames.Admin);
        builder.HasKey(x => x.Id);
        builder.Property(x => x.FamilyId).IsRequired();
        builder.Property(x => x.TokenHash).IsRequired().HasMaxLength(32); // varbinary(32) — SHA-256 digest
        builder.Property(x => x.AdminUserId).IsRequired();
        builder.Property(x => x.Status).HasConversion<int>().IsRequired();
        builder.Property(x => x.IssuedAt).IsRequired();
        builder.Property(x => x.IdleExpiresAt).IsRequired();
        builder.Property(x => x.AbsoluteExpiresAt).IsRequired();
        builder.Property(x => x.SupersededAt);
        builder.Property(x => x.SupersededBySessionId);
        builder.Property(x => x.IpAddress).HasMaxLength(45);
        builder.Property(x => x.UserAgent).HasMaxLength(256);
        builder.HasIndex(x => x.TokenHash).IsUnique();       // O(1) lookup by hashed id (REQ-11.4)
        builder.HasIndex(x => x.FamilyId);                   // family-wide revoke (REQ-11.4)
        builder.HasIndex(x => x.AdminUserId);             // logout-all (REQ-6.2)
        builder.HasIndex(x => x.AbsoluteExpiresAt);          // prune sweep (REQ-11.5)
    }
}

public sealed class AuthAuditConfiguration : IEntityTypeConfiguration<AuthAudit>
{
    public void Configure(EntityTypeBuilder<AuthAudit> builder)
    {
        builder.ToTable("AuthAudits", SchemaNames.Admin);
        builder.HasKey(x => x.Id);
        builder.Property(x => x.EventType).HasMaxLength(32).IsRequired();
        builder.Property(x => x.AdminUserId);             // null when no admin was resolved (REQ-12.4)
        builder.Property(x => x.Subject).HasMaxLength(256);
        builder.Property(x => x.Reason).HasMaxLength(128);
        builder.Property(x => x.CorrelationId).HasMaxLength(128).IsRequired();
        builder.Property(x => x.OccurredAt).IsRequired();
        builder.HasIndex(x => x.AdminUserId);
    }
}
