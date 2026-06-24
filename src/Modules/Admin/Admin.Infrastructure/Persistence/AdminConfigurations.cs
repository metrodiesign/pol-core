using Admin.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Admin.Infrastructure.Persistence;

// EF mappings for the admin realm onto the producer schema (discovered via ModuleAssemblies.Producer). These
// are control-plane tables: NO tenant RLS predicate, granted to pol_admin only (see AddAdminIdentityTables).

public sealed class AdminAccountConfiguration : IEntityTypeConfiguration<AdminAccount>
{
    public void Configure(EntityTypeBuilder<AdminAccount> builder)
    {
        builder.ToTable("AdminAccounts");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Subject).HasMaxLength(256); // nullable until an invited Scoped account binds it
        builder.Property(x => x.Email).HasMaxLength(320).IsRequired();
        builder.Property(x => x.Tier).HasConversion<int>().IsRequired();
        builder.Property(x => x.Status).HasConversion<int>().IsRequired();
        builder.Property(x => x.CreatedAt).IsRequired();
        // Filtered unique: one account per bound subject; invited (NULL-subject) rows are exempt (REQ-3.1).
        builder.HasIndex(x => x.Subject).IsUnique().HasFilter("[Subject] IS NOT NULL");
        builder.HasIndex(x => x.Email).IsUnique(); // the invite key before a subject is bound
    }
}

public sealed class AdminTenantAssignmentConfiguration : IEntityTypeConfiguration<AdminTenantAssignment>
{
    public void Configure(EntityTypeBuilder<AdminTenantAssignment> builder)
    {
        builder.ToTable("AdminTenantAssignments");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.AdminAccountId).IsRequired();
        builder.Property(x => x.TenantId).IsRequired();
        builder.Property(x => x.AssignedByAdminId).IsRequired();
        builder.Property(x => x.AssignedAt).IsRequired();
        builder.HasIndex(x => new { x.AdminAccountId, x.TenantId }).IsUnique(); // REQ-4.1/4.4
    }
}

public sealed class AdminAccountAuditConfiguration : IEntityTypeConfiguration<AdminAccountAudit>
{
    public void Configure(EntityTypeBuilder<AdminAccountAudit> builder)
    {
        builder.ToTable("AdminAccountAudits");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Action).HasMaxLength(64).IsRequired();
        builder.Property(x => x.ActorType).HasMaxLength(16).IsRequired();
        builder.Property(x => x.ActorId).IsRequired();
        builder.Property(x => x.TargetAdminId);
        builder.Property(x => x.TenantId);
        builder.Property(x => x.TargetRoleId); // role-CRUD audit target (admin-role-rbac REQ-10.2)
        builder.Property(x => x.CorrelationId).HasMaxLength(128).IsRequired();
        builder.Property(x => x.OccurredAt).IsRequired();
    }
}
