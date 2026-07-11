using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BuildingBlocks.Infrastructure.Vault;

public sealed class VaultRevealAuditConfiguration : IEntityTypeConfiguration<VaultRevealAudit>
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

        // Per-merchant monotonic sequence: a unique (MerchantId, Seq) makes a concurrent fork fail loudly and
        // a deletion leave a detectable gap. (MerchantId, Id) walks the chain in append order for verify.
        builder.HasIndex(x => new { x.MerchantId, x.Seq }).IsUnique();
        builder.HasIndex(x => new { x.MerchantId, x.Id });
    }
}
