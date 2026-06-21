using Cart.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cart.Infrastructure;

/// <summary>
/// Maps a cart line into the <c>producer</c> schema. The unit price is two scalar columns per the EF
/// money mapping rule; the computed <c>UnitPrice</c> and <c>LineTotal</c> projections are not persisted.
/// </summary>
public sealed class CartItemConfiguration : IEntityTypeConfiguration<CartItem>
{
    public void Configure(EntityTypeBuilder<CartItem> builder)
    {
        builder.ToTable("CartItems");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.CartId).IsRequired();
        builder.Property(x => x.ProductId).IsRequired();
        builder.Property(x => x.Quantity).IsRequired();
        builder.Property(x => x.UnitPriceMinorUnits).IsRequired();
        builder.Property(x => x.UnitPriceCurrency).HasMaxLength(3).IsRequired();

        builder.Ignore(x => x.UnitPrice);
        builder.Ignore(x => x.LineTotal);
    }
}
