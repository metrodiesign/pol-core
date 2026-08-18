using BuildingBlocks.Infrastructure.Persistence;
using Merchants.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Merchants.Infrastructure.Persistence.Users;

// EF mappings for the merchant-user identity realm onto the merch schema (discovered via ModuleAssemblies.Modules).
// MerchantUsers is control-plane (NO merchant predicate, pol_admin only — like Admin.PlatformUsers); the merchant edge
// is now User.MerchantId directly (the former separate assignment row is absorbed, REQ-2.3). The other child
// tables are control-plane too. PascalCase identifiers map straight to columns.
// ponytail: shape mirrors Admins.Infrastructure.Persistence.AdminConfigurations (control-plane EF style) — deliberate.

public sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("Users", SchemaNames.Merch, table =>
            table.HasCheckConstraint("CK_Users_ActorMerchant",
                "[Status] NOT IN (2, 4) OR ([MerchantId] IS NOT NULL AND [MerchantId] <> '00000000-0000-0000-0000-000000000000')"));
        builder.HasKey(x => x.Id);
        // Provider slug ("google"/"microsoft"): identity is the PAIR (Provider, Subject) — DEFAULT 'google'
        // backfills pre-discriminator rows in-place (microsoft-oidc-ciam-alignment REQ-4.5/4.6).
        builder.Property(x => x.Provider).HasMaxLength(32).IsRequired().HasDefaultValue(ExternalLogin.Google);
        builder.Property(x => x.Subject).HasMaxLength(256).IsRequired();
        builder.Property(x => x.Email).HasMaxLength(320).IsRequired();
        builder.Property(x => x.Status).HasConversion<int>().IsRequired();
        builder.Property(x => x.MerchantId); // NULL until admin approval sets it (REQ-2.3)
        builder.Property(x => x.Version).IsConcurrencyToken().IsRequired();
        builder.Property(x => x.CreatedAt).IsRequired();
        // The registrant's own person details (REQ-7.1) live on the account — a "merchant" is the company/app, not
        // the person, so this data belongs to the person's record, not a merchant-scoped profile.
        builder.Property(x => x.DisplayName).HasMaxLength(200).IsRequired(); // server-computed from first/last name
        builder.Property(x => x.FirstName).HasMaxLength(200).IsRequired();
        builder.Property(x => x.LastName).HasMaxLength(200).IsRequired();
        builder.Property(x => x.IdentityType).HasConversion<int>().IsRequired();
        builder.Property(x => x.IdentityNumber).HasMaxLength(64);
        builder.Property(x => x.SaleCode).HasMaxLength(20).IsUnicode(false);
        builder.Property(x => x.LicenseNumber).HasMaxLength(64);
        builder.Property(x => x.Phone).HasMaxLength(32);
        builder.Property(x => x.PhotoObjectKey).HasMaxLength(256); // opaque key, not bytes (REQ-7.2)
        builder.Property(x => x.PhotoContentType).HasMaxLength(128);
        builder.Property(x => x.KycPhotoObjectKey).HasMaxLength(256);
        builder.HasIndex(x => new { x.Provider, x.Subject }).IsUnique(); // one account per provider identity (REQ-4.6)
        builder.HasIndex(x => new { x.Id, x.MerchantId }).IsUnique();
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
        builder.Property(x => x.UserId).IsRequired();
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
        // Canonical target key (microsoft-oidc-ciam-alignment REQ-4.8) — subjects are no longer unique across
        // providers, so the timeline reads by internal id; the FK proves every audit row points at a real user.
        builder.Property(x => x.TargetUserId).IsRequired();
        builder.Property(x => x.ActorAdminId); // canonical actor for admin actions (REQ-4.9); NULL = self-service
        builder.Property(x => x.ActorSubject).HasMaxLength(256);
        builder.Property(x => x.TargetSubject).HasMaxLength(256).IsRequired();
        builder.Property(x => x.Role).HasMaxLength(64);
        builder.Property(x => x.Reason).HasMaxLength(1024);
        builder.Property(x => x.MerchantId);
        builder.Property(x => x.CorrelationId).HasMaxLength(128).IsRequired();
        builder.Property(x => x.OccurredAt).IsRequired();
        builder.HasOne<User>().WithMany().HasForeignKey(x => x.TargetUserId).OnDelete(DeleteBehavior.Restrict);
        // The registration-history timeline filters by TargetUserId on every request; the audit table grows
        // platform-wide, so an unindexed scan would degrade with total registrations, not per-user history.
        builder.HasIndex(x => x.TargetUserId);
    }
}

// Per-submit form snapshot (registration-attempt-history REQ-1). Maxlengths mirror the User columns the values
// are copied from. The FK to merch.Users is a real declared relationship (no CLR navigation) — the first in
// this cluster: REQ-1.3 mandates the DB constraint, and the runtime mirror needs the same declaration so EF
// orders the Users INSERT before the attempt INSERT in the registration branch's single SaveChanges.
public sealed class RegistrationAttemptConfiguration : IEntityTypeConfiguration<RegistrationAttempt>
{
    public void Configure(EntityTypeBuilder<RegistrationAttempt> builder)
    {
        builder.ToTable("RegistrationAttempts", SchemaNames.Merch);
        builder.HasKey(x => x.Id);
        builder.Property(x => x.UserId).IsRequired();
        builder.Property(x => x.AttemptNo).IsRequired();
        builder.Property(x => x.Purpose).HasConversion<int>().IsRequired();
        builder.Property(x => x.FirstName).HasMaxLength(200).IsRequired();
        builder.Property(x => x.LastName).HasMaxLength(200).IsRequired();
        builder.Property(x => x.IdentityType).HasConversion<int>().IsRequired();
        builder.Property(x => x.IdentityNumber).HasMaxLength(64);
        builder.Property(x => x.SaleCode).HasMaxLength(20).IsUnicode(false);
        builder.Property(x => x.LicenseNumber).HasMaxLength(64);
        builder.Property(x => x.Phone).HasMaxLength(32);
        builder.Property(x => x.Email).HasMaxLength(320).IsRequired();
        builder.Property(x => x.PhotoObjectKey).HasMaxLength(256);
        builder.Property(x => x.PhotoContentType).HasMaxLength(128);
        builder.Property(x => x.SubmittedAt).IsRequired();
        builder.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.UserId, x.AttemptNo }).IsUnique(); // REQ-1.4/1.5 — races lose here → 409
    }
}

// Maps onto merch.RegistrationNotices, which the SecurityObjects migration creates in raw SQL (control-plane, no
// merchant predicate, sole runtime principal pol_app). Column shapes mirror that DDL exactly.
public sealed class RegistrationNoticeConfiguration : IEntityTypeConfiguration<RegistrationNotice>
{
    public void Configure(EntityTypeBuilder<RegistrationNotice> builder)
    {
        // The table + its unique index + grants are managed by the SecurityObjects migration's raw SQL; exclude it
        // from migration diffing so EF maps it for runtime reads/writes without trying to CREATE it a second time.
        builder.ToTable("RegistrationNotices", SchemaNames.Merch, t => t.ExcludeFromMigrations());
        builder.HasKey(x => x.Id);
        builder.Property(x => x.UserId).IsRequired();
        builder.Property(x => x.Subject).HasMaxLength(256).IsRequired();
        builder.Property(x => x.Email).HasMaxLength(320).IsRequired();
        builder.Property(x => x.DisplayName).HasMaxLength(200).IsRequired();
        builder.Property(x => x.HostedDomain).HasMaxLength(256);
        builder.Property(x => x.OccurredAt).IsRequired();
        builder.Property(x => x.CreatedAt).IsRequired();
        builder.HasIndex(x => x.UserId).IsUnique(); // one notice per registration (idempotent, REQ-20.4)
    }
}

public sealed class MerchantUserInvitationConfiguration : IEntityTypeConfiguration<MerchantUserInvitation>
{
    public void Configure(EntityTypeBuilder<MerchantUserInvitation> builder)
    {
        builder.ToTable("MerchantUserInvitations", SchemaNames.Merch);
        builder.HasKey(x => x.Id);
        builder.Property(x => x.MerchantId).IsRequired();
        builder.Property(x => x.Email).HasMaxLength(320).IsRequired();
        builder.Property(x => x.NormalizedEmail).HasMaxLength(320).IsRequired();
        builder.Property(x => x.TokenHash).HasMaxLength(64).IsUnicode(false).IsRequired();
        builder.Property(x => x.ExpiresAt).IsRequired();
        builder.Property(x => x.CreatedByUserId).IsRequired();
        builder.Property(x => x.CreatedByAudience).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(x => x.IntendedRoleCodesJson).HasMaxLength(2_000).IsRequired();
        builder.Property(x => x.CreatedAt).IsRequired();
        builder.Property(x => x.RowVersion).IsRowVersion();
        builder.HasIndex(x => x.TokenHash).IsUnique();
        builder.HasIndex(x => new { x.MerchantId, x.NormalizedEmail }).IsUnique()
            .HasFilter("[AcceptedAt] IS NULL AND [RevokedAt] IS NULL");
    }
}

public sealed class AdminUserOperationRecordConfiguration : IEntityTypeConfiguration<AdminUserOperationRecord>
{
    public void Configure(EntityTypeBuilder<AdminUserOperationRecord> builder)
    {
        builder.ToTable("AdminUserOperationRecords", SchemaNames.Merch);
        builder.HasKey(x => x.Id);
        builder.Property(x => x.MerchantId);
        builder.Property(x => x.ActorId).IsRequired();
        builder.Property(x => x.Operation).HasMaxLength(120).IsRequired();
        builder.Property(x => x.IdempotencyKey).HasMaxLength(200).IsRequired();
        builder.Property(x => x.IntentHash).HasMaxLength(64).IsUnicode(false).IsRequired();
        builder.Property(x => x.Result).HasMaxLength(16_384).IsRequired();
        builder.Property(x => x.HttpStatus).IsRequired();
        builder.Property(x => x.CreatedAt).IsRequired();
        builder.Property(x => x.ExpiresAt).IsRequired();
        builder.HasIndex(x => new { x.MerchantId, x.ActorId, x.Operation, x.IdempotencyKey }).IsUnique();
        builder.HasIndex(x => new { x.ActorId, x.Operation, x.IdempotencyKey })
            .IsUnique()
            .HasFilter("[MerchantId] IS NULL");
        builder.HasIndex(x => x.ExpiresAt);
    }
}

public sealed class MerchantUserManagementAuditConfiguration : IEntityTypeConfiguration<MerchantUserManagementAudit>
{
    public void Configure(EntityTypeBuilder<MerchantUserManagementAudit> builder)
    {
        builder.ToTable("MerchantUserManagementAudits", SchemaNames.Merch, table =>
            table.HasCheckConstraint("CK_MerchantUserManagementAudits_Target",
                "[TargetUserId] IS NOT NULL OR [InvitationId] IS NOT NULL"));
        builder.HasKey(x => x.Id);
        builder.Property(x => x.MerchantId).IsRequired();
        builder.Property(x => x.Action).HasMaxLength(32).IsUnicode(false).IsRequired();
        builder.Property(x => x.CorrelationId).HasMaxLength(128).IsRequired();
        builder.Property(x => x.OccurredAt).IsRequired();
        builder.HasIndex(x => new { x.MerchantId, x.OccurredAt });
    }
}
