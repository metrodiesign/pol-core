using Admins.Domain.Users;
using Divisions.Domain;
using Levels.Domain;
using Offices.Domain;
using Positions.Domain;
using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Admins.Infrastructure.Persistence.Users;

// EF mappings for the admin realm onto the admin schema (discovered via HostModuleAssemblies.All). These
// are control-plane tables: NO merchant RLS predicate, granted to pol_admin only (see AddAdminIdentityTables).

public sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("Users", SchemaNames.Admin);
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Subject).HasMaxLength(256); // nullable until an invited Scoped account binds it
        builder.Property(x => x.Email).HasMaxLength(320).IsRequired();
        builder.Property(x => x.Tier).HasConversion<int>().IsRequired();
        builder.Property(x => x.Status).HasConversion<int>().IsRequired();
        builder.Property(x => x.CreatedAt).IsRequired();
        builder.Property(x => x.UpdatedAt);
        // rls-to-query-filter REQ-4.9/4.11 (task 8): real column, mirrors Persistence.ControlPlane's own
        // UserConfiguration — the authorization lease's conditional no-op UPDATE relies on EF's native
        // concurrency-token WHERE clause (WHERE Id=@caller AND AuthorizationVersion=@expected).
        builder.Property(x => x.AuthorizationVersion).IsConcurrencyToken();
        builder.Property(x => x.Version).HasDefaultValue(1L).IsConcurrencyToken();
        // Filtered unique: one account per bound subject; invited (NULL-subject) rows are exempt (REQ-3.1).
        builder.HasIndex(x => x.Subject).IsUnique().HasFilter("[Subject] IS NOT NULL");
        builder.HasIndex(x => x.Email).IsUnique(); // the invite key before a subject is bound

        // Org-profile FKs to the master lists. Nullable (unknown at invite); Restrict so a referenced master
        // can't be hard-deleted (soft-deactivate via IsActive instead). No back-navigation on the master side.
        builder.HasOne<Position>().WithMany().HasForeignKey(x => x.PositionId).IsRequired(false).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Office>().WithMany().HasForeignKey(x => x.OfficeId).IsRequired(false).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Level>().WithMany().HasForeignKey(x => x.LevelId).IsRequired(false).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Division>().WithMany().HasForeignKey(x => x.DivisionId).IsRequired(false).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class MerchantAccessConfiguration : IEntityTypeConfiguration<MerchantAccess>
{
    public void Configure(EntityTypeBuilder<MerchantAccess> builder)
    {
        builder.ToTable("MerchantAccess", SchemaNames.Admin);
        builder.HasKey(x => x.Id);
        builder.Property(x => x.AdminUserId).IsRequired();
        builder.Property(x => x.MerchantId).IsRequired();
        builder.Property(x => x.AssignedByAdminId).IsRequired();
        builder.Property(x => x.AssignedAt).IsRequired();
        builder.HasIndex(x => new { x.AdminUserId, x.MerchantId }).IsUnique(); // REQ-4.1/4.4
    }
}

public sealed class AuditConfiguration : IEntityTypeConfiguration<Audit>
{
    public void Configure(EntityTypeBuilder<Audit> builder)
    {
        builder.ToTable("UserAudits", SchemaNames.Admin);
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Action).HasMaxLength(64).IsRequired();
        builder.Property(x => x.ActorType).HasMaxLength(16).IsRequired();
        builder.Property(x => x.ActorId).IsRequired();
        builder.Property(x => x.TargetAdminId);
        builder.Property(x => x.MerchantId);
        builder.Property(x => x.TargetRoleId); // role-CRUD audit target (admin-role-rbac REQ-10.2)
        builder.Property(x => x.CorrelationId).HasMaxLength(128).IsRequired();
        builder.Property(x => x.OccurredAt).IsRequired();
    }
}
