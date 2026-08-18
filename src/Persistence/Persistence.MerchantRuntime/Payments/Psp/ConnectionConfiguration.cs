using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Payments.Domain.Psp;
using BuildingBlocks.Infrastructure.Vault;

namespace Persistence.MerchantRuntime.Payments.Psp;

// Runtime (scalar-only) mapping — mirrors Payments.Infrastructure.Persistence.Psp.ConnectionConfiguration
// exactly for column/index shape.

internal sealed class ConnectionConfiguration(MerchantRuntimeDbContext context) : IEntityTypeConfiguration<Connection>
{
    public void Configure(EntityTypeBuilder<Connection> builder)
    {
        builder.ToTable("PspConnections", SchemaNames.Txn);
        builder.HasKey(x => x.Id);

        builder.Property(x => x.MerchantId).IsRequired();

        TenantKeyDescriptor.Require(builder.Metadata, nameof(Connection.MerchantId));
        builder.HasQueryFilter(x => x.MerchantId == context.CurrentMerchant);
        builder.Property(x => x.Psp).IsRequired();
        builder.Property(x => x.PaymentProviderId);
        builder.Property(x => x.EnabledMethods).HasMaxLength(256).IsRequired();
        builder.Property(x => x.SecretRefName).HasMaxLength(128).IsRequired();
        // No explicit HasColumnType: an unconstrained `string` property already defaults to nvarchar(max)
        // under the SQL Server provider (identical real DDL), and leaving it provider-agnostic keeps this
        // model buildable against the Sqlite unit-test harness too (a literal "nvarchar(max)" column-type
        // string is SQL-Server-only syntax that Sqlite's CREATE TABLE parser rejects).
        builder.Property(x => x.Metadata);
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
        builder.HasOne<VaultSecretVersion>().WithMany()
            .HasForeignKey(x => new { x.MerchantId, x.ActiveSecretVersionId })
            .HasPrincipalKey(x => new { x.MerchantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<VaultSecretVersion>().WithMany()
            .HasForeignKey(x => new { x.MerchantId, x.PendingSecretVersionId })
            .HasPrincipalKey(x => new { x.MerchantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}
