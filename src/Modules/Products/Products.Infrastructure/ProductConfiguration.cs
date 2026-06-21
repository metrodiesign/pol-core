using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Products.Domain;

namespace Products.Infrastructure;

/// <summary>
/// EF mapping for <see cref="Product"/>. Per the Money mapping rule, the validating
/// <see cref="Product.Price"/> seam is ignored and persisted as two scalar columns.
/// Discovered at model-build time by <c>ProducerDbContext</c> via <c>ModuleAssemblies.Producer</c>.
/// </summary>
public sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> builder)
    {
        builder.ToTable("Products");
        builder.HasKey(x => x.Id);

        builder.Ignore(x => x.Price);

        builder.Property(x => x.Name).HasMaxLength(200).IsRequired();
        builder.Property(x => x.PriceMinorUnits).IsRequired();
        builder.Property(x => x.PriceCurrency).HasMaxLength(3).IsRequired();
        builder.Property(x => x.IsActive).IsRequired();
        builder.Property(x => x.CreatedAtUtc).IsRequired();

        // Tenant-scoped active-catalog lookups (RLS still gates the rows).
        builder.HasIndex(x => new { x.TenantId, x.IsActive });
    }
}
