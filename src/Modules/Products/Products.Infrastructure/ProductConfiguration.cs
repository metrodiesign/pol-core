using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Products.Domain;

namespace Products.Infrastructure;

/// <summary>
/// EF mapping for <see cref="Product"/> (an insurance document,
/// <c>docs/reference/vcentralpay-sp-quick-reference.pdf</c> §5.2). Premium columns are plain
/// <c>decimal(19,2)</c> scalars, not <c>Money</c> — §5.2 carries no currency column (the source
/// system is THB-only), so currency is minted once at the cart boundary. Enum columns store the
/// uppercase wire values via string conversion. Discovered at model-build time by
/// <c>PolDbContext</c> via <c>HostModuleAssemblies.All</c>.
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

        builder.Property(x => x.TotalPremium).HasPrecision(19, 2).IsRequired();
        builder.Property(x => x.NetPremium).HasPrecision(19, 2);
        builder.Property(x => x.Stamp).HasPrecision(19, 2);
        builder.Property(x => x.TaxVat).HasPrecision(19, 2);
        builder.Property(x => x.CommissionAmount).HasPrecision(19, 2);
        builder.Property(x => x.CommissionPercent).HasPrecision(19, 6);
        builder.Ignore(x => x.InsuranceType);

        builder.Property(x => x.PaymentStatus).HasConversion<string>().HasMaxLength(10).IsUnicode(false).IsRequired();
        builder.Property(x => x.PaidDate).HasPrecision(0);

        // Named HasIndex overloads on purpose — a repeated anonymous HasIndex mutates instead of adds.
        builder.HasIndex(x => new { x.MerchantId, x.PaymentStatus }, "IX_Products_MerchantId_PaymentStatus");
        builder.HasIndex(x => new { x.MerchantId, x.DocumentNo }, "IX_Products_MerchantId_DocumentNo").IsUnique();
    }
}
