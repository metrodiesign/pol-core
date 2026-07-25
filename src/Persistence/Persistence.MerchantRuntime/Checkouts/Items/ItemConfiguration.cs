using Checkouts.Domain.Items;
using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Persistence.MerchantRuntime.Checkouts.Items;

// Runtime (scalar-only) mapping — mirrors Checkouts.Infrastructure.Items.ItemConfiguration exactly for
// column/index shape. ProductId stays a bare scalar (no CLR nav to Product).

internal sealed class ItemConfiguration(MerchantRuntimeDbContext context) : IEntityTypeConfiguration<Item>
{
    public void Configure(EntityTypeBuilder<Item> builder)
    {
        builder.ToTable("CheckoutSessionItems", SchemaNames.Shop);
        builder.HasKey(x => x.Id);

        builder.Property(x => x.SessionId).IsRequired();
        builder.Property(x => x.MerchantId).IsRequired(); // denormalized from Session
        builder.Property(x => x.ProductId).IsRequired();
        builder.Property(x => x.Quantity).IsRequired();

        TenantKeyDescriptor.Require(builder.Metadata, nameof(Item.MerchantId));
        builder.HasQueryFilter(x => x.MerchantId == context.CurrentMerchant);

        builder.ComplexProperty(x => x.UnitPrice, p =>
        {
            p.Property(m => m.Amount).HasColumnName("UnitPriceAmount").HasPrecision(19, 4);
            p.Property(m => m.Currency).HasColumnName("UnitPriceCurrency").HasMaxLength(3).IsFixedLength().IsUnicode(false);
        });

        builder.ComplexProperty(x => x.SumInsured, p =>
        {
            p.Property(m => m.Amount).HasColumnName("SumInsuredAmount").HasPrecision(19, 4);
            p.Property(m => m.Currency).HasColumnName("SumInsuredCurrency").HasMaxLength(3).IsFixedLength().IsUnicode(false);
        });
        builder.Property(x => x.CoverageDurationDays).IsRequired();
        builder.Property(x => x.Insurer).HasColumnName("InsurerName").HasMaxLength(200).IsRequired();

        builder.Property(x => x.InsuredFirstName).HasMaxLength(200).IsRequired();
        builder.Property(x => x.InsuredLastName).HasMaxLength(200).IsRequired();
        builder.Property(x => x.InsuredIdNumber).HasMaxLength(20).IsRequired();
        builder.Property(x => x.InsuredDateOfBirth).IsRequired();
    }
}
