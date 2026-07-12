using BuildingBlocks.Infrastructure.Persistence;
using Merchants.Domain.Users.Roles;
using Merchants.Domain.Users.Permissions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Merchants.Infrastructure.Persistence.Users.Roles;

// EF mappings for the Merchant-User Role RBAC realm onto the merch schema (discovered via ModuleAssemblies.Modules).
// Control-plane tables: NO merchant RLS predicate, granted to pol_admin only (see the SecurityObjects migration). The
// permission/group tables are reference data seeded by the SeedData migration; Keys.Key is the FK
// target of role grants so a role can never reference a key outside the catalog (REQ-16.2).
// ponytail: DUPLICATE of Admins.Infrastructure.Persistence.AdminRoleConfigurations — deliberate debt, do not refactor into a shared base.

public sealed class PermissionGroupConfiguration : IEntityTypeConfiguration<PermissionGroup>
{
    public void Configure(EntityTypeBuilder<PermissionGroup> builder)
    {
        builder.ToTable("MerchantUserPermissionGroups", SchemaNames.Merch);
        builder.HasKey(x => x.Key);
        builder.Property(x => x.Key).HasMaxLength(32);
        builder.Property(x => x.LabelTh).HasMaxLength(128).IsRequired();
        builder.Property(x => x.SortOrder).IsRequired();
    }
}

public sealed class PermissionConfiguration : IEntityTypeConfiguration<Permission>
{
    public void Configure(EntityTypeBuilder<Permission> builder)
    {
        builder.ToTable("Keys", SchemaNames.Merch);
        builder.HasKey(x => x.Key);
        builder.Property(x => x.Key).HasMaxLength(64);
        builder.Property(x => x.GroupKey).HasMaxLength(32).IsRequired();
        builder.Property(x => x.LabelTh).HasMaxLength(160).IsRequired();
        builder.Property(x => x.SortOrder).IsRequired();
        builder.HasOne<PermissionGroup>().WithMany().HasForeignKey(x => x.GroupKey)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => x.GroupKey);
    }
}

public sealed class RoleConfiguration : IEntityTypeConfiguration<Role>
{
    public void Configure(EntityTypeBuilder<Role> builder)
    {
        builder.ToTable("MerchantUserRoleDefinitions", SchemaNames.Merch);
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Code).HasMaxLength(64).IsRequired();
        builder.HasIndex(x => x.Code).IsUnique(); // immutable identity (REQ-16.1)
        builder.Property(x => x.Name).HasMaxLength(128).IsRequired();
        builder.Property(x => x.Description).HasMaxLength(256); // nullable
        builder.Property(x => x.Color).HasMaxLength(16);        // nullable
        builder.Property(x => x.Status).HasConversion<int>().IsRequired();

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
        builder.ToTable("MerchantUserRolePermissions", SchemaNames.Merch);
        builder.HasKey(x => x.Id);
        builder.Property(x => x.PermissionKey).HasMaxLength(64).IsRequired();
        builder.HasIndex(x => new { x.RoleId, x.PermissionKey }).IsUnique(); // one grant per (role, permission)
        // FK to the catalog: a role can only grant a real permission (REQ-16.2). Restrict so a granted permission
        // cannot be removed from the catalog out from under a role.
        builder.HasOne<Permission>().WithMany().HasForeignKey(x => x.PermissionKey)
            .HasPrincipalKey(p => p.Key).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class RoleAssignmentConfiguration : IEntityTypeConfiguration<RoleAssignment>
{
    public void Configure(EntityTypeBuilder<RoleAssignment> builder)
    {
        builder.ToTable("MerchantUserRoleAssignments", SchemaNames.Merch);
        builder.HasKey(x => x.Id);
        builder.Property(x => x.MerchantUserId).IsRequired();
        builder.Property(x => x.RoleId).IsRequired();
        builder.Property(x => x.MerchantId).IsRequired();
        builder.Property(x => x.AssignedByAdminId).IsRequired();
        builder.Property(x => x.AssignedAt).IsRequired();
        builder.HasIndex(x => new { x.MerchantUserId, x.RoleId }).IsUnique(); // REQ-16.3
        builder.HasIndex(x => new { x.MerchantUserId, x.MerchantId });        // per-request resolution lookup (REQ-16.4/17.1)
        // Restrict: a role with bound accounts cannot be deleted at the DB either (REQ-16.5 is also checked in the
        // handler for a clean 409). MerchantUserId is a soft reference (mirrors AdminRoleAssignment.AdminAccountId).
        builder.HasOne<Role>().WithMany().HasForeignKey(x => x.RoleId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
