using Products.Domain;

namespace Products.Application;

/// <summary>
/// The central read model of one insurance document read live from the upstream
/// (products-external-source-of-truth REQ-3): a field-for-field mirror of the §5.2 result set of
/// <c>docs/reference/vcentralpay-sp-quick-reference.pdf</c> (all 32 fields, in document order), AFTER the mapper
/// has validated the row and parsed its wire enums. It carries no technical <c>Id</c> — this repo no longer mints
/// one (REQ-1.8/2.1); <see cref="DocumentNo"/> is the identifier.
/// <para>This is the one shape the whole buy path reads a document through: the product list projects it, add-item
/// prices from its <see cref="TotalPremium"/>, and checkout snapshots <see cref="DocumentNo"/>,
/// <see cref="ProductGroup"/>, <see cref="DocumentType"/>, <see cref="PolicyNumber"/>, <see cref="StartDate"/> and
/// <see cref="EndDate"/> from it (REQ-4.4).</para>
/// <para><see cref="DocumentNo"/>, <see cref="SaleCode"/>, <see cref="TotalPremium"/> and
/// <see cref="PaymentStatus"/> are non-nullable because the mapper drops any row that lacks them;
/// <see cref="ProductGroup"/>, <see cref="DocumentType"/> and <see cref="PaymentStatus"/> are CLR enums whose
/// <c>ToString()</c> is the wire value.</para>
/// </summary>
public sealed record DocumentView(
    ProductGroup ProductGroup,
    DocumentType DocumentType,
    string DocumentNo,
    string? PolicyYear,
    string? ReferenceBranch,
    string? ReferencePre,
    string? PolicySequenceNo,
    string? ReferenceYear,
    string? ReferenceNo,
    string? PolicyBranch,
    string? PolicyType,
    string SaleCode,
    string? SaleFullName,
    string? BrokerCode,
    string? BrokerName,
    string? PolicyNumber,
    string? ApplicationNumber,
    string? PreviousPolicyNumber,
    string? EndorsementNumber,
    DateTime? StartDate,
    DateTime? EndDate,
    string? ShowName,
    decimal? NetPremium,
    decimal? Stamp,
    decimal? TaxVat,
    decimal TotalPremium,
    decimal? CommissionPercent,
    decimal? CommissionAmount,
    DateTime? PaidDate,
    string? LicensePlateNumber,
    PaymentStatus PaymentStatus)
{
    /// <summary>§5.2 <c>InsuranceType</c> — derived from <see cref="ProductGroup"/>, never stored, exactly as the
    /// old <c>Product.InsuranceType</c> did.</summary>
    public InsuranceType InsuranceType =>
        ProductGroup is ProductGroup.CMI or ProductGroup.VMI ? InsuranceType.Motor : InsuranceType.NonMotor;
}
