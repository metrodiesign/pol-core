using Iam.Domain.Roles;
using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Persistence.ControlPlane.Iam;

// Runtime (scalar-only, except one exception) mapping — mirrors Iam.Infrastructure.Persistence.Roles.
// RoleConfigurations. Role.Permissions is a REAL CLR navigation (backed by the private _permissions field
// domain logic mutates via SetPermissions) — that HasMany/Navigation pairing MUST stay wired here or
// ControlPlaneDbContext could never load/save it correctly; it is same-context (both Role and
// RolePermission map into ControlPlaneDbContext), so keeping it does not reintroduce cross-context nav.

public sealed class RoleConfiguration : IEntityTypeConfiguration<Role>
{
    public void Configure(EntityTypeBuilder<Role> builder)
    {
        builder.ToTable("Roles", SchemaNames.Iam, t => t.HasCheckConstraint(
            "CK_Roles_ScopeMerchant", "([Scope] = 0 AND [MerchantId] IS NULL) OR [Scope] = 1"));
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Code).HasMaxLength(64).IsRequired();
        builder.Property(x => x.Name).HasMaxLength(128).IsRequired();
        builder.Property(x => x.Description).HasMaxLength(256);
        builder.Property(x => x.Color).HasMaxLength(16);
        builder.Property(x => x.Status).HasConversion<int>().IsRequired();
        builder.Property(x => x.Scope).HasConversion<int>().IsRequired();
        builder.Property(x => x.MerchantId);
        builder.HasIndex(x => new { x.MerchantId, x.Code }).IsUnique().HasFilter(null);

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
        builder.HasIndex(x => new { x.RoleId, x.PermissionKey }).IsUnique();
    }
}
