using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OrderItem = Orders.Domain.Items.Item;

namespace Persistence.MerchantRuntime.Orders.Items;

// Runtime (scalar-only) mapping — mirrors Orders.Infrastructure.Items.ItemConfiguration exactly for
// column/index shape (rls-to-query-filter design.md "Runtime EF config is scalar-only, separate from
// the migration-owner's relationship config"). The Order FK is wired from OrderConfiguration's HasMany,
// not here; the document is identified by DocumentNo, which has no table to point at.

internal sealed class ItemConfiguration(MerchantRuntimeDbContext context) : IEntityTypeConfiguration<OrderItem>
{
    public void Configure(EntityTypeBuilder<OrderItem> builder)
    {
        builder.ToTable("OrderItems", SchemaNames.Shop);
        builder.HasKey(x => x.Id);

        builder.Property(x => x.OrderId).IsRequired();
        builder.Property(x => x.MerchantId).IsRequired(); // denormalized from Order
        builder.Property(x => x.Quantity).IsRequired();
        builder.Property(x => x.Metadata).HasColumnType("json");

        TenantKeyDescriptor.Require(builder.Metadata, nameof(OrderItem.MerchantId));
        builder.HasQueryFilter(x => x.MerchantId == context.CurrentMerchant);

        builder.ComplexProperty(x => x.UnitPrice, p =>
        {
            p.Property(m => m.Amount).HasColumnName("UnitPriceAmount").HasPrecision(19, 4);
            p.Property(m => m.Currency).HasColumnName("UnitPriceCurrency").HasMaxLength(3).IsFixedLength().IsUnicode(false);
        });

        // Mirrors Orders.Infrastructure.Items.ItemConfiguration (purchase-flow-completion REQ-7.2).
        builder.ComplexProperty(x => x.Discount, p =>
        {
            p.Property(m => m.Amount).HasColumnName("DiscountAmount").HasPrecision(19, 4);
            p.Property(m => m.Currency).HasColumnName("DiscountCurrency").HasMaxLength(3).IsFixedLength().IsUnicode(false);
        });

        builder.Property(x => x.ProductCode).HasMaxLength(150).IsRequired();
        builder.Property(x => x.VariantCode).HasMaxLength(64).IsUnicode(false).IsRequired();
        builder.Property(x => x.VariantName).HasMaxLength(128);

        // Mirrors Orders.Infrastructure.Items.ItemConfiguration (products-external-source-of-truth REQ-5.15) —
        // the index DocumentSaleProbe's single read seeks.
        builder.HasIndex(x => x.ProductCode, "IX_OrderItems_ProductCode")
            .IncludeProperties(x => new { x.OrderId, x.VariantCode });
    }
}
