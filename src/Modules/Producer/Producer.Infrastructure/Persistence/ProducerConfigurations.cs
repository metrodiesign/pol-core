using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Producer.Domain;

namespace Producer.Infrastructure.Persistence;

// EF mappings for the producer identity realm onto the producer schema (discovered via ModuleAssemblies.Producer).
// ProducerAccounts is control-plane (NO tenant predicate, pol_admin only — like Admin.AdminAccounts); the tenant edge
// is a separate ProducerTenantAssignments row. The other child tables are control-plane too. PascalCase identifiers
// map straight to columns.
// ponytail: shape mirrors Admin.Infrastructure.Persistence.AdminConfigurations (control-plane EF style) — deliberate.

public sealed class ProducerAccountConfiguration : IEntityTypeConfiguration<ProducerAccount>
{
    public void Configure(EntityTypeBuilder<ProducerAccount> builder)
    {
        builder.ToTable("ProducerAccounts");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Subject).HasMaxLength(256).IsRequired();
        builder.Property(x => x.Email).HasMaxLength(320).IsRequired();
        builder.Property(x => x.Status).HasConversion<int>().IsRequired();
        builder.Property(x => x.CreatedAt).IsRequired();
        builder.HasIndex(x => x.Subject).IsUnique(); // a subject maps to at most one account (REQ-1.4)
        builder.Ignore(x => x.DomainEvents); // events are enqueued by the handler in-tx (REQ-20), not via the aggregate
    }
}

// The tenant edge of a ProducerAccount (REQ-6) — mirrors Admin.AdminTenantAssignment, control-plane. A producer acts
// for exactly one tenant: UNIQUE on ProducerAccountId (not the (account, tenant) pair Admin uses), so a second tenant
// for the same account raises a unique-violation.
public sealed class ProducerTenantAssignmentConfiguration : IEntityTypeConfiguration<ProducerTenantAssignment>
{
    public void Configure(EntityTypeBuilder<ProducerTenantAssignment> builder)
    {
        builder.ToTable("ProducerTenantAssignments");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.ProducerAccountId).IsRequired();
        builder.Property(x => x.TenantId).IsRequired();
        builder.Property(x => x.AssignedByAdminId).IsRequired();
        builder.Property(x => x.AssignedAt).IsRequired();
        builder.HasIndex(x => x.ProducerAccountId).IsUnique(); // exactly one tenant per producer account (REQ-6)
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
        builder.Property(x => x.ProducerAccountId).IsRequired();
        builder.HasIndex(x => new { x.Provider, x.Subject }).IsUnique(); // REQ-2.1
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
        builder.Property(x => x.Purpose).HasConversion<int>().IsRequired();
        builder.Property(x => x.CreatedAt).IsRequired();
        builder.Property(x => x.ExpiresAt).IsRequired();
        builder.Property(x => x.UsedAt); // the single-use replay guard (REQ-3.4)
        builder.Ignore(x => x.DomainEvents);
    }
}

public sealed class TenantUserProfileConfiguration : IEntityTypeConfiguration<TenantUserProfile>
{
    public void Configure(EntityTypeBuilder<TenantUserProfile> builder)
    {
        builder.ToTable("TenantUserProfiles");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.ProducerAccountId).IsRequired();
        builder.Property(x => x.DisplayName).HasMaxLength(200).IsRequired();
        builder.Property(x => x.FirstName).HasMaxLength(200);
        builder.Property(x => x.LastName).HasMaxLength(200);
        builder.Property(x => x.PersonType).HasConversion<int>();
        builder.Property(x => x.IdNumber).HasMaxLength(64);
        builder.Property(x => x.ProducerCode).HasMaxLength(64);
        builder.Property(x => x.LicenseNumber).HasMaxLength(64);
        builder.Property(x => x.Phone).HasMaxLength(32);
        builder.Property(x => x.PhotoObjectKey).HasMaxLength(256); // opaque key, not bytes (REQ-7.2)
        builder.Property(x => x.PhotoContentType).HasMaxLength(128);
        builder.HasIndex(x => x.ProducerAccountId).IsUnique(); // one-to-one with the account (REQ-7.1)
    }
}

public sealed class RegistrationAuditConfiguration : IEntityTypeConfiguration<RegistrationAudit>
{
    public void Configure(EntityTypeBuilder<RegistrationAudit> builder)
    {
        builder.ToTable("RegistrationAudits");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Action).HasMaxLength(64).IsRequired();
        builder.Property(x => x.ActorSubject).HasMaxLength(256);
        builder.Property(x => x.TargetSubject).HasMaxLength(256).IsRequired();
        builder.Property(x => x.Role).HasMaxLength(64);
        builder.Property(x => x.Reason).HasMaxLength(1024);
        builder.Property(x => x.TenantId);
        builder.Property(x => x.CorrelationId).HasMaxLength(128).IsRequired();
        builder.Property(x => x.OccurredAt).IsRequired();
    }
}

// Maps onto producer.ProducerRegistrationNotices, which AddProducerIdentityTables created in raw SQL (the consumer
// landed in Task 4). Column shapes mirror that DDL exactly; control-plane (no tenant predicate), pol_admin + pol_worker.
public sealed class ProducerRegistrationNoticeConfiguration : IEntityTypeConfiguration<ProducerRegistrationNotice>
{
    public void Configure(EntityTypeBuilder<ProducerRegistrationNotice> builder)
    {
        // The table + its unique index + grants are managed by AddProducerIdentityTables' raw SQL; exclude it from
        // migration diffing so EF maps it for runtime reads/writes without trying to CREATE it a second time.
        builder.ToTable("ProducerRegistrationNotices", t => t.ExcludeFromMigrations());
        builder.HasKey(x => x.Id);
        builder.Property(x => x.TenantUserId).IsRequired();
        builder.Property(x => x.Subject).HasMaxLength(256).IsRequired();
        builder.Property(x => x.Email).HasMaxLength(320).IsRequired();
        builder.Property(x => x.DisplayName).HasMaxLength(200).IsRequired();
        builder.Property(x => x.HostedDomain).HasMaxLength(256);
        builder.Property(x => x.OccurredAt).IsRequired();
        builder.Property(x => x.CreatedAt).IsRequired();
        builder.HasIndex(x => x.TenantUserId).IsUnique(); // one notice per registration (idempotent, REQ-20.4)
    }
}
