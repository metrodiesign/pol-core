using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Payments.Domain.Psp;
using BuildingBlocks.Infrastructure.Vault;
using Merchants.Domain;
using Payments.Domain.Capabilities;

namespace Payments.Infrastructure.Persistence.Psp;

/// <summary>
/// Maps <see cref="Connection"/> onto the txn schema. Only the vault lookup name is stored,
/// never the secret. One connection per (merchant, PSP) is enforced by a unique index.
/// </summary>
public sealed class ConnectionConfiguration : IEntityTypeConfiguration<Connection>
{
    public void Configure(EntityTypeBuilder<Connection> builder)
    {
        builder.ToTable("PspConnections", SchemaNames.Txn);
        builder.HasKey(x => x.Id);

        builder.Property(x => x.MerchantId).IsRequired();
        builder.Property(x => x.Psp).IsRequired();
        builder.Property(x => x.PaymentProviderId);
        builder.Property(x => x.EnabledMethods).HasMaxLength(256).IsRequired();
        builder.Property(x => x.SecretRefName).HasMaxLength(128).IsRequired();
        // Non-secret PSP config (merchantId/accountId/card/installment/enabledSources/returnUrls/...) is
        // stored here as JSON; the full Omise payload can exceed 4000 chars, so use nvarchar(max).
        builder.Property(x => x.Metadata).HasColumnType("nvarchar(max)");
        builder.Property(x => x.IsEnabled).IsRequired();
        builder.Property(x => x.CreatedAt).IsRequired();
        builder.Property(x => x.Health).HasConversion<int>().IsRequired();
        builder.Property(x => x.LastTestResult).HasMaxLength(500);
        builder.Property(x => x.Version).IsConcurrencyToken().IsRequired();

        builder.HasIndex(x => new { x.MerchantId, x.Psp }).IsUnique();
        builder.HasIndex(x => new { x.MerchantId, x.PaymentProviderId }).IsUnique()
            .HasFilter("[PaymentProviderId] IS NOT NULL");
        builder.HasIndex(x => new { x.Id, x.MerchantId, x.PaymentProviderId }).IsUnique();
        builder.HasAlternateKey(x => new { x.MerchantId, x.Id });
        builder.HasOne<Merchant>().WithMany().HasForeignKey(x => x.MerchantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<PaymentProvider>().WithMany()
            .HasForeignKey(x => new { x.PaymentProviderId, x.Psp })
            .HasPrincipalKey(x => new { x.Id, x.AdapterCode })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<VaultSecretVersion>().WithMany()
            .HasForeignKey(x => new { x.MerchantId, x.ActiveSecretVersionId })
            .HasPrincipalKey(x => new { x.MerchantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<VaultSecretVersion>().WithMany()
            .HasForeignKey(x => new { x.MerchantId, x.PendingSecretVersionId })
            .HasPrincipalKey(x => new { x.MerchantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}
