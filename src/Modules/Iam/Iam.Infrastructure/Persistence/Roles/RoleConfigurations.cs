using BuildingBlocks.Infrastructure.Persistence;
using Iam.Domain.Roles;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Iam.Infrastructure.Persistence.Roles;

// EF mappings for the central IAM Role RBAC realm onto the iam schema (discovered via ModuleAssemblies.Modules).
// No RLS predicate (design.md REQ-9.2): Permissions/PermissionGroups are pure vocabulary; Roles/RolePermissions
// carry per-merchant data (MerchantId) but rely on the app-layer visibility floor in Iam.Application's store
// (REQ-3.6/3.9), not a DB policy — see the design's residual-risk note. Per-side assignment tables
// (admin.RoleAssignments / merch.RoleAssignments) live in Admins/Merchants.Infrastructure and FK here.

public sealed class RoleConfiguration : IEntityTypeConfiguration<Role>
{
    public void Configure(EntityTypeBuilder<Role> builder)
    {
        builder.ToTable("Roles", SchemaNames.Iam, t => t.HasCheckConstraint(
            "CK_Roles_ScopeMerchant", "([Scope] = 1 AND [MerchantId] IS NULL) OR [Scope] = 2")); // REQ-3.3
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Code).HasMaxLength(64).IsRequired();
        builder.Property(x => x.Name).HasMaxLength(128).IsRequired();
        builder.Property(x => x.Description).HasMaxLength(256); // nullable
        builder.Property(x => x.Color).HasMaxLength(16);        // nullable
        builder.Property(x => x.Status).HasConversion<int>().IsRequired();
        builder.Property(x => x.Version).HasDefaultValue(1L).IsConcurrencyToken();
        builder.Property(x => x.Scope).HasConversion<int>().IsRequired();
        builder.Property(x => x.MerchantId);

        // UNIQUE (MerchantId, Code): NULL = one shared bucket (SQL Server treats NULL as equal in a unique
        // index, non-ANSI but correct on the pinned stack), so shared/seed role codes cannot collide while
        // different merchants may reuse the same code across their own buckets. HasFilter(null) is REQUIRED —
        // the SqlServer provider's default filtered index ("[MerchantId] IS NOT NULL") would silently exempt
        // every NULL row from uniqueness, letting a duplicate shared code insert (design.md, Codex P2 PR #98).
        builder.HasIndex(x => new { x.MerchantId, x.Code }).IsUnique().HasFilter(null);

        // Granted permissions are child rows of the aggregate, written through the _permissions backing field.
        builder.HasMany(x => x.Permissions).WithOne().HasForeignKey(x => x.RoleId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(x => x.Permissions).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

public sealed class RolePermissionConfiguration : IEntityTypeConfiguration<RolePermission>
{
    public void Configure(EntityTypeBuilder<RolePermission> builder)
    {
        builder.ToTable("RolePermissions", SchemaNames.Iam);
        builder.HasKey(x => x.Id);
        builder.Property(x => x.PermissionKey).HasMaxLength(64).IsRequired();
        builder.HasIndex(x => new { x.RoleId, x.PermissionKey }).IsUnique(); // one grant per (role, permission)
        // FK to the catalog: a role can only grant a real permission (REQ-2.6). Restrict so a granted permission
        // cannot be removed from the catalog out from under a role.
        builder.HasOne<Iam.Domain.Permissions.Permission>().WithMany().HasForeignKey(x => x.PermissionKey)
            .HasPrincipalKey(p => p.Key).OnDelete(DeleteBehavior.Restrict);
    }
}
