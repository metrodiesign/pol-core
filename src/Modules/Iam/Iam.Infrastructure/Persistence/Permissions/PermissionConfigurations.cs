using BuildingBlocks.Infrastructure.Persistence;
using Iam.Domain.Permissions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Iam.Infrastructure.Persistence.Permissions;

// EF mappings for the central IAM permission catalog onto the iam schema (discovered via ModuleAssemblies.Modules).
// Reference data seeded by the SeedData migration; Permission.Key is the FK target of role grants (REQ-2.6).

public sealed class PermissionGroupConfiguration : IEntityTypeConfiguration<PermissionGroup>
{
    public void Configure(EntityTypeBuilder<PermissionGroup> builder)
    {
        builder.ToTable("PermissionGroups", SchemaNames.Iam);
        builder.HasKey(x => x.Key);
        builder.Property(x => x.Key).HasMaxLength(32);
        builder.Property(x => x.Scope).HasConversion<int>().IsRequired();
        builder.Property(x => x.LabelTh).HasMaxLength(128).IsRequired();
        builder.Property(x => x.SortOrder).IsRequired();
    }
}

public sealed class PermissionConfiguration : IEntityTypeConfiguration<Permission>
{
    public void Configure(EntityTypeBuilder<Permission> builder)
    {
        builder.ToTable("Permissions", SchemaNames.Iam);
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
