using BuildingBlocks.Infrastructure.Persistence;
using BuildingBlocks.Infrastructure.Vault;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Persistence.MerchantRuntime.Vault;

// Filtered mapping — mirrors BuildingBlocks.Infrastructure.Vault.VaultRevealAuditConfiguration exactly for
// column/index shape, but is a FRESH class (see Outbox/OutboxMessageConfiguration.cs for why). Filtered
// per REQ-1.6 (fail-closed) — the vault-audit narrow escape-hatch port (IVaultAuditAppender) that
// suppresses this for the applock/append path is task 5's scope, not this one.

internal sealed class VaultRevealAuditConfiguration(MerchantRuntimeDbContext context) : IEntityTypeConfiguration<VaultRevealAudit>
{
    public void Configure(EntityTypeBuilder<VaultRevealAudit> builder)
    {
        builder.ToTable("VaultRevealAudits", SchemaNames.Merch);
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();
        builder.Property(x => x.SecretName).HasMaxLength(128).IsRequired();
        builder.Property(x => x.Seq).IsRequired();
        builder.Property(x => x.PrevHash).HasColumnType("varbinary(32)").IsRequired();
        builder.Property(x => x.Hash).HasColumnType("varbinary(32)").IsRequired();
        builder.Property(x => x.RevealedAt).IsRequired();

        builder.HasIndex(x => new { x.MerchantId, x.Seq }).IsUnique();
        builder.HasIndex(x => new { x.MerchantId, x.Id });

        TenantKeyDescriptor.Require(builder.Metadata, nameof(VaultRevealAudit.MerchantId));
        builder.HasQueryFilter(x => x.MerchantId == context.CurrentMerchant);
        AppendOnlyDescriptor.Mark(builder.Metadata); // rls-to-query-filter REQ-2.4: append-only
    }
}
