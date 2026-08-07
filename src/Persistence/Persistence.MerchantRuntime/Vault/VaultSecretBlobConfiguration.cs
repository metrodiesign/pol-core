using BuildingBlocks.Infrastructure.Persistence;
using BuildingBlocks.Infrastructure.Vault;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Persistence.MerchantRuntime.Vault;

// Filtered mapping — mirrors BuildingBlocks.Infrastructure.Vault.VaultSecretBlobConfiguration exactly for
// column/index shape, but is a FRESH class (see Outbox/OutboxMessageConfiguration.cs for why).

internal sealed class VaultSecretBlobConfiguration(MerchantRuntimeDbContext context) : IEntityTypeConfiguration<VaultSecretBlob>
{
    public void Configure(EntityTypeBuilder<VaultSecretBlob> builder)
    {
        builder.ToTable("VaultSecrets", SchemaNames.Merch);
        builder.HasKey(x => new { x.MerchantId, x.SecretName });
        builder.Property(x => x.SecretName).HasMaxLength(128);
        builder.Property(x => x.SecretKey).HasMaxLength(64).IsRequired();
        builder.Property(x => x.Hint).HasMaxLength(16).IsRequired();

        TenantKeyDescriptor.Require(builder.Metadata, nameof(VaultSecretBlob.MerchantId));
        builder.HasQueryFilter(x => x.MerchantId == context.CurrentMerchant);
    }
}
