using BuildingBlocks.Infrastructure.Persistence;
using Merchants.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Persistence.MerchantUser.Users;

// Runtime (scalar-only) mapping — mirrors Merchants.Infrastructure.Persistence.Users.SessionConfigurations.
// Control-plane: no merchant RLS predicate. The raw session token is NEVER stored — only its SHA-256 hash.

public sealed class SessionConfiguration : IEntityTypeConfiguration<Session>
{
    public void Configure(EntityTypeBuilder<Session> builder)
    {
        builder.ToTable("Sessions", SchemaNames.Merch);
        builder.HasKey(x => x.Id);
        builder.Property(x => x.FamilyId).IsRequired();
        builder.Property(x => x.TokenHash).IsRequired().HasMaxLength(32); // varbinary(32) — SHA-256 digest
        builder.Property(x => x.MerchantUserId).IsRequired();
        builder.Property(x => x.Status).HasConversion<int>().IsRequired();
        builder.Property(x => x.IssuedAt).IsRequired();
        builder.Property(x => x.IdleExpiresAt).IsRequired();
        builder.Property(x => x.AbsoluteExpiresAt).IsRequired();
        builder.Property(x => x.SupersededAt);
        builder.Property(x => x.SupersededBySessionId);
        builder.Property(x => x.CreatedIp).HasMaxLength(45);
        builder.Property(x => x.UserAgent).HasMaxLength(256);
        builder.HasIndex(x => x.TokenHash).IsUnique();        // O(1) lookup by hashed id (REQ-11.4)
        builder.HasIndex(x => x.FamilyId);                    // family-wide revoke (REQ-11.3)
        builder.HasIndex(x => x.MerchantUserId);               // logout-all + suspend propagation (REQ-12.2/12.3)
        builder.HasIndex(x => x.AbsoluteExpiresAt);           // prune sweep (REQ-10.4)
    }
}

public sealed class AuthAuditConfiguration : IEntityTypeConfiguration<AuthAudit>
{
    public void Configure(EntityTypeBuilder<AuthAudit> builder)
    {
        builder.ToTable("AuthAudits", SchemaNames.Merch);
        builder.HasKey(x => x.Id);
        builder.Property(x => x.EventType).HasMaxLength(32).IsRequired();
        builder.Property(x => x.MerchantUserId);          // null when no account was resolved (REQ-12.4)
        builder.Property(x => x.Subject).HasMaxLength(256);
        builder.Property(x => x.Reason).HasMaxLength(128);
        builder.Property(x => x.CorrelationId).HasMaxLength(128).IsRequired();
        builder.Property(x => x.OccurredAt).IsRequired();
        builder.HasIndex(x => x.MerchantUserId);
        AppendOnlyDescriptor.Mark(builder.Metadata); // rls-to-query-filter REQ-2.4-adjacent: append-only audit
    }
}
