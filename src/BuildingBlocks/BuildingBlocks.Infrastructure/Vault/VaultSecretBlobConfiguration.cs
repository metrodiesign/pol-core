using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BuildingBlocks.Infrastructure.Vault;

public sealed class VaultSecretBlobConfiguration : IEntityTypeConfiguration<VaultSecretBlob>
{
    public void Configure(EntityTypeBuilder<VaultSecretBlob> builder)
    {
        builder.ToTable("VaultSecrets", SchemaNames.Merch);
        builder.HasKey(x => new { x.MerchantId, x.Name });
        builder.Property(x => x.Name).HasMaxLength(128);
        builder.Property(x => x.KeyId).HasMaxLength(64).IsRequired();
        builder.Property(x => x.Hint).HasMaxLength(16).IsRequired();
    }
}
