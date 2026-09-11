using Admins.Domain.Roles;
using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Persistence.ControlPlane.Admins;

// Runtime (scalar-only) mapping — mirrors Admins.Infrastructure.Persistence.Roles.RoleConfigurations.
// RoleId stays a plain scalar (no HasOne<Iam.Domain.Roles.Role>()): RoleAssignment carries no CLR
// navigation to Role, so the migration-owner's HasOne was FK-only (no observable EF behavior lost here).
// Compare Iam.RoleConfiguration.Permissions, which DOES keep a real HasMany — Role's aggregate genuinely
// navigates that collection.

public sealed class RoleAssignmentConfiguration : IEntityTypeConfiguration<RoleAssignment>
{
    public void Configure(EntityTypeBuilder<RoleAssignment> builder)
    {
        builder.ToTable("RoleAssignments", SchemaNames.Admin);
        builder.HasKey(x => x.Id);
        builder.Property(x => x.AdminUserId).IsRequired();
        builder.Property(x => x.RoleId).IsRequired();
        builder.Property(x => x.AssignedById).IsRequired();
        builder.Property(x => x.AssignedAt).IsRequired();
        builder.HasIndex(x => new { x.AdminUserId, x.RoleId }).IsUnique();
    }
}
