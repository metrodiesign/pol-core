using BuildingBlocks.Infrastructure.Persistence;
using Merchants.Domain.Users.Roles;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Persistence.MerchantUser.Users;

// Runtime (scalar-only) mapping — mirrors Merchants.Infrastructure.Persistence.Users.Roles.RoleConfigurations.
// RoleId stays a plain scalar (no HasOne<Iam.Domain.Roles.Role>()): RoleId now points at the central iam.Roles
// catalog, which lives in the OTHER (ControlPlane) runtime context, and RoleAssignment carries no CLR
// navigation to Role anyway — the migration-owner's HasOne was FK-only, so nothing observable is lost by
// dropping it here. Importing Iam.Domain into this assembly to keep that HasOne would break cluster
// disjointness (rls-to-query-filter design.md "Context topology").

internal sealed class RoleAssignmentConfiguration(MerchantUserDbContext context) : IEntityTypeConfiguration<RoleAssignment>
{
    public void Configure(EntityTypeBuilder<RoleAssignment> builder)
    {
        builder.ToTable("RoleAssignments", SchemaNames.Merch);
        builder.HasKey(x => x.Id);
        builder.Property(x => x.MerchantUserId).IsRequired();
        builder.Property(x => x.RoleId).IsRequired();
        builder.Property(x => x.MerchantId).IsRequired();
        builder.Property(x => x.AssignedById).IsRequired();
        builder.Property(x => x.AssignedAt).IsRequired();
        builder.HasIndex(x => new { x.MerchantUserId, x.RoleId }).IsUnique();
        builder.HasIndex(x => new { x.MerchantUserId, x.MerchantId }); // per-request resolution lookup

        TenantKeyDescriptor.Require(builder.Metadata, nameof(RoleAssignment.MerchantId));
        builder.HasQueryFilter(x => x.MerchantId == context.CurrentMerchant);
    }
}
