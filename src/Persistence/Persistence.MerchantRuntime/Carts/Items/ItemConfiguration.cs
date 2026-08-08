using Carts.Domain.Items;
using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Persistence.MerchantRuntime.Carts.Items;

// Runtime (scalar-only) mapping — mirrors Carts.Infrastructure.Items.ItemConfiguration exactly for
// column/index shape. CartId stays a bare scalar here (the Cart FK is wired from CartConfiguration's
// HasMany, not here); the product is an external-source snapshot with no local product table.

internal sealed class ItemConfiguration(MerchantRuntimeDbContext context) : IEntityTypeConfiguration<Item>
{
    public void Configure(EntityTypeBuilder<Item> builder)
    {
        builder.ToTable("CartItems", SchemaNames.Shop);
        builder.HasKey(x => x.Id);
        // Mirrors Carts.Infrastructure.Items.ItemConfiguration: client-minted id, so EF paints a new
        // line on a tracked cart as Added (INSERT) instead of Modified (UPDATE of 0 rows -> 409).
        builder.Property(x => x.Id).ValueGeneratedNever();

        builder.Property(x => x.CartId).IsRequired();
        builder.Property(x => x.MerchantId).IsRequired(); // denormalized from Cart (rls-to-query-filter REQ-6)
        builder.Property(x => x.ProductCode).HasMaxLength(150).IsRequired();
        builder.Property(x => x.SaleCode).HasMaxLength(20).IsUnicode(false).IsRequired();
        builder.Property(x => x.VariantCode).HasMaxLength(64).IsUnicode(false).IsRequired();
        builder.Property(x => x.VariantName).HasMaxLength(128);
        builder.Property(x => x.Quantity).IsRequired();
        builder.Property(x => x.Metadata).HasColumnType("json");

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
