using Carts.Domain.Items;
using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Carts.Infrastructure.Items;

/// <summary>
/// Maps a cart line into the <c>shop</c> schema. <c>UnitPrice</c> is mapped as a complex type
/// (UnitPriceAmount decimal(19,4), UnitPriceCurrency char(3)) per the EF money mapping rule; the
/// computed <c>LineTotal</c> projection is not persisted.
/// </summary>
public sealed class ItemConfiguration : IEntityTypeConfiguration<Item>
{
    public void Configure(EntityTypeBuilder<Item> builder)
    {
        builder.ToTable("CartItems", SchemaNames.Shop);
        builder.HasKey(x => x.Id);
        // The id is minted by Cart.AddItem, not the store. Leaving it store-generated (the Guid-PK
        // convention default) makes EF's graph paint a new line discovered on a tracked cart Modified
        // instead of Added — an UPDATE of 0 rows, surfacing as a spurious concurrency conflict.
        builder.Property(x => x.Id).ValueGeneratedNever();

        builder.Property(x => x.CartId).IsRequired();
        builder.Property(x => x.MerchantId).IsRequired(); // denormalized from Cart (rls-to-query-filter REQ-6)
        builder.Property(x => x.ProductId).IsRequired();
        builder.Property(x => x.Quantity).IsRequired();

        builder.ComplexProperty(x => x.UnitPrice, p =>
        {
            p.Property(m => m.Amount).HasColumnName("UnitPriceAmount").HasPrecision(19, 4);
            p.Property(m => m.Currency).HasColumnName("UnitPriceCurrency").HasMaxLength(3).IsFixedLength().IsUnicode(false);
        });

        builder.Ignore(x => x.LineTotal);
    }
}
