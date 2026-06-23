using Identity.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Identity.Infrastructure.Persistence;

// EF mappings for the TenantUser realm onto the producer schema, discovered at model-build time via
// ModuleAssemblies.Producer. TenantId is a plain Guid? (NO DB FK to Tenants — Identity does not reference
// the Tenant module; existence/active is validated at approval, and the RLS predicate scopes on it).

public sealed class TenantUserConfiguration : IEntityTypeConfiguration<TenantUser>
{
    public void Configure(EntityTypeBuilder<TenantUser> builder)
    {
        builder.ToTable("TenantUsers");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Subject).HasMaxLength(256).IsRequired();
        builder.Property(x => x.Email).HasMaxLength(320).IsRequired();
        builder.Property(x => x.TenantId); // nullable until approved
        builder.Property(x => x.Role).HasConversion<int?>();
        builder.Property(x => x.Status).HasConversion<int>().IsRequired();
        builder.Property(x => x.CreatedAtUtc).IsRequired();
        builder.HasIndex(x => x.Subject).IsUnique();
    }
}

public sealed class ExternalLoginConfiguration : IEntityTypeConfiguration<ExternalLogin>
{
    public void Configure(EntityTypeBuilder<ExternalLogin> builder)
    {
        builder.ToTable("ExternalLogins");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Provider).HasMaxLength(32).IsRequired();
        builder.Property(x => x.Subject).HasMaxLength(256).IsRequired();
        builder.Property(x => x.TenantUserId).IsRequired();
        builder.HasIndex(x => new { x.Provider, x.Subject }).IsUnique();
    }
}

public sealed class TenantUserProfileConfiguration : IEntityTypeConfiguration<TenantUserProfile>
{
    public void Configure(EntityTypeBuilder<TenantUserProfile> builder)
    {
        builder.ToTable("TenantUserProfiles");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.TenantUserId).IsRequired();
        builder.Property(x => x.DisplayName).HasMaxLength(200).IsRequired();
        builder.HasIndex(x => x.TenantUserId).IsUnique();
    }
}

public sealed class RegistrationTicketConfiguration : IEntityTypeConfiguration<RegistrationTicket>
{
    public void Configure(EntityTypeBuilder<RegistrationTicket> builder)
    {
        builder.ToTable("RegistrationTickets");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Subject).HasMaxLength(256).IsRequired();
        builder.Property(x => x.Email).HasMaxLength(320).IsRequired();
        builder.Property(x => x.HostedDomain).HasMaxLength(256);
        builder.Property(x => x.CreatedAtUtc).IsRequired();
        builder.Property(x => x.ExpiresAtUtc).IsRequired();
        builder.Property(x => x.UsedAtUtc);
    }
}

public sealed class RegistrationAuditConfiguration : IEntityTypeConfiguration<RegistrationAudit>
{
    public void Configure(EntityTypeBuilder<RegistrationAudit> builder)
    {
        builder.ToTable("RegistrationAudits");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Action).HasMaxLength(64).IsRequired();
        builder.Property(x => x.AdminSubject).HasMaxLength(256).IsRequired();
        builder.Property(x => x.TargetSubject).HasMaxLength(256).IsRequired();
        builder.Property(x => x.Role).HasMaxLength(32).IsRequired();
        builder.Property(x => x.TenantId);
        builder.Property(x => x.CorrelationId).HasMaxLength(128).IsRequired();
        builder.Property(x => x.OccurredAtUtc).IsRequired();
    }
}
