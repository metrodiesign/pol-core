using Admins.Domain.Users;
using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Admins.Infrastructure.Persistence.Users;

// EF mappings for the admin realm onto the admin schema (discovered via HostModuleAssemblies.All). These
// are control-plane tables: NO merchant RLS predicate, granted to pol_admin only (see AddAdminIdentityTables).

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
    }
}

public sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("Users", SchemaNames.Admin, table => table.HasCheckConstraint(
            "CK_Users_TenantId_MicrosoftProvider",
            "[TenantId] IS NULL OR [Provider] COLLATE Latin1_General_100_BIN2 = N'microsoft'"));
        builder.HasKey(x => x.Id);
        // Provider slug ("google"/"microsoft"): identity is the PAIR (Provider, Subject) — DEFAULT 'google'
        // backfills pre-discriminator rows in-place (microsoft-oidc-ciam-alignment REQ-4.5/4.6).
        builder.Property(x => x.Provider).HasMaxLength(32).IsRequired().HasDefaultValue(User.GoogleProvider);
        builder.Property(x => x.TenantId);
        builder.Property(x => x.Subject).HasMaxLength(256);
        builder.Property(x => x.Email).HasMaxLength(AdminContactEmail.MaxLength);
        builder.Property(x => x.Tier).HasConversion<int>().IsRequired();
        builder.Property(x => x.Status).HasConversion<int>().IsRequired();
        builder.Property(x => x.CreatedAt).IsRequired();
        builder.Property(x => x.UpdatedAt);
        // rls-to-query-filter REQ-4.9/4.11 (task 8): real column, mirrors Persistence.ControlPlane's own
        // UserConfiguration — the authorization lease's conditional no-op UPDATE relies on EF's native
        // concurrency-token WHERE clause (WHERE Id=@caller AND AuthorizationVersion=@expected).
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
        builder.HasIndex(x => new { x.AdminUserId, x.MerchantId }).IsUnique(); // REQ-4.1/4.4
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
        builder.Property(x => x.TargetRoleId); // role-CRUD audit target (admin-role-rbac REQ-10.2)
        builder.Property(x => x.CorrelationId).HasMaxLength(128).IsRequired();
        builder.Property(x => x.OccurredAt).IsRequired();
    }
}
