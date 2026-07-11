using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Products.Domain;

namespace Products.Infrastructure;

/// <summary>
/// EF mapping for <see cref="Product"/>. Per the Money mapping rule, <see cref="Product.Price"/> is
/// mapped as a complex type onto two scalar columns (PriceAmount decimal(19,4), PriceCurrency
/// char(3)). Discovered at model-build time by <c>PolDbContext</c> via
/// <c>HostModuleAssemblies.All</c>.
/// </summary>
public sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> builder)
    {
        builder.ToTable("Products", SchemaNames.Shop);
        builder.HasKey(x => x.Id);

        builder.ComplexProperty(x => x.Price, p =>
        {
            p.Property(m => m.Amount).HasColumnName("PriceAmount").HasPrecision(19, 4);
            p.Property(m => m.Currency).HasColumnName("PriceCurrency").HasMaxLength(3).IsFixedLength().IsUnicode(false);
        });

        builder.Property(x => x.Name).HasMaxLength(200).IsRequired();
        builder.Property(x => x.IsActive).IsRequired();
        builder.Property(x => x.CreatedAt).IsRequired();

        // Merchant-scoped active-catalog lookups (RLS still gates the rows).
        builder.HasIndex(x => new { x.MerchantId, x.IsActive });
    }
}
