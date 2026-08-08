using BuildingBlocks.Infrastructure.Persistence;
using Iam.Domain.Roles;
using Merchants.Domain.Users.Roles;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Merchants.Infrastructure.Persistence.Users.Roles;

// EF mapping for the merchant-user-side role ASSIGNMENT edge onto the merch schema (discovered via
// ModuleAssemblies.Modules). Control-plane table: NO merchant RLS predicate, granted to pol_admin only. The
// catalog itself (Permission/PermissionGroup/Role/RolePermission) moved to the iam schema (rf2) —
// Merchants.Infrastructure no longer owns any catalog EF configuration, only this assignment edge.

public sealed class RoleAssignmentConfiguration : IEntityTypeConfiguration<RoleAssignment>
{
    public void Configure(EntityTypeBuilder<RoleAssignment> builder)
    {
        builder.ToTable("RoleAssignments", SchemaNames.Merch);
        builder.HasKey(x => x.Id);
        builder.Property(x => x.UserId).IsRequired();
        builder.Property(x => x.RoleId).IsRequired();
        builder.Property(x => x.MerchantId).IsRequired();
        builder.Property(x => x.AssignedById).IsRequired();
        builder.Property(x => x.AssignedAt).IsRequired();
        builder.HasIndex(x => new { x.UserId, x.RoleId }).IsUnique();
        builder.HasIndex(x => new { x.UserId, x.MerchantId }); // per-request resolution lookup
        // Restrict: a role with bound accounts cannot be deleted at the DB either (also checked in the
        // handler for a clean 409 — Iam.Application's DeleteRoleHandler). UserId is a soft reference
        // (mirrors PlatformMerchantAccess.MerchantId). RoleId now points at the central iam.Roles catalog (rf2).
        builder.HasOne<Role>().WithMany().HasForeignKey(x => x.RoleId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
