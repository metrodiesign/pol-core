using BuildingBlocks.Infrastructure.Persistence;
using Merchants.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Persistence.MerchantUsers.Users;

// Runtime (scalar-only) mapping — mirrors Merchants.Infrastructure.Persistence.Users.UserConfigurations.
// None of these entities carry a CLR navigation to another type in this cluster (MerchantUserId/MerchantId
// are plain Guid/Guid? columns everywhere), so there is nothing to keep as a real EF relationship here.

internal sealed class UserConfiguration(MerchantUserDbContext context) : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("Users", SchemaNames.Merch);
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Subject).HasMaxLength(256).IsRequired();
        builder.Property(x => x.Email).HasMaxLength(320).IsRequired();
        builder.Property(x => x.Status).HasConversion<int>().IsRequired();
        builder.Property(x => x.MerchantId); // NULL until admin approval sets it (REQ-2.3)

        // Pending-approval carve-out (rls-to-query-filter REQ-11.7): MerchantId may be NULL. A NULL never
        // equals CurrentMerchant in SQL, so this filter naturally hides pending rows from every merchant
        // actor without special-casing — they become visible only through the approve write port (task 5),
        // which suppresses this filter explicitly.
        TenantKeyDescriptor.Require(builder.Metadata, nameof(User.MerchantId), allowNullable: true);
        builder.HasQueryFilter(x => x.MerchantId == context.CurrentMerchant);
        builder.Property(x => x.CreatedAt).IsRequired();
        builder.Property(x => x.DisplayName).HasMaxLength(200).IsRequired(); // server-computed from first/last name
        builder.Property(x => x.FirstName).HasMaxLength(200).IsRequired();
        builder.Property(x => x.LastName).HasMaxLength(200).IsRequired();
        builder.Property(x => x.PersonType).HasConversion<int>();
        builder.Property(x => x.IdNumber).HasMaxLength(64);
        builder.Property(x => x.ProducerCode).HasMaxLength(64);
        builder.Property(x => x.LicenseNumber).HasMaxLength(64);
        builder.Property(x => x.Phone).HasMaxLength(32);
        builder.Property(x => x.PhotoObjectKey).HasMaxLength(256); // opaque key, not bytes (REQ-7.2)
        builder.Property(x => x.PhotoContentType).HasMaxLength(128);
        builder.HasIndex(x => x.Subject).IsUnique(); // a subject maps to at most one account (REQ-1.4)
        builder.Ignore(x => x.DomainEvents); // events are enqueued by the handler in-tx (REQ-20), not via the aggregate
    }
}

public sealed class ExternalLoginConfiguration : IEntityTypeConfiguration<ExternalLogin>
{
    public void Configure(EntityTypeBuilder<ExternalLogin> builder)
    {
        builder.ToTable("ExternalLogins", SchemaNames.Merch);
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Provider).HasMaxLength(32).IsRequired();
        builder.Property(x => x.Subject).HasMaxLength(256).IsRequired();
        builder.Property(x => x.MerchantUserId).IsRequired(); // FK-only: no CLR nav to User, stays scalar
        builder.HasIndex(x => new { x.Provider, x.Subject }).IsUnique(); // REQ-2.1
    }
}

public sealed class RegistrationAuditConfiguration : IEntityTypeConfiguration<RegistrationAudit>
{
    public void Configure(EntityTypeBuilder<RegistrationAudit> builder)
    {
        builder.ToTable("RegistrationAudits", SchemaNames.Merch);
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Action).HasMaxLength(64).IsRequired();
        builder.Property(x => x.ActorSubject).HasMaxLength(256);
        builder.Property(x => x.TargetSubject).HasMaxLength(256).IsRequired();
        builder.Property(x => x.Role).HasMaxLength(64);
        builder.Property(x => x.Reason).HasMaxLength(1024);
        builder.Property(x => x.MerchantId);
        builder.Property(x => x.CorrelationId).HasMaxLength(128).IsRequired();
        builder.Property(x => x.OccurredAt).IsRequired();
        AppendOnlyDescriptor.Mark(builder.Metadata); // rls-to-query-filter REQ-2.4-adjacent: append-only audit
    }
}

// Maps onto merch.RegistrationNotices, which the SecurityObjects migration creates in raw SQL (control-plane, no
// merchant predicate, pol_admin + pol_worker). ExcludeFromMigrations mirrored exactly — PolDbContext still owns
// this table's migration via raw SQL, this context only reads/writes it at runtime.
public sealed class RegistrationNoticeConfiguration : IEntityTypeConfiguration<RegistrationNotice>
{
    public void Configure(EntityTypeBuilder<RegistrationNotice> builder)
    {
        builder.ToTable("RegistrationNotices", SchemaNames.Merch, t => t.ExcludeFromMigrations());
        builder.HasKey(x => x.Id);
        builder.Property(x => x.MerchantUserId).IsRequired();
        builder.Property(x => x.Subject).HasMaxLength(256).IsRequired();
        builder.Property(x => x.Email).HasMaxLength(320).IsRequired();
        builder.Property(x => x.DisplayName).HasMaxLength(200).IsRequired();
        builder.Property(x => x.HostedDomain).HasMaxLength(256);
        builder.Property(x => x.OccurredAt).IsRequired();
        builder.Property(x => x.CreatedAt).IsRequired();
        builder.HasIndex(x => x.MerchantUserId).IsUnique(); // one notice per registration (idempotent, REQ-20.4)
    }
}
