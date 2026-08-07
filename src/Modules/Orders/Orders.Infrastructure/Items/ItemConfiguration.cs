using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OrderItem = Orders.Domain.Items.Item;

namespace Orders.Infrastructure.Items;

/// <summary>
/// Maps an order item into the <c>shop</c> schema. Mirrors <c>Carts.Infrastructure.Items.ItemConfiguration</c>
/// exactly for shape (insurance-pivot REQ-6): <c>UnitPrice</c> is a complex-type Money
/// (decimal(19,4) + char(3)) per the EF money mapping rule.
/// </summary>
public sealed class ItemConfiguration : IEntityTypeConfiguration<OrderItem>
{
    public void Configure(EntityTypeBuilder<OrderItem> builder)
    {
        builder.ToTable("OrderItems", SchemaNames.Shop);
        builder.HasKey(x => x.Id);

        builder.Property(x => x.OrderId).IsRequired();
        builder.Property(x => x.MerchantId).IsRequired(); // denormalized from Order
        builder.Property(x => x.Quantity).IsRequired();
        builder.Property(x => x.Metadata).HasColumnType("json");

        builder.ComplexProperty(x => x.UnitPrice, p =>
        {
            p.Property(m => m.Amount).HasColumnName("UnitPriceAmount").HasPrecision(19, 4);
            p.Property(m => m.Currency).HasColumnName("UnitPriceCurrency").HasMaxLength(3).IsFixedLength().IsUnicode(false);
        });

        // purchase-flow-completion REQ-7.2 — carried from the checkout line, mapped like UnitPrice.
        builder.ComplexProperty(x => x.Discount, p =>
        {
            p.Property(m => m.Amount).HasColumnName("DiscountAmount").HasPrecision(19, 4);
            p.Property(m => m.Currency).HasColumnName("DiscountCurrency").HasMaxLength(3).IsFixedLength().IsUnicode(false);
        });

        builder.Property(x => x.ProductCode).HasMaxLength(150).IsRequired();
        builder.Property(x => x.VariantCode).HasMaxLength(64).IsUnicode(false).IsRequired();
        builder.Property(x => x.VariantName).HasMaxLength(128);

        // products-external-source-of-truth REQ-5.15: the sold-check probes up to 25 document numbers in one
        // read, and DocumentNo is its only IN predicate. Covering OrderId + ProductGroup keeps that read off
        // the clustered index entirely. NOT unique on purpose — a cancelled order and the order that really
        // sells the document may both hold it (design decision #4).
        builder.HasIndex(x => x.ProductCode, "IX_OrderItems_ProductCode")
            .IncludeProperties(x => new { x.OrderId, x.VariantCode });
    }
}
