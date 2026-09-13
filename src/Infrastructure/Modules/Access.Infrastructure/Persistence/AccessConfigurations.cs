using Access.Domain;
using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Access.Infrastructure.Persistence;

public sealed class MerchantAccessConfiguration : IEntityTypeConfiguration<MerchantAccess>
{
    public void Configure(EntityTypeBuilder<MerchantAccess> builder)
    {
        builder.ToTable("MerchantAccess", SchemaNames.Access, table =>
            table.HasCheckConstraint("CK_MerchantAccess_DataScope", "[DataScope] IN (1, 2, 3, 4)"));
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.AccountId).IsRequired();
        builder.Property(x => x.MerchantId).IsRequired();
        builder.Property(x => x.DataScope).HasConversion<int>().IsRequired();
        builder.Property(x => x.Status).HasConversion<int>().IsRequired();
        builder.Property(x => x.Version).IsConcurrencyToken().IsRequired();
        builder.HasAlternateKey(x => new { x.Id, x.MerchantId });
        builder.HasIndex(x => new { x.AccountId, x.MerchantId })
            .IsUnique()
            .HasFilter("[Status] = 1");
        builder.HasIndex(x => x.MerchantId);
    }
}

public sealed class AccessRoleConfiguration : IEntityTypeConfiguration<AccessRole>
{
    public void Configure(EntityTypeBuilder<AccessRole> builder)
    {
        builder.ToTable("AccessRoles", SchemaNames.Access);
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.MerchantAccessId).IsRequired();
        builder.Property(x => x.MerchantId).IsRequired();
        builder.Property(x => x.RoleId).IsRequired();
        builder.HasOne<MerchantAccess>().WithMany()
            .HasForeignKey(x => new { x.MerchantAccessId, x.MerchantId })
            .HasPrincipalKey(x => new { x.Id, x.MerchantId })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.MerchantAccessId, x.RoleId }).IsUnique();
    }
}

public sealed class BranchAccessConfiguration : IEntityTypeConfiguration<BranchAccess>
{
    public void Configure(EntityTypeBuilder<BranchAccess> builder)
    {
        builder.ToTable("BranchAccess", SchemaNames.Access);
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.MerchantAccessId).IsRequired();
        builder.Property(x => x.MerchantId).IsRequired();
        builder.Property(x => x.BranchId).IsRequired();
        builder.HasOne<MerchantAccess>().WithMany()
            .HasForeignKey(x => new { x.MerchantAccessId, x.MerchantId })
            .HasPrincipalKey(x => new { x.Id, x.MerchantId })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.MerchantAccessId, x.BranchId }).IsUnique();
    }
}

public sealed class PlatformAccessConfiguration : IEntityTypeConfiguration<PlatformAccess>
{
    public void Configure(EntityTypeBuilder<PlatformAccess> builder)
    {
        builder.ToTable("PlatformAccess", SchemaNames.Access);
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.EmployeeAccountId).IsRequired();
        builder.Property(x => x.Status).HasConversion<int>().IsRequired();
        builder.Property(x => x.Version).IsConcurrencyToken().IsRequired();
        builder.HasIndex(x => x.EmployeeAccountId).IsUnique();
        builder.HasOne<Accounts.Domain.Employee>().WithOne()
            .HasForeignKey<PlatformAccess>(x => x.EmployeeAccountId)
            .HasPrincipalKey<Accounts.Domain.Employee>(x => x.AccountId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class PlatformAccessRoleConfiguration : IEntityTypeConfiguration<PlatformAccessRole>
{
    public void Configure(EntityTypeBuilder<PlatformAccessRole> builder)
    {
        builder.ToTable("PlatformAccessRoles", SchemaNames.Access, table =>
            table.HasCheckConstraint("CK_PlatformAccessRoles_RoleScope", "[RoleScope] IN (1, 3)"));
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.PlatformAccessId).IsRequired();
        builder.Property(x => x.RoleId).IsRequired();
        builder.Property(x => x.RoleScope).IsRequired();
        builder.HasIndex(x => new { x.PlatformAccessId, x.RoleId }).IsUnique();
    }
}

public sealed class SystemClientScopeConfiguration : IEntityTypeConfiguration<SystemClientScope>
{
    public void Configure(EntityTypeBuilder<SystemClientScope> builder)
    {
        builder.ToTable("SystemClientScopes", SchemaNames.Access);
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.SystemClientId).IsRequired();
        builder.Property(x => x.ScopeCode).HasMaxLength(128).IsRequired();
        builder.HasIndex(x => new { x.SystemClientId, x.ScopeCode }).IsUnique();
    }
}

public sealed class MerchantAccessMethodConfiguration : IEntityTypeConfiguration<MerchantAccessMethod>
{
    public void Configure(EntityTypeBuilder<MerchantAccessMethod> builder)
    {
        builder.ToTable("MerchantAccessMethods", SchemaNames.Access);
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.MerchantAccessId).IsRequired();
        builder.Property(x => x.MethodCode).HasMaxLength(64).IsRequired();
        builder.HasIndex(x => new { x.MerchantAccessId, x.MethodCode }).IsUnique();
    }
}
