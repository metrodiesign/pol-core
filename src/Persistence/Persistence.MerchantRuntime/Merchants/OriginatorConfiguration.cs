using BuildingBlocks.Infrastructure.Persistence;
using Merchants.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Persistence.MerchantRuntime.Merchants;

internal sealed class OriginatorConfiguration(MerchantRuntimeDbContext context) : IEntityTypeConfiguration<Originator>
{
    public void Configure(EntityTypeBuilder<Originator> builder)
    {
        builder.ToTable("Originators", SchemaNames.Merch);
        builder.HasKey(x => x.Id);
        TenantKeyDescriptor.Require(builder.Metadata, nameof(Originator.MerchantId));
        builder.HasQueryFilter(x => x.MerchantId == context.CurrentMerchant);
        builder.Property(x => x.Code).HasMaxLength(64).IsRequired();
        builder.Property(x => x.Name).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Type).HasConversion<int>().IsRequired();
        builder.Property(x => x.SaleCode).HasMaxLength(100);
        builder.Property(x => x.Status).HasConversion<int>().IsRequired();
        builder.Property(x => x.Version).IsConcurrencyToken().IsRequired();
        builder.HasIndex(x => new { x.MerchantId, x.Code }).IsUnique();
        builder.HasAlternateKey(x => new { x.MerchantId, x.Id });
        builder.HasOne<Merchant>().WithMany().HasForeignKey(x => x.MerchantId).OnDelete(DeleteBehavior.Restrict);
    }
}
