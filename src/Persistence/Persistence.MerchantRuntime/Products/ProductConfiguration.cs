using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Products.Domain;

namespace Persistence.MerchantRuntime.Products;

// Runtime (scalar-only) mapping — mirrors Products.Infrastructure.ProductConfiguration exactly for
// column/index shape (rls-to-query-filter design.md "Runtime EF config is scalar-only, separate from
// the migration-owner's relationship config"). No HasOne here — Product has none to begin with.

internal sealed class ProductConfiguration(MerchantRuntimeDbContext context) : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> builder)
    {
        builder.ToTable("Products", SchemaNames.Shop);
        builder.HasKey(x => x.Id);
        TenantKeyDescriptor.Require(builder.Metadata, nameof(Product.MerchantId));
        builder.HasQueryFilter(x => x.MerchantId == context.CurrentMerchant);

        builder.ComplexProperty(x => x.Price, p =>
        {
            p.Property(m => m.Amount).HasColumnName("PriceAmount").HasPrecision(19, 4);
            p.Property(m => m.Currency).HasColumnName("PriceCurrency").HasMaxLength(3).IsFixedLength().IsUnicode(false);
        });

        builder.Property(x => x.Name).HasMaxLength(200).IsRequired();
        builder.Property(x => x.IsActive).IsRequired();
        builder.Property(x => x.CreatedAt).IsRequired();

        builder.HasIndex(x => new { x.MerchantId, x.IsActive });
    }
}
