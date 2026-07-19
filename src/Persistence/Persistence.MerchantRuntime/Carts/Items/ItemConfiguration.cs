using Carts.Domain.Items;
using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Persistence.MerchantRuntime.Carts.Items;

// Runtime (scalar-only) mapping — mirrors Carts.Infrastructure.Items.ItemConfiguration exactly for
// column/index shape. CartId/ProductId stay bare scalars here (Item has no CLR nav to Product — it's
// a different module — and its Cart FK is wired from CartConfiguration's HasMany, not here).

internal sealed class ItemConfiguration(MerchantRuntimeDbContext context) : IEntityTypeConfiguration<Item>
{
    public void Configure(EntityTypeBuilder<Item> builder)
    {
        builder.ToTable("CartItems", SchemaNames.Shop);
        builder.HasKey(x => x.Id);

        builder.Property(x => x.CartId).IsRequired();
        builder.Property(x => x.MerchantId).IsRequired(); // denormalized from Cart (rls-to-query-filter REQ-6)
        builder.Property(x => x.ProductId).IsRequired();
        builder.Property(x => x.Quantity).IsRequired();

        TenantKeyDescriptor.Require(builder.Metadata, nameof(Item.MerchantId));
        builder.HasQueryFilter(x => x.MerchantId == context.CurrentMerchant);

        builder.ComplexProperty(x => x.UnitPrice, p =>
        {
            p.Property(m => m.Amount).HasColumnName("UnitPriceAmount").HasPrecision(19, 4);
            p.Property(m => m.Currency).HasColumnName("UnitPriceCurrency").HasMaxLength(3).IsFixedLength().IsUnicode(false);
        });

        builder.Ignore(x => x.LineTotal);
    }
}
