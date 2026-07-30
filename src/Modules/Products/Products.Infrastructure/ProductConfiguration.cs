using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Products.Domain;

namespace Products.Infrastructure;

/// <summary>
/// EF mapping for <see cref="Product"/> (an insurance document, VCentralPay SP guide §5.2). Per the
/// Money mapping rule, <see cref="Product.TotalPremium"/> is a complex type onto two scalar columns
/// (decimal(19,4) + char(3)); the optional premium breakdown uses nullable Amount+Currency scalar
/// pairs (the <c>ItemPolicy</c> pattern — sidesteps the EF Core 10 optional-complex-type bug), with
/// the computed Money? properties explicitly ignored. Enum columns store the uppercase wire values
/// via string conversion. Discovered at model-build time by <c>PolDbContext</c> via
/// <c>HostModuleAssemblies.All</c>.
/// </summary>
public sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> builder)
    {
        builder.ToTable("Products", SchemaNames.Shop);
        builder.HasKey(x => x.Id);

        builder.Property(x => x.MerchantId).IsRequired();

        builder.Property(x => x.ProductGroup).HasConversion<string>().HasMaxLength(10).IsUnicode(false).IsRequired();
        builder.Property(x => x.DocumentType).HasConversion<string>().HasMaxLength(20).IsUnicode(false).IsRequired();
        builder.Property(x => x.DocumentNo).HasMaxLength(150).IsRequired();
        builder.Property(x => x.PolicyYear).HasMaxLength(2).IsUnicode(false);

        builder.Property(x => x.ReferenceBranch).HasMaxLength(3).IsUnicode(false);
        builder.Property(x => x.ReferencePre).HasMaxLength(20).IsUnicode(false);
        builder.Property(x => x.PolicySequenceNo).HasMaxLength(30).IsUnicode(false);
        builder.Property(x => x.ReferenceYear).HasMaxLength(2).IsUnicode(false);
        builder.Property(x => x.ReferenceNo).HasMaxLength(30).IsUnicode(false);

        builder.Property(x => x.BranchCode).HasMaxLength(3).IsUnicode(false).IsRequired();
        builder.Property(x => x.SaleCode).HasMaxLength(20).IsUnicode(false).IsRequired();
        builder.Property(x => x.SaleFullName).HasMaxLength(500);
        builder.Property(x => x.BrokerCode).HasMaxLength(20).IsUnicode(false);
        builder.Property(x => x.BrokerName).HasMaxLength(500);
        builder.Property(x => x.PolicyBranch).HasMaxLength(250);
        builder.Property(x => x.PolicyType).HasMaxLength(250);

        builder.Property(x => x.PolicyNumber).HasMaxLength(150).IsUnicode(false);
        builder.Property(x => x.ApplicationNumber).HasMaxLength(150).IsUnicode(false);
        builder.Property(x => x.PreviousPolicyNumber).HasMaxLength(150).IsUnicode(false);
        builder.Property(x => x.EndorsementNumber).HasMaxLength(150).IsUnicode(false);

        builder.Property(x => x.StartDate).HasPrecision(0);
        builder.Property(x => x.EndDate).HasPrecision(0);
        builder.Property(x => x.ShowName).HasMaxLength(500);
        builder.Property(x => x.LicensePlateNumber).HasMaxLength(100);

        builder.ComplexProperty(x => x.TotalPremium, p =>
        {
            p.Property(m => m.Amount).HasColumnName("TotalPremiumAmount").HasPrecision(19, 4);
            p.Property(m => m.Currency).HasColumnName("TotalPremiumCurrency").HasMaxLength(3).IsFixedLength().IsUnicode(false);
        });

        builder.Property(x => x.NetPremiumAmount).HasPrecision(19, 4);
        builder.Property(x => x.NetPremiumCurrency).HasMaxLength(3).IsFixedLength().IsUnicode(false);
        builder.Property(x => x.StampAmount).HasPrecision(19, 4);
        builder.Property(x => x.StampCurrency).HasMaxLength(3).IsFixedLength().IsUnicode(false);
        builder.Property(x => x.TaxVatAmount).HasPrecision(19, 4);
        builder.Property(x => x.TaxVatCurrency).HasMaxLength(3).IsFixedLength().IsUnicode(false);
        builder.Property(x => x.CommissionAmountAmount).HasColumnName("CommissionAmount").HasPrecision(19, 4);
        builder.Property(x => x.CommissionAmountCurrency).HasColumnName("CommissionCurrency").HasMaxLength(3).IsFixedLength().IsUnicode(false);
        builder.Property(x => x.CommissionPercent).HasPrecision(19, 6);
        builder.Ignore(x => x.NetPremium);
        builder.Ignore(x => x.Stamp);
        builder.Ignore(x => x.TaxVat);
        builder.Ignore(x => x.CommissionAmount);
        builder.Ignore(x => x.InsuranceType);

        builder.Property(x => x.PaymentStatus).HasConversion<string>().HasMaxLength(10).IsUnicode(false).IsRequired();
        builder.Property(x => x.PaidDate).HasPrecision(0);

        builder.Property(x => x.IsActive).IsRequired();
        builder.Property(x => x.CreatedAt).IsRequired();

        // Named HasIndex overloads on purpose — a repeated anonymous HasIndex mutates instead of adds.
        builder.HasIndex(x => new { x.MerchantId, x.IsActive }, "IX_Products_MerchantId_IsActive");
        builder.HasIndex(x => new { x.MerchantId, x.PaymentStatus }, "IX_Products_MerchantId_PaymentStatus");
        builder.HasIndex(x => new { x.MerchantId, x.DocumentNo }, "IX_Products_MerchantId_DocumentNo").IsUnique();
    }
}
