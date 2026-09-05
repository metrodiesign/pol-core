using Admins.Domain.Users;
using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Persistence.ControlPlane.Admins;

// Runtime (scalar-only) mapping — mirrors Admins.Infrastructure.Persistence.Users.UserConfigurations
// exactly for column/index shape. The migration-owner (PolDbContext) keeps the canonical
// IEntityTypeConfiguration; this copy is what ControlPlaneDbContext (the runtime chokepoint) actually
// uses (design.md "Runtime EF config is scalar-only, separate from the migration-owner's relationship
// config"). Keeping every runtime config scalar-only avoids re-deriving "same-cluster vs cross-cluster"
// per property.

public sealed class WorkforceTenantBindingConfiguration : IEntityTypeConfiguration<WorkforceTenantBinding>
{
    public void Configure(EntityTypeBuilder<WorkforceTenantBinding> builder)
    {
        builder.ToTable("WorkforceTenantBindings", SchemaNames.Admin, table =>
            table.HasCheckConstraint("CK_WorkforceTenantBindings_Singleton", "[Id] = 1"));
        builder.HasKey(x => x.Id);
        builder.HasAlternateKey(x => x.TenantId).HasName("AK_WorkforceTenantBindings_TenantId");
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.TenantId).IsRequired();
        AppendOnlyDescriptor.Mark(builder.Metadata);
    }
}

public sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        // Runtime tests also build this model on SQLite, whose default equality is case-sensitive. Production's
        // migration-owned constraint uses an explicit SQL Server BIN2 collation under the same constraint name.
        builder.ToTable("Users", SchemaNames.Admin, table => table.HasCheckConstraint(
            "CK_Users_TenantId_MicrosoftProvider", "[TenantId] IS NULL OR [Provider] = 'microsoft'"));
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Provider).HasMaxLength(32).IsRequired().HasDefaultValue(User.GoogleProvider);
        builder.Property(x => x.TenantId);
        builder.Property(x => x.Subject).HasMaxLength(256);
        builder.Property(x => x.Email).HasMaxLength(AdminContactEmail.MaxLength);
        builder.Property(x => x.Tier).HasConversion<int>().IsRequired();
        builder.Property(x => x.Status).HasConversion<int>().IsRequired();
        builder.Property(x => x.CreatedAt).IsRequired();
        builder.Property(x => x.UpdatedAt);
        // rls-to-query-filter REQ-4.9/4.11: the authorization lease's conditional no-op UPDATE relies on EF's
        // native concurrency-token WHERE clause (WHERE Id=@caller AND AuthorizationVersion=@expected) to get
        // exactly-one-row-or-throw for free — mirrors the tenant-key concurrency-token pattern from task 3.
        builder.Property(x => x.AuthorizationVersion).IsConcurrencyToken();
        builder.Property(x => x.Version).HasDefaultValue(1L).IsConcurrencyToken();
        builder.HasIndex(x => new { x.Provider, x.TenantId, x.Subject })
            .IsUnique()
            .HasFilter("[Subject] IS NOT NULL");
        builder.HasIndex(x => x.TenantId);
        // tier0-graph-employee-profile REQ-2.11/3.8/8.1-8.4: profile columns, EmployeeId filtered-unique.
        builder.Property(x => x.EmployeeId).HasMaxLength(EmployeeIdPolicy.MaxLength);
        builder.Property(x => x.FirstName).HasMaxLength(500);
        builder.Property(x => x.LastName).HasMaxLength(500);
        builder.HasIndex(x => x.EmployeeId).IsUnique().HasFilter("[EmployeeId] IS NOT NULL");
        builder.HasOne<WorkforceTenantBinding>().WithMany().HasForeignKey(x => x.TenantId)
            .HasPrincipalKey(x => x.TenantId).IsRequired(false).OnDelete(DeleteBehavior.NoAction);
    }
}

public sealed class MerchantAccessConfiguration : IEntityTypeConfiguration<MerchantAccess>
{
    public void Configure(EntityTypeBuilder<MerchantAccess> builder)
    {
        builder.ToTable("MerchantAccess", SchemaNames.Admin);
        builder.HasKey(x => x.Id);
        builder.Property(x => x.AdminUserId).IsRequired();
        builder.Property(x => x.MerchantId).IsRequired();
        builder.Property(x => x.AssignedByAdminId).IsRequired();
        builder.Property(x => x.AssignedAt).IsRequired();
        builder.HasIndex(x => new { x.AdminUserId, x.MerchantId }).IsUnique();
    }
}

public sealed class AuditConfiguration : IEntityTypeConfiguration<Audit>
{
    public void Configure(EntityTypeBuilder<Audit> builder)
    {
        builder.ToTable("UserAudits", SchemaNames.Admin);
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Action).HasMaxLength(64).IsRequired();
        builder.Property(x => x.ActorType).HasMaxLength(16).IsRequired();
        builder.Property(x => x.ActorId).IsRequired();
        builder.Property(x => x.TargetAdminId);
        builder.Property(x => x.MerchantId);
        builder.Property(x => x.TargetRoleId);
        builder.Property(x => x.CorrelationId).HasMaxLength(128).IsRequired();
        builder.Property(x => x.OccurredAt).IsRequired();
        AppendOnlyDescriptor.Mark(builder.Metadata); // rls-to-query-filter REQ-2.4-adjacent: append-only admin audit
    }
}
