using BuildingBlocks.Infrastructure.Idempotency;
using BuildingBlocks.Infrastructure.Vault;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BuildingBlocks.Infrastructure.Persistence;

internal sealed class AdminOperationRecordConfiguration : IEntityTypeConfiguration<AdminOperationRecord>
{
    public void Configure(EntityTypeBuilder<AdminOperationRecord> builder)
    {
        builder.ToTable("AdminOperationRecords", SchemaNames.Txn);
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Operation).HasMaxLength(120).IsRequired();
        builder.Property(x => x.IdempotencyKey).HasMaxLength(200).IsRequired();
        builder.Property(x => x.IntentHash).HasMaxLength(64).IsRequired();
        builder.Property(x => x.State).HasConversion<int>().IsRequired();
        builder.Property(x => x.Result).HasMaxLength(16_384);
        builder.Property(x => x.ResourceId).HasMaxLength(200);
        builder.HasIndex(x => new { x.MerchantId, x.ActorId, x.Operation, x.IdempotencyKey }).IsUnique();
        builder.HasIndex(x => x.ExpiresAt);
    }
}

internal sealed class VaultSecretVersionConfiguration : IEntityTypeConfiguration<VaultSecretVersion>
{
    public void Configure(EntityTypeBuilder<VaultSecretVersion> builder)
    {
        builder.ToTable("VaultSecretVersions", SchemaNames.Merch);
        builder.HasKey(x => x.Id);
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
