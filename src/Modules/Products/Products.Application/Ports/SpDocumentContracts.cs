using Products.Domain;

namespace Products.Application.Ports;

/// <summary>
/// The §2 input surface of <c>docs/reference/vcentralpay-sp-quick-reference.pdf</c>: 17 of the 18
/// procedure parameters, carried as the upstream spells them rather than as this repo's own types.
/// <para><c>@BranchCode</c> — the 18th — is deliberately absent: it is server-side per §1.1 and is filled
/// in by the adapter from <c>SpDocumentOptions</c>, which keeps options infrastructure out of this layer.
/// <see cref="Target"/> is not a procedure parameter at all but the routing key the server derives from
/// the caller's filters (CMI/VMI -> Motor, FIRE/MISC -> NonMotor), so the client cannot choose a
/// connection or a procedure.</para>
/// <para><see cref="PaymentStatus"/>, <see cref="DocumentType"/> and <see cref="ProductGroup"/> are wire
/// strings, not CLR enums, because <c>ALL</c> is a legal value of all three and is not — and must not
/// become — a member of the domain enums. Every date flows through naively (Thailand local time, no
/// conversion), which is the timezone contract this spec locked for the whole path.</para>
/// </summary>
public sealed record SpDocumentSearchRequest(
    InsuranceType Target,
    string SaleCode,
    string? SearchText,
    string? InsuredName,
    DateOnly? CoverageStartFrom,
    DateOnly? CoverageStartTo,
    DateOnly? CoverageEndFrom,
    DateOnly? CoverageEndTo,
    string PaymentStatus,
    string DocumentType,
    string ProductGroup,
    string? PolicyNo,
    string? ApplicationNo,
    DateTime? PaidDateFrom,
    DateTime? PaidDateTo,
    int PageNo,
    int PageSize,
    string CountMode);

/// <summary>
/// The input to a single-document lookup (products-external-source-of-truth REQ-3.1): the caller supplies the
/// <see cref="DocumentNo"/> to find and the <see cref="ProductGroup"/>, which the adapter uses ONLY to pick the
/// Motor vs Non-Motor procedure (CMI/VMI -> Motor, FIRE/MISC -> NonMotor, REQ-3.2). <see cref="SaleCode"/> is the
/// server-side sale code of the authenticated merchant user (REQ-3.1/4.8), never a client value.
/// <para>The value the caller passes here is not used to filter — the lookup runs with <c>@PaymentStatus = 'ALL'</c>
/// and <c>@ProductGroup = 'ALL'</c> so the row the upstream returns is the authoritative one, and its own
/// <c>ProductGroup</c> wins (REQ-3.5).</para>
/// </summary>
public sealed record SpDocumentLookupRequest(string DocumentNo, ProductGroup ProductGroup, string SaleCode);

/// <summary>
/// Result set 1 of §5.1, one row, always returned before the documents.
/// <para><see cref="TotalRows"/> and <see cref="TotalPages"/> are null under <c>@CountMode = 'FAST'</c>,
/// where the procedure skips the count on purpose; <see cref="HasNextPage"/> stays correct in both modes,
/// so it — not a total — is what tells a caller whether another page exists.</para>
/// </summary>
public sealed record SpPaginationMetadata(
    long? TotalRows,
    long? TotalPages,
    int PageNo,
    int PageSize,
    bool HasNextPage,
    bool HasPreviousPage,
    string CountMode,
    int SearchWindowMonths);

/// <summary>
/// One row of result set 2 of §5.2 — all 32 fields in document order, every one nullable because this is
/// what the upstream said, not what this repo requires. Rows too incomplete to become a
/// <c>Product</c> are dropped by the mapper, never repaired here.
/// <para><see cref="InsuranceType"/>, <see cref="SourceSystem"/>, <see cref="DocumentType"/> and
/// <see cref="PaymentStatus"/> stay raw strings so an unrecognised upstream value is a mapping decision
/// rather than a parse failure inside the adapter. <see cref="LicensePlateNumber"/> is Motor-only (§3);
/// the Non-Motor procedure returns the column as a constant null (§4).</para>
/// </summary>
public sealed record SpDocumentItem(
    string? InsuranceType,
    string? SourceSystem,
    string? DocumentType,
    string? DocumentNo,
    string? PolicyYear,
    string? ReferenceBranch,
    string? ReferencePre,
    string? PolicySequenceNo,
    string? ReferenceYear,
    string? ReferenceNo,
    string? PolicyBranch,
    string? PolicyType,
    string? SaleCode,
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
    decimal? TotalPremium,
    decimal? CommissionPercent,
    decimal? CommissionAmount,
    DateTime? PaidDate,
    string? LicensePlateNumber,
    string? PaymentStatus);

/// <summary>Both result sets of one §5 search: the §5.1 metadata and the §5.2 rows in the order the
/// procedure returned them (<c>ORDER BY DocumentNo</c>), which is the order the API answers in.</summary>
public sealed record SpDocumentSearchResult(
    SpPaginationMetadata Page,
    IReadOnlyList<SpDocumentItem> Items);
