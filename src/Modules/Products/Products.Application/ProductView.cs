using Products.Domain;
using SharedKernel;

namespace Products.Application;

/// <summary>Read model for a single insurance document (VCentralPay SP guide §5.2).</summary>
public sealed record ProductView(
    Guid ProductId,
    Guid MerchantId,
    ProductGroup ProductGroup,
    DocumentType DocumentType,
    string DocumentNo,
    string? PolicyYear,
    string? ReferenceBranch,
    string? ReferencePre,
    string? PolicySequenceNo,
    string? ReferenceYear,
    string? ReferenceNo,
    string BranchCode,
    string SaleCode,
    string? SaleFullName,
    string? BrokerCode,
    string? BrokerName,
    string? PolicyBranch,
    string? PolicyType,
    string? PolicyNumber,
    string? ApplicationNumber,
    string? PreviousPolicyNumber,
    string? EndorsementNumber,
    DateTime? StartDate,
    DateTime? EndDate,
    string? ShowName,
    string? LicensePlateNumber,
    Money TotalPremium,
    Money? NetPremium,
    Money? Stamp,
    Money? TaxVat,
    decimal? CommissionPercent,
    Money? CommissionAmount,
    PaymentStatus PaymentStatus,
    DateTime? PaidDate,
    bool IsActive,
    DateTime CreatedAt)
{
    public InsuranceType InsuranceType =>
        ProductGroup is ProductGroup.CMI or ProductGroup.VMI ? InsuranceType.Motor : InsuranceType.NonMotor;

    /// <summary>Selling price — the total premium. Kept under the old name for the cart flow.</summary>
    public Money Price => TotalPremium;

    // ponytail: bridge — the SP contract carries no sum-insured; replaced for real when the
    // checkout snapshot chain is reworked to document fields.
    public Money SumInsured => TotalPremium;

    // ponytail: bridge for the legacy checkout snapshot — derived from the coverage window.
    public int CoverageDurationDays =>
        StartDate is { } start && EndDate is { } end && (end - start).Days > 0 ? (end - start).Days : 1;

    // ponytail: bridge for the legacy checkout snapshot — best available issuer-ish label.
    public string Insurer => BrokerName ?? ProductGroup.ToString();

    public static ProductView From(Product p) => new(
        p.Id, p.MerchantId, p.ProductGroup, p.DocumentType, p.DocumentNo, p.PolicyYear,
        p.ReferenceBranch, p.ReferencePre, p.PolicySequenceNo, p.ReferenceYear, p.ReferenceNo,
        p.BranchCode, p.SaleCode, p.SaleFullName, p.BrokerCode, p.BrokerName, p.PolicyBranch, p.PolicyType,
        p.PolicyNumber, p.ApplicationNumber, p.PreviousPolicyNumber, p.EndorsementNumber,
        p.StartDate, p.EndDate, p.ShowName, p.LicensePlateNumber,
        p.TotalPremium, p.NetPremium, p.Stamp, p.TaxVat, p.CommissionPercent, p.CommissionAmount,
        p.PaymentStatus, p.PaidDate, p.IsActive, p.CreatedAt);
}
