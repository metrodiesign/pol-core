using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Products.Domain;

namespace Persistence.MerchantRuntime.Products;

// Runtime (scalar-only) mapping — mirrors Products.Infrastructure.ProductConfiguration exactly for
// column/index shape (rls-to-query-filter design.md "Runtime EF config is scalar-only, separate from
// the migration-owner's relationship config"). No HasOne here — Product has none to begin with.
//
// Unlike every other entity in this context, Product carries NO tenant key and NO query filter: the
// document catalogue is central (§5.2 has no merchant field), shared by every merchant, and scoped
// per request by the mandatory SaleCode filter instead. Writes therefore reach IWriteAuthorizer with
// targetMerchant = Guid.Empty, which MerchantRequestWriteAuthorizer allows for its owned types.

internal sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> builder)
    {
        builder.ToTable("Products", SchemaNames.Shop);
        builder.HasKey(x => x.Id);

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

        builder.HasIndex(x => new { x.SaleCode, x.PaymentStatus }, "IX_Products_SaleCode_PaymentStatus");
        builder.HasIndex(x => x.DocumentNo, "IX_Products_DocumentNo").IsUnique();
    }
}
