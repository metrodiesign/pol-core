using BuildingBlocks.Infrastructure.Persistence;
using BuildingBlocks.Infrastructure.Provisioning;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Persistence.ControlPlane.Admins;

/// <summary>
/// rls-to-query-filter task 7: <c>admin.ProvisioningOperations</c> — the provisioning idempotency ledger
/// (design.md "Provisioning UoW" step 4). Task 8's migration creates this table for real; until then it is a
/// documented pre-migration phantom (mirrors <c>MerchantUserOutbox</c>'s task 5 precedent — see
/// <c>ModelDisjointnessTests.knownPreMigrationPhantoms</c>). No query filter: control-plane, and the ledger's
/// OWN <see cref="ProvisioningOperation.MerchantId"/> is a pre-minted, non-FK value — not a tenant key.
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
        builder.Property(x => x.Result); // no MaxLength -> nvarchar(max) on SQL Server; SQLite's CREATE TABLE parser
                                          // rejects the literal "(max)" token if set via HasColumnType (task 2 precedent)
        builder.Property(x => x.CreatedAt).IsRequired();
        builder.HasIndex(x => x.OperationKey).IsUnique().HasDatabaseName("UX_ProvisioningOperations_Key");
    }
}
