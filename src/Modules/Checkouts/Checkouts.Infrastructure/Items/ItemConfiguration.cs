using Checkouts.Domain.Items;
using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Checkouts.Infrastructure.Items;

/// <summary>
/// Maps a checkout item into the <c>shop</c> schema. Mirrors <c>Carts.Infrastructure.Items.ItemConfiguration</c>
/// exactly for shape (insurance-pivot REQ-6.5).
/// </summary>
public sealed class ItemConfiguration : IEntityTypeConfiguration<Item>
{
    public void Configure(EntityTypeBuilder<Item> builder)
    {
        builder.ToTable("CheckoutSessionItems", SchemaNames.Shop);
        builder.HasKey(x => x.Id);

        builder.Property(x => x.SessionId).IsRequired();
        builder.Property(x => x.MerchantId).IsRequired(); // denormalized from Session
        builder.Property(x => x.ProductId).IsRequired();
        builder.Property(x => x.Quantity).IsRequired();

        builder.ComplexProperty(x => x.UnitPrice, p =>
        {
            p.Property(m => m.Amount).HasColumnName("UnitPriceAmount").HasPrecision(19, 4);
            p.Property(m => m.Currency).HasColumnName("UnitPriceCurrency").HasMaxLength(3).IsFixedLength().IsUnicode(false);
        });

        builder.Property(x => x.DocumentNo).HasMaxLength(150).IsRequired();
        builder.Property(x => x.ProductGroup).HasMaxLength(10).IsUnicode(false).IsRequired();
        builder.Property(x => x.DocumentType).HasMaxLength(20).IsUnicode(false).IsRequired();
        builder.Property(x => x.PolicyNumber).HasMaxLength(150).IsUnicode(false);
        builder.Property(x => x.StartDate).HasPrecision(0);
        builder.Property(x => x.EndDate).HasPrecision(0);

        builder.Property(x => x.InsuredFirstName).HasMaxLength(200).IsRequired();
        builder.Property(x => x.InsuredLastName).HasMaxLength(200).IsRequired();
        builder.Property(x => x.InsuredIdNumber).HasMaxLength(20).IsRequired();
        builder.Property(x => x.InsuredDateOfBirth).IsRequired();
    }
}
