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

    public static ProductView From(Product p) => new(
        p.Id, p.MerchantId, p.ProductGroup, p.DocumentType, p.DocumentNo, p.PolicyYear,
        p.ReferenceBranch, p.ReferencePre, p.PolicySequenceNo, p.ReferenceYear, p.ReferenceNo,
        p.BranchCode, p.SaleCode, p.SaleFullName, p.BrokerCode, p.BrokerName, p.PolicyBranch, p.PolicyType,
        p.PolicyNumber, p.ApplicationNumber, p.PreviousPolicyNumber, p.EndorsementNumber,
        p.StartDate, p.EndDate, p.ShowName, p.LicensePlateNumber,
        p.TotalPremium, p.NetPremium, p.Stamp, p.TaxVat, p.CommissionPercent, p.CommissionAmount,
        p.PaymentStatus, p.PaidDate, p.IsActive, p.CreatedAt);
}
