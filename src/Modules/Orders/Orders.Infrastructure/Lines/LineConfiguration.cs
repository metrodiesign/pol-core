using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OrderLine = Orders.Domain.Lines.Line;

namespace Orders.Infrastructure.Lines;

/// <summary>
/// Maps an order line into the <c>shop</c> schema. Mirrors <c>Carts.Infrastructure.Items.ItemConfiguration</c>
/// exactly for shape (insurance-pivot REQ-6): <c>UnitPrice</c>/<c>SumInsured</c> are complex-type Money
/// (decimal(19,4) + char(3)) per the EF money mapping rule.
/// </summary>
public sealed class LineConfiguration : IEntityTypeConfiguration<OrderLine>
{
    public void Configure(EntityTypeBuilder<OrderLine> builder)
    {
        builder.ToTable("OrderLines", SchemaNames.Shop);
        builder.HasKey(x => x.Id);

        builder.Property(x => x.OrderId).IsRequired();
        builder.Property(x => x.MerchantId).IsRequired(); // denormalized from Order
        builder.Property(x => x.ProductId).IsRequired();
        builder.Property(x => x.Quantity).IsRequired();

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
