using BuildingBlocks.Infrastructure.Persistence;
using Merchants.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Persistence.MerchantUsers.Users;

// Runtime mapping — mirrors Merchants.Infrastructure.Persistence.Users.UserConfigurations.
// No entity here carries a CLR navigation (UserId/MerchantId are plain Guid/Guid? columns), and most
// stay scalar-only; the ONE declared relationship is RegistrationAttempt→User (scalar-FK HasOne<User>()), which
// this runtime side must mirror so EF orders the Users INSERT before the attempt INSERT in the registration
// branch's single SaveChanges (registration-attempt-history B3).

internal sealed class UserConfiguration(MerchantUserDbContext context) : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("Users", SchemaNames.Merch);
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Provider).HasMaxLength(32).IsRequired().HasDefaultValue(ExternalLogin.Google);
        builder.Property(x => x.Subject).HasMaxLength(256).IsRequired();
        builder.Property(x => x.Email).HasMaxLength(320).IsRequired();
        // Status + MerchantId are concurrency tokens (bugfix-merchant-prebind-wiring, Codex P1): every tracked
        // UPDATE carries WHERE Status = @original AND MerchantId = @original, which restores the deleted DML
        // writers' conditional semantics (Status = PendingApproval AND MerchantId IS NULL) for EVERY lifecycle
        // transition. Two racing approvals — or an approve racing a reject/resubmit — can both load the same
        // pending row; without the tokens both stale snapshots would commit (last writer wins: e.g. a rejected
        // but merchant-bound account with a stray RoleAssignment). With them the loser's whole transaction
        // (user row + role assignment + audit) fails as DbUpdateConcurrencyException → mapped to
        // ConcurrencyConflictException → 409 by MerchantUserUnitOfWork. Model-level only (no DB column, no
        // migration) — PolDbContext (migration owner, never registered at runtime) is deliberately untouched.
        builder.Property(x => x.Status).HasConversion<int>().IsConcurrencyToken().IsRequired();
        builder.Property(x => x.MerchantId).IsConcurrencyToken(); // NULL until admin approval sets it (REQ-2.3)
        builder.Property(x => x.Version).IsConcurrencyToken().IsRequired();

        // Pending-approval carve-out (rls-to-query-filter REQ-11.7): MerchantId may be NULL. A NULL never
        // equals CurrentMerchant in SQL, so this filter naturally hides pending rows from every merchant
        // actor without special-casing — they become visible only through the approve write port (task 5),
        // which suppresses this filter explicitly.
        TenantKeyDescriptor.Require(
            builder.Metadata, nameof(User.MerchantId), allowNullable: true, allowNullToValue: true);
        builder.HasQueryFilter(x => x.MerchantId == context.CurrentMerchant);
        builder.Property(x => x.CreatedAt).IsRequired();
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
        builder.Property(x => x.UserId).IsRequired(); // FK-only: no CLR nav to User, stays scalar
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
        builder.Property(x => x.TargetUserId).IsRequired(); // canonical target key (REQ-4.8)
        builder.Property(x => x.ActorAdminId); // canonical actor for admin actions (REQ-4.9); NULL = self-service
        builder.Property(x => x.ActorSubject).HasMaxLength(256);
        builder.Property(x => x.TargetSubject).HasMaxLength(256).IsRequired();
        builder.Property(x => x.Role).HasMaxLength(64);
        builder.Property(x => x.Reason).HasMaxLength(1024);
        builder.Property(x => x.MerchantId);
        builder.Property(x => x.CorrelationId).HasMaxLength(128).IsRequired();
        builder.Property(x => x.OccurredAt).IsRequired();
        // Declared on BOTH configs (mirrors RegistrationAttempt's B3 rationale): migration-owner for the DDL
        // FK, here for INSERT ordering — registration writes the user + its audit in ONE SaveChanges.
        builder.HasOne<User>().WithMany().HasForeignKey(x => x.TargetUserId).OnDelete(DeleteBehavior.Restrict);
        // Mirrors the migration-owner index — the registration-history timeline filters by TargetUserId.
        builder.HasIndex(x => x.TargetUserId);
        AppendOnlyDescriptor.Mark(builder.Metadata); // rls-to-query-filter REQ-2.4-adjacent: append-only audit
    }
}

// Runtime mirror of Merchants.Infrastructure's RegistrationAttemptConfiguration (registration-attempt-history
// REQ-1). Not merchant-filtered (same as RegistrationAudits — pre-bind rows have no merchant yet); append-only
// at the write floor, and the DB grant is SELECT, INSERT only.
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
        // Declared on BOTH configs (B3): migration-owner for the DDL constraint, here for INSERT ordering —
        // without it EF may emit the attempt INSERT before the Users INSERT → SQL 547 masquerading as a 409.
        builder.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.UserId, x.AttemptNo }).IsUnique(); // REQ-1.4/1.5 — races lose here → 409
        AppendOnlyDescriptor.Mark(builder.Metadata); // REQ-1.7: snapshots are immutable history
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

internal sealed class MerchantUserInvitationConfiguration(MerchantUserDbContext context)
    : IEntityTypeConfiguration<MerchantUserInvitation>
{
    public void Configure(EntityTypeBuilder<MerchantUserInvitation> builder)
    {
        builder.ToTable("MerchantUserInvitations", SchemaNames.Merch);
        builder.HasKey(x => x.Id);
        builder.Property(x => x.MerchantId).IsRequired();
        TenantKeyDescriptor.Require(builder.Metadata, nameof(MerchantUserInvitation.MerchantId));
        builder.HasQueryFilter(x => x.MerchantId == context.CurrentMerchant);
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

internal sealed class AdminUserOperationRecordConfiguration(MerchantUserDbContext context)
    : IEntityTypeConfiguration<AdminUserOperationRecord>
{
    public void Configure(EntityTypeBuilder<AdminUserOperationRecord> builder)
    {
        builder.ToTable("AdminUserOperationRecords", SchemaNames.Merch);
        builder.HasKey(x => x.Id);
        builder.Property(x => x.MerchantId);
        TenantKeyDescriptor.Require(builder.Metadata, nameof(AdminUserOperationRecord.MerchantId), allowNullable: true);
        builder.HasQueryFilter(x => x.MerchantId == context.CurrentMerchant);
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
        AppendOnlyDescriptor.Mark(builder.Metadata);
    }
}

internal sealed class MerchantUserManagementAuditConfiguration(MerchantUserDbContext context)
    : IEntityTypeConfiguration<MerchantUserManagementAudit>
{
    public void Configure(EntityTypeBuilder<MerchantUserManagementAudit> builder)
    {
        builder.ToTable("MerchantUserManagementAudits", SchemaNames.Merch);
        builder.HasKey(x => x.Id);
        builder.Property(x => x.MerchantId).IsRequired();
        TenantKeyDescriptor.Require(builder.Metadata, nameof(MerchantUserManagementAudit.MerchantId));
        builder.HasQueryFilter(x => x.MerchantId == context.CurrentMerchant);
        builder.Property(x => x.Action).HasMaxLength(32).IsUnicode(false).IsRequired();
        builder.Property(x => x.CorrelationId).HasMaxLength(128).IsRequired();
        builder.Property(x => x.OccurredAt).IsRequired();
        builder.HasIndex(x => new { x.MerchantId, x.OccurredAt });
        builder.ToTable(table => table.HasCheckConstraint("CK_MerchantUserManagementAudits_Target",
            "[TargetUserId] IS NOT NULL OR [InvitationId] IS NOT NULL"));
        AppendOnlyDescriptor.Mark(builder.Metadata);
    }
}
