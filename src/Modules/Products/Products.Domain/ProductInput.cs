namespace Products.Domain;

/// <summary>
/// Primitive input for <see cref="Product.Create"/> and <see cref="Product.RefreshFromExternal"/> —
/// mirrors <c>OrderItemInput</c>'s reasoning for living in <c>Products.Domain</c> rather than
/// <c>Products.Application</c> (a Domain factory cannot take an Application-layer parameter type without
/// an illegal reverse project reference). Field set follows
/// <c>docs/reference/vcentralpay-sp-quick-reference.pdf</c> §2 (input parameters) and §5.2 (document
/// items); premium values are plain THB decimals with at most 2 decimal places.
/// <para><see cref="PaymentStatus"/> and <see cref="PaidDate"/> carry no default on purpose, which is why
/// they sit among the required parameters instead of at the end: an upstream row can arrive already PAID,
/// and a defaulted UNPAID would silently put a paid document back on sale. Every construction site has to
/// state the payment state it means.</para>
/// </summary>
public sealed record ProductInput(
    ProductGroup ProductGroup,
    DocumentType DocumentType,
    string DocumentNo,
    string SaleCode,
    decimal TotalPremium,
    PaymentStatus PaymentStatus,
    DateTime? PaidDate,
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
    decimal? NetPremium = null,
    decimal? Stamp = null,
    decimal? TaxVat = null,
    decimal? CommissionAmount = null,
    decimal? CommissionPercent = null);
