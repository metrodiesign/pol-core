using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using BuildingBlocks.Application;
using Mediator;
using Products.Domain;
using DomainPaymentStatus = Products.Domain.PaymentStatus;

namespace Products.Application;

/// <summary>
/// Read model for one insurance document — a field-for-field mirror of the §5.2 result set of
/// <c>docs/reference/vcentralpay-sp-quick-reference.pdf</c> (all 32 fields, in document order) plus
/// the technical key <c>Id</c>. Used by both the paged list and the single-document read.
/// <para>
/// Naming/typing deviations from §5.2, all deliberate (see the spec's "Deviation" section):
/// <c>ProductGroup</c> is §5.2's <c>SourceSystem</c> under its §2 parameter name <c>@ProductGroup</c>;
/// <c>DocumentNo</c>/<c>SaleCode</c>/<c>TotalPremium</c>/<c>PaymentStatus</c> stay non-nullable because
/// this repo owns the data (§5.2 is a read model of the upstream system); <c>ProductGroup</c>,
/// <c>DocumentType</c> and <c>PaymentStatus</c> stay CLR enums whose <c>ToString()</c> is the wire value.
/// <c>MerchantId</c> is not carried: it is not a §5.2 field and the caller already knows the merchant.
/// </para>
/// </summary>
public sealed record ProductListItem(
    Guid Id,
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
    DomainPaymentStatus PaymentStatus)
{
    /// <summary>§5.2 <c>InsuranceType</c> — derived from <see cref="ProductGroup"/> exactly as
    /// <c>Product.InsuranceType</c> does, so a repository projection never has to select it.</summary>
    public InsuranceType InsuranceType =>
        ProductGroup is ProductGroup.CMI or ProductGroup.VMI ? InsuranceType.Motor : InsuranceType.NonMotor;

    public static ProductListItem From(Product p) => new(
        p.Id, p.ProductGroup, p.DocumentType, p.DocumentNo, p.PolicyYear,
        p.ReferenceBranch, p.ReferencePre, p.PolicySequenceNo, p.ReferenceYear, p.ReferenceNo,
        p.PolicyBranch, p.PolicyType, p.SaleCode, p.SaleFullName, p.BrokerCode, p.BrokerName,
        p.PolicyNumber, p.ApplicationNumber, p.PreviousPolicyNumber, p.EndorsementNumber,
        p.StartDate, p.EndDate, p.ShowName,
        p.NetPremium, p.Stamp, p.TaxVat, p.TotalPremium, p.CommissionPercent, p.CommissionAmount,
        p.PaidDate, p.LicensePlateNumber, p.PaymentStatus);
}

/// <summary>
/// The required, strictly-validated filter surface for the document list, mirroring the §2 input rules
/// of <c>docs/reference/vcentralpay-sp-quick-reference.pdf</c> (minus paging, which
/// <see cref="ListProductsQuery"/> owns). Parsed from the <c>productFilters</c> JSON query param, which
/// is mandatory because <c>@SaleCode</c> is: an absent/blank value, malformed JSON or a broken rule is a
/// 400, not a silent-drop. Null members other than <see cref="SaleCode"/> mean ALL.
/// <para>
/// Two knowing deviations from §2: <c>@SaleCode</c> is taken from the client although the document puts it
/// in the server-side authorization context (user decision — the real tenant floor is <c>MerchantId</c>),
/// and <c>@BranchCode</c> is not supported at all because the <c>BranchCode</c> column is gone from §5.2.
/// A <c>branchCode</c> member in the JSON is therefore ignored (<see cref="JsonSerializerDefaults.Web"/>
/// does not error on unknown members).
/// </para>
/// </summary>
public sealed record ProductFilterDto
{
    /// <summary>§2 <c>@SaleCode</c> — required (SP error 50005), stored trimmed by <see cref="Parse"/>.</summary>
    [Required][MaxLength(20)] public string? SaleCode { get; init; }

    [MaxLength(100)] public string? SearchText { get; init; }
    [MaxLength(200)] public string? InsuredName { get; init; }
    [MaxLength(30)] public string? PolicyNo { get; init; }
    [MaxLength(30)] public string? ApplicationNo { get; init; }
    public DocumentType? DocumentType { get; init; }
    public ProductGroup? ProductGroup { get; init; }

    /// <summary>§2 <c>@PaymentStatus</c> on the wire: <c>UNPAID</c> | <c>PAID</c> | <c>ALL</c>,
    /// case-sensitive, absent = <c>UNPAID</c>. A string rather than the enum because <c>ALL</c> is not a
    /// <see cref="DomainPaymentStatus"/> member and must not become one (locked by the
    /// <c>checkout-chain-document-fields</c> spec). Read via <see cref="PaymentStatusFilter"/>.</summary>
    public string? PaymentStatus { get; init; }

    public DateOnly? CoverageStartFrom { get; init; }
    public DateOnly? CoverageStartTo { get; init; }
    public DateOnly? CoverageEndFrom { get; init; }
    public DateOnly? CoverageEndTo { get; init; }
    public DateTime? PaidDateFrom { get; init; }
    public DateTime? PaidDateTo { get; init; }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        // allowIntegerValues:false — a numeric enum token in productFilters is a 400, not a silent
        // out-of-contract value (matches the host-level converter).
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false) },
    };

    /// <summary>§2 <see cref="PaymentStatus"/> resolved to a filter: <c>null</c> means "do not filter"
    /// (wire <c>ALL</c>), absent means <see cref="DomainPaymentStatus.UNPAID"/>. Throws on any other value;
    /// <see cref="Parse"/> forces this read so the 400 happens at the boundary, not mid-query.</summary>
    public DomainPaymentStatus? PaymentStatusFilter
    {
        get
        {
            // Matched by name, not Enum.TryParse: TryParse also accepts the numeric forms ("0", "1")
            // and any other integer string, which would slip an undefined enum value into the query.
            return PaymentStatus switch
            {
                null => DomainPaymentStatus.UNPAID,
                "ALL" => null,
                nameof(DomainPaymentStatus.UNPAID) => DomainPaymentStatus.UNPAID,
                nameof(DomainPaymentStatus.PAID) => DomainPaymentStatus.PAID,
                _ => throw new ArgumentException("PaymentStatus must be UNPAID, PAID or ALL (SP error 50007)."),
            };
        }
    }

    /// <summary>Deserializes then validates the <c>productFilters</c> value, which is mandatory.
    /// Absent, blank, malformed or invalid -> <see cref="ArgumentException"/> (mapped to 400
    /// ProblemDetails by <c>ProblemDetailsExceptionHandler</c>; never <c>BadHttpRequestException</c>,
    /// which is an <see cref="IOException"/> and would surface as a 500).</summary>
    public static ProductFilterDto Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            throw new ArgumentException("productFilters is required (SaleCode is required — SP error 50005).");

        ProductFilterDto? dto;
        try { dto = JsonSerializer.Deserialize<ProductFilterDto>(raw, Json); }
        catch (JsonException ex) { throw new ArgumentException("Malformed productFilters.", ex); }
        if (dto is null)
            throw new ArgumentException("productFilters is required (SaleCode is required — SP error 50005).");

        // Before Validator, so a missing SaleCode cites 50005 instead of the generic invalid-filters message.
        var saleCode = dto.SaleCode?.Trim();
        if (string.IsNullOrEmpty(saleCode))
            throw new ArgumentException("SaleCode is required (SP error 50005).");
        dto = dto with { SaleCode = saleCode };

        _ = dto.PaymentStatusFilter;

        var results = new List<ValidationResult>();
        if (!Validator.TryValidateObject(dto, new ValidationContext(dto), results, validateAllProperties: true))
            throw new ArgumentException("Invalid productFilters.");

        if (dto.PaidDateFrom is { } paidFrom && dto.PaidDateTo is { } paidTo && paidFrom > paidTo)
            throw new ArgumentException("PaidDateFrom must not be after PaidDateTo (SP error 50003).");
        if (dto.CoverageStartFrom is { } startFrom && dto.CoverageStartTo is { } startTo && startFrom > startTo)
            throw new ArgumentException("CoverageStartFrom must not be after CoverageStartTo (SP error 50008).");
        if (dto.CoverageEndFrom is { } endFrom && dto.CoverageEndTo is { } endTo && endFrom > endTo)
            throw new ArgumentException("CoverageEndFrom must not be after CoverageEndTo (SP error 50009).");

        return dto;
    }
}

/// <summary>
/// Lists a merchant's insurance documents (§2 input surface). Merchant data -> <see cref="IMerchantScoped"/>,
/// so <c>MerchantGuardBehavior</c> rejects a request with no merchant context. <see cref="MerchantId"/> is
/// bound from the authenticated principal by the endpoint, never supplied by the client.
/// <para>
/// Deliberately does NOT inherit <see cref="PagedQuery"/>: §2 has no filter/sort/search concept, and
/// inheriting would leave settable <c>Filters</c>/<c>Sort</c>/<c>Search</c> that nothing reads.
/// </para>
/// </summary>
public sealed record ListProductsQuery : IQuery<PagedResult<ProductListItem>>, IMerchantScoped
{
    public required Guid MerchantId { get; init; }
    public required ProductFilterDto ProductFilters { get; init; }

    /// <summary>§2 <c>@PageNo</c>; clamped to >= 1 at the Hosts layer.</summary>
    public int Page { get; init; } = 1;

    /// <summary>§2 <c>@PageSize</c>; capped at 25 at the Hosts layer.</summary>
    public int Limit { get; init; } = 25;
}

public sealed class ListProductsHandler(IProductRepository products)
    : IQueryHandler<ListProductsQuery, PagedResult<ProductListItem>>
{
    public async ValueTask<PagedResult<ProductListItem>> Handle(ListProductsQuery query, CancellationToken ct) =>
        await products.ListAsync(query, ct);
}
