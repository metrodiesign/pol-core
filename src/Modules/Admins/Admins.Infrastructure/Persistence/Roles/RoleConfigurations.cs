using Admins.Domain.Roles;
using BuildingBlocks.Infrastructure.Persistence;
using Iam.Domain.Roles;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Admins.Infrastructure.Persistence.Roles;

// EF mapping for the admin-side role ASSIGNMENT edge onto the admin schema (discovered via
// HostModuleAssemblies.All). Control-plane table: NO merchant RLS predicate, granted to pol_admin only. The
// catalog itself (Permission/PermissionGroup/Role/RolePermission) moved to the iam schema (rf2) —
// Admins.Infrastructure no longer owns any catalog EF configuration, only this assignment edge.

public sealed class RoleAssignmentConfiguration : IEntityTypeConfiguration<RoleAssignment>
{
    public void Configure(EntityTypeBuilder<RoleAssignment> builder)
    {
        builder.ToTable("RoleAssignments", SchemaNames.Admin);
        builder.HasKey(x => x.Id);
        builder.Property(x => x.PlatformUserId).IsRequired();
        builder.Property(x => x.RoleId).IsRequired();
        builder.Property(x => x.AssignedByAdminId).IsRequired();
        builder.Property(x => x.AssignedAt).IsRequired();
        builder.HasIndex(x => new { x.PlatformUserId, x.RoleId }).IsUnique(); // REQ-4.1
        // Restrict: a role with bound users cannot be deleted at the DB either (also checked in the handler
        // for a clean 409 — Iam.Application's DeleteRoleHandler). PlatformUserId is a soft reference (mirrors
        // PlatformMerchantAccess.MerchantId). RoleId now points at the central iam.Roles catalog (rf2).
        builder.HasOne<Role>().WithMany().HasForeignKey(x => x.RoleId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
