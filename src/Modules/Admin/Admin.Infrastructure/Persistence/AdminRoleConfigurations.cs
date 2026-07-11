using Admin.Domain;
using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Admin.Infrastructure.Persistence;

// EF mappings for the Admin Role RBAC realm onto the producer schema (discovered via ModuleAssemblies.Producer).
// Control-plane tables: NO merchant RLS predicate, granted to pol_admin only (see AddAdminRoleRbacTables). The
// permission/group tables are reference data seeded by the migration; AdminPermissions.Key is the FK target of
// role grants so a role can never reference a key outside the catalog (REQ-3.2).

public sealed class AdminPermissionGroupConfiguration : IEntityTypeConfiguration<AdminPermissionGroup>
{
    public void Configure(EntityTypeBuilder<AdminPermissionGroup> builder)
    {
        builder.ToTable("AdminPermissionGroups", SchemaNames.Admin);
        builder.HasKey(x => x.Key);
        builder.Property(x => x.Key).HasMaxLength(32);
        builder.Property(x => x.LabelTh).HasMaxLength(128).IsRequired();
        builder.Property(x => x.SortOrder).IsRequired();
    }
}

public sealed class AdminPermissionConfiguration : IEntityTypeConfiguration<AdminPermission>
{
    public void Configure(EntityTypeBuilder<AdminPermission> builder)
    {
        builder.ToTable("AdminPermissions", SchemaNames.Admin);
        builder.HasKey(x => x.Key);
        builder.Property(x => x.Key).HasMaxLength(64);
        builder.Property(x => x.GroupKey).HasMaxLength(32).IsRequired();
        builder.Property(x => x.LabelTh).HasMaxLength(160).IsRequired();
        builder.Property(x => x.SortOrder).IsRequired();
        builder.HasOne<AdminPermissionGroup>().WithMany().HasForeignKey(x => x.GroupKey)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => x.GroupKey);
    }
}

public sealed class AdminRoleConfiguration : IEntityTypeConfiguration<AdminRole>
{
    public void Configure(EntityTypeBuilder<AdminRole> builder)
    {
        builder.ToTable("AdminRoles", SchemaNames.Admin);
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Code).HasMaxLength(64).IsRequired();
        builder.HasIndex(x => x.Code).IsUnique(); // immutable identity (REQ-2.1/2.4)
        builder.Property(x => x.Name).HasMaxLength(128).IsRequired();
        builder.Property(x => x.Description).HasMaxLength(256); // nullable (N2)
        builder.Property(x => x.Color).HasMaxLength(16);        // nullable (N2)
        builder.Property(x => x.Status).HasConversion<int>().IsRequired();

        // Granted permissions are child rows of the aggregate, written through the _permissions backing field.
        builder.HasMany(x => x.Permissions).WithOne().HasForeignKey(x => x.RoleId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(x => x.Permissions).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

public sealed class AdminRolePermissionConfiguration : IEntityTypeConfiguration<AdminRolePermission>
{
    public void Configure(EntityTypeBuilder<AdminRolePermission> builder)
    {
        builder.ToTable("AdminRolePermissions", SchemaNames.Admin);
        builder.HasKey(x => x.Id);
        builder.Property(x => x.PermissionKey).HasMaxLength(64).IsRequired();
        builder.HasIndex(x => new { x.RoleId, x.PermissionKey }).IsUnique(); // one grant per (role, permission)
        // FK to the catalog: a role can only grant a real permission (REQ-3.2). Restrict so a granted permission
        // cannot be removed from the catalog out from under a role.
        builder.HasOne<AdminPermission>().WithMany().HasForeignKey(x => x.PermissionKey)
            .HasPrincipalKey(p => p.Key).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class AdminRoleAssignmentConfiguration : IEntityTypeConfiguration<AdminRoleAssignment>
{
    public void Configure(EntityTypeBuilder<AdminRoleAssignment> builder)
    {
        builder.ToTable("AdminRoleAssignments", SchemaNames.Admin);
        builder.HasKey(x => x.Id);
        builder.Property(x => x.PlatformUserId).IsRequired();
        builder.Property(x => x.RoleId).IsRequired();
        builder.Property(x => x.AssignedByAdminId).IsRequired();
        builder.Property(x => x.AssignedAt).IsRequired();
        builder.HasIndex(x => new { x.PlatformUserId, x.RoleId }).IsUnique(); // REQ-4.1
        // Restrict: a role with bound users cannot be deleted at the DB either (REQ-4.4 is also checked in the
        // handler for a clean 409). PlatformUserId is a soft reference (mirrors PlatformMerchantAccess.MerchantId).
        builder.HasOne<AdminRole>().WithMany().HasForeignKey(x => x.RoleId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
