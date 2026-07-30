using SharedKernel;

namespace Products.Domain;

/// <summary>
/// Primitive input for <see cref="Product.Create"/> — mirrors <c>OrderItemInput</c>'s reasoning for
/// living in <c>Products.Domain</c> rather than <c>Products.Application</c> (a Domain factory cannot
/// take an Application-layer parameter type without an illegal reverse project reference). Field set
/// follows the VCentralPay SP guide §2 (input parameters) and §5.2 (document items).
/// </summary>
public sealed record ProductInput(
    Guid MerchantId,
    ProductGroup ProductGroup,
    DocumentType DocumentType,
    string DocumentNo,
    string BranchCode,
    string SaleCode,
    Money TotalPremium,
    string? PolicyYear = null,
    string? ReferenceBranch = null,
    string? ReferencePre = null,
    string? PolicySequenceNo = null,
    string? ReferenceYear = null,
    string? ReferenceNo = null,
    string? PolicyBranch = null,
    string? PolicyType = null,
    string? SaleFullName = null,
    string? BrokerCode = null,
    string? BrokerName = null,
    string? PolicyNumber = null,
    string? ApplicationNumber = null,
    string? PreviousPolicyNumber = null,
    string? EndorsementNumber = null,
    DateTime? StartDate = null,
    DateTime? EndDate = null,
    string? ShowName = null,
    string? LicensePlateNumber = null,
    Money? NetPremium = null,
    Money? Stamp = null,
    Money? TaxVat = null,
    Money? CommissionAmount = null,
    decimal? CommissionPercent = null);
