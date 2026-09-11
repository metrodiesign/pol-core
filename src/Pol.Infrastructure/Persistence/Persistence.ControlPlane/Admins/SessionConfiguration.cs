using Admins.Domain.Users;
using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Persistence.ControlPlane.Admins;

// Runtime (scalar-only) mapping — mirrors Admins.Infrastructure.Persistence.Users.SessionConfigurations.

public sealed class SessionConfiguration : IEntityTypeConfiguration<Session>
{
    public void Configure(EntityTypeBuilder<Session> builder)
    {
        builder.ToTable("Sessions", SchemaNames.Admin);
        builder.HasKey(x => x.Id);
        builder.Property(x => x.FamilyId).IsRequired();
        builder.Property(x => x.TokenHash).IsRequired().HasMaxLength(32);
        builder.Property(x => x.AdminUserId).IsRequired();
        builder.Property(x => x.Status).HasConversion<int>().IsRequired();
        builder.Property(x => x.IssuedAt).IsRequired();
        builder.Property(x => x.IdleExpiresAt).IsRequired();
        builder.Property(x => x.AbsoluteExpiresAt).IsRequired();
        builder.Property(x => x.SupersededAt);
        builder.Property(x => x.SupersededBySessionId);
        builder.Property(x => x.IpAddress).HasMaxLength(45);
        builder.Property(x => x.UserAgent).HasMaxLength(256);
        builder.HasIndex(x => x.TokenHash).IsUnique();
        builder.HasIndex(x => x.FamilyId);
        builder.HasIndex(x => x.AdminUserId);
        builder.HasIndex(x => x.AbsoluteExpiresAt);
    }
}

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
