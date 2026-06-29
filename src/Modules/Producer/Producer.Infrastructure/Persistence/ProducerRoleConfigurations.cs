using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Producer.Domain;

namespace Producer.Infrastructure.Persistence;

// EF mappings for the Producer Role RBAC realm onto the producer schema (discovered via ModuleAssemblies.Producer).
// Control-plane tables: NO tenant RLS predicate, granted to pol_admin only (see AddProducerRoleRbacTables). The
// permission/group tables are reference data seeded by the migration; ProducerPermissions.Key is the FK target of
// role grants so a role can never reference a key outside the catalog (REQ-16.2).
// ponytail: DUPLICATE of Admin.Infrastructure.Persistence.AdminRoleConfigurations — deliberate debt, do not refactor into a shared base.

public sealed class ProducerPermissionGroupConfiguration : IEntityTypeConfiguration<ProducerPermissionGroup>
{
    public void Configure(EntityTypeBuilder<ProducerPermissionGroup> builder)
    {
        builder.ToTable("ProducerPermissionGroups");
        builder.HasKey(x => x.Key);
        builder.Property(x => x.Key).HasMaxLength(32);
        builder.Property(x => x.LabelTh).HasMaxLength(128).IsRequired();
        builder.Property(x => x.SortOrder).IsRequired();
    }
}

public sealed class ProducerPermissionConfiguration : IEntityTypeConfiguration<ProducerPermission>
{
    public void Configure(EntityTypeBuilder<ProducerPermission> builder)
    {
        builder.ToTable("ProducerPermissions");
        builder.HasKey(x => x.Key);
        builder.Property(x => x.Key).HasMaxLength(64);
        builder.Property(x => x.GroupKey).HasMaxLength(32).IsRequired();
        builder.Property(x => x.LabelTh).HasMaxLength(160).IsRequired();
        builder.Property(x => x.SortOrder).IsRequired();
        builder.HasOne<ProducerPermissionGroup>().WithMany().HasForeignKey(x => x.GroupKey)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => x.GroupKey);
    }
}

public sealed class ProducerRoleConfiguration : IEntityTypeConfiguration<ProducerRole>
{
    public void Configure(EntityTypeBuilder<ProducerRole> builder)
    {
        builder.ToTable("ProducerRoles");
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

public sealed class ProducerRolePermissionConfiguration : IEntityTypeConfiguration<ProducerRolePermission>
{
    public void Configure(EntityTypeBuilder<ProducerRolePermission> builder)
    {
        builder.ToTable("ProducerRolePermissions");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.PermissionKey).HasMaxLength(64).IsRequired();
        builder.HasIndex(x => new { x.RoleId, x.PermissionKey }).IsUnique(); // one grant per (role, permission)
        // FK to the catalog: a role can only grant a real permission (REQ-16.2). Restrict so a granted permission
        // cannot be removed from the catalog out from under a role.
        builder.HasOne<ProducerPermission>().WithMany().HasForeignKey(x => x.PermissionKey)
            .HasPrincipalKey(p => p.Key).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class ProducerRoleAssignmentConfiguration : IEntityTypeConfiguration<ProducerRoleAssignment>
{
    public void Configure(EntityTypeBuilder<ProducerRoleAssignment> builder)
    {
        builder.ToTable("ProducerRoleAssignments");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.ProducerAccountId).IsRequired();
        builder.Property(x => x.RoleId).IsRequired();
        builder.Property(x => x.TenantId).IsRequired();
        builder.Property(x => x.AssignedByAdminId).IsRequired();
        builder.Property(x => x.AssignedAt).IsRequired();
        builder.HasIndex(x => new { x.ProducerAccountId, x.RoleId }).IsUnique(); // REQ-16.3
        builder.HasIndex(x => new { x.ProducerAccountId, x.TenantId });          // per-request resolution lookup (REQ-16.4/17.1)
        // Restrict: a role with bound accounts cannot be deleted at the DB either (REQ-16.5 is also checked in the
        // handler for a clean 409). ProducerAccountId is a soft reference (mirrors AdminRoleAssignment.AdminAccountId).
        builder.HasOne<ProducerRole>().WithMany().HasForeignKey(x => x.RoleId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
