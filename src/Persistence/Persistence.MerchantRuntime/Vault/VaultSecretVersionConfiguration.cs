using BuildingBlocks.Infrastructure.Persistence;
using BuildingBlocks.Infrastructure.Vault;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Persistence.MerchantRuntime.Vault;

internal sealed class VaultSecretVersionConfiguration(MerchantRuntimeDbContext context)
    : IEntityTypeConfiguration<VaultSecretVersion>
{
    public void Configure(EntityTypeBuilder<VaultSecretVersion> builder)
    {
        builder.ToTable("VaultSecretVersions", SchemaNames.Merch);
        builder.HasKey(x => x.Id);
        TenantKeyDescriptor.Require(builder.Metadata, nameof(VaultSecretVersion.MerchantId));
        builder.HasQueryFilter(x => x.MerchantId == context.CurrentMerchant);
        builder.Property(x => x.SecretName).HasMaxLength(128).IsRequired();
        builder.Property(x => x.SecretKey).HasMaxLength(64).IsRequired();
        builder.Property(x => x.EncryptedDek).IsRequired();
        builder.Property(x => x.EncryptedSecret).IsRequired();
        builder.Property(x => x.Hint).HasMaxLength(512).IsRequired();
        builder.Property(x => x.State).HasConversion<int>().IsRequired();
        builder.HasIndex(x => new { x.MerchantId, x.SecretName, x.Version }).IsUnique();
        builder.HasAlternateKey(x => new { x.MerchantId, x.Id });
        builder.HasIndex(x => new { x.MerchantId, x.SecretName, x.State })
            .IsUnique().HasFilter("[State] = 2");
    }
}
