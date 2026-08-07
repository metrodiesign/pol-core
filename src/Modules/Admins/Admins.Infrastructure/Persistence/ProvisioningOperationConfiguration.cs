using BuildingBlocks.Infrastructure.Persistence;
using BuildingBlocks.Infrastructure.Provisioning;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Admins.Infrastructure.Persistence;

/// <summary>
/// Migration-owner mapping — mirrors Persistence.ControlPlane.Admins.ProvisioningOperationConfiguration
/// exactly for column/index shape. Discovered by PolDbContext via HostModuleAssemblies.All (same mechanism
/// as UserConfigurations.cs alongside it), not applied explicitly.
/// </summary>
public sealed class ProvisioningOperationConfiguration : IEntityTypeConfiguration<ProvisioningOperation>
{
    public void Configure(EntityTypeBuilder<ProvisioningOperation> builder)
    {
        builder.ToTable("ProvisioningOperations", SchemaNames.Admin);
        builder.HasKey(x => x.Id);
        builder.Property(x => x.OperationKey).HasMaxLength(200).IsRequired();
        builder.Property(x => x.CallerAdminId).IsRequired();
        builder.Property(x => x.ExpectedAuthorizationVersion).IsRequired();
        builder.Property(x => x.RequestHash).HasMaxLength(64).IsRequired(); // SHA-256 hex
        builder.Property(x => x.MerchantId).IsRequired(); // pre-minted, deliberately NOT a real FK (row precedes merch.Merchants)
        builder.Property(x => x.Result).HasColumnType("json");
        builder.Property(x => x.CreatedAt).IsRequired();
        builder.HasIndex(x => x.OperationKey).IsUnique().HasDatabaseName("UX_ProvisioningOperations_Key");
    }
}
