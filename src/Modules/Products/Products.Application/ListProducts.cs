using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using BuildingBlocks.Application;
using Mediator;
using Microsoft.Extensions.Logging;
using Products.Application.Ports;
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
/// <c>MerchantId</c> is not carried: the catalogue is central and §5.2 has no such field.
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
/// and <c>@BranchCode</c> is not client input at all — the adapter fills it from <c>SpDocumentOptions</c>
/// (REQ-6.6). A <c>branchCode</c> member in the JSON is therefore ignored
/// (<see cref="JsonSerializerDefaults.Web"/> does not error on unknown members).
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

    /// <summary>Which upstream procedure answers the search: <c>Motor</c> | <c>NonMotor</c> (REQ-6.1). The two
    /// catalogues cannot be merged into one page, so exactly one side is picked per request — from this member,
    /// or derived from <see cref="ProductGroup"/> when only that is given; the handler rejects a request that
    /// gives neither or gives two that disagree. Read case-insensitively like the other enum members here, not
    /// case-sensitively like <see cref="PaymentStatus"/>, which is a raw wire string.</summary>
    public InsuranceType? InsuranceType { get; init; }

    /// <summary>§2 <c>@CountMode</c> on the wire: <c>EXACT</c> | <c>FAST</c>, absent = <c>EXACT</c>. A string for
    /// the same reason as <see cref="PaymentStatus"/> — it is upstream vocabulary, not a Domain concept. Read via
    /// <see cref="CountModeValue"/>.</summary>
    public string? CountMode { get; init; }

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

    /// <summary>§2 <see cref="CountMode"/> resolved to the wire value sent to the procedure; absent means
    /// <c>EXACT</c>. Throws on any other value, and <see cref="Parse"/> forces this read so the 400 happens at
    /// the boundary rather than as SP error 50006 a round-trip later.</summary>
    public string CountModeValue => CountMode switch
    {
        null => "EXACT",
        "EXACT" or "FAST" => CountMode,
        _ => throw new ArgumentException("CountMode must be EXACT or FAST (SP error 50006)."),
    };

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
        _ = dto.CountModeValue;

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
/// One page of documents plus the §5.1 pagination envelope, copied value-by-value from what the upstream
/// procedure reported (REQ-8.1). Nothing is recounted here: the procedure owns the window, the filtering and
/// the totals, so recomputing them locally could only ever disagree with it. <see cref="TotalRows"/> and
/// <see cref="TotalPages"/> are null under <c>countMode=FAST</c>, where the procedure deliberately does not
/// count (REQ-8.2). <see cref="Items"/> may be shorter than <see cref="PageSize"/> — and than
/// <see cref="TotalRows"/> implies — when a row on the page could not become a document (REQ-7.7).
/// </summary>
public sealed record ProductPage(
    IReadOnlyList<ProductListItem> Items,
    long? TotalRows,
    long? TotalPages,
    int PageNo,
    int PageSize,
    bool HasNextPage,
    bool HasPreviousPage,
    string CountMode,
    int SearchWindowMonths);

/// <summary>
/// Lists insurance documents from the upstream catalogue (§2 input surface). Not <see cref="IMerchantScoped"/>:
/// the catalogue carries no merchant, so <c>SaleCode</c> — mandatory in <see cref="ProductFilterDto"/> — is
/// the only scoping axis, and the endpoint's authorization is the access gate.
/// <para>
/// Deliberately does NOT inherit <see cref="PagedQuery"/>: §2 has no filter/sort/search concept, and
/// inheriting would leave settable <c>Filters</c>/<c>Sort</c>/<c>Search</c> that nothing reads.
/// </para>
/// </summary>
public sealed record ListProductsQuery : IQuery<ProductPage>
{
    public required ProductFilterDto ProductFilters { get; init; }

    /// <summary>§2 <c>@PageNo</c>; clamped to >= 1 at the Hosts layer.</summary>
    public int Page { get; init; } = 1;

    /// <summary>§2 <c>@PageSize</c>; capped at 25 at the Hosts layer.</summary>
    public int Limit { get; init; } = 25;
}

/// <summary>
/// Searches the upstream catalogue live and mirrors what came back into <c>shop.Products</c> (REQ-7.1/7.2):
/// resolve which of the two procedures answers, translate the filter into wire values, search, map each row,
/// upsert the usable ones by <c>DocumentNo</c>, and answer with the local rows — which is what gives every
/// item the <c>Guid</c> the cart later adds (REQ-7.9). The order is the procedure's; nothing re-sorts it.
/// <para>
/// No <c>IUnitOfWork</c>: the upsert saves inside the repository, because a unique-index race has to be
/// retried with a reset change tracker, which only that layer can reach (M7).
/// </para>
/// </summary>
public sealed class ListProductsHandler(
    ISpDocumentGateway gateway, IProductRepository products, ILogger<ListProductsHandler> logger)
    : IQueryHandler<ListProductsQuery, ProductPage>
{
    public async ValueTask<ProductPage> Handle(ListProductsQuery query, CancellationToken ct)
    {
        var filters = query.ProductFilters;
        var (target, productGroup) = ResolveTarget(filters);

        // Absent means "everything" on the wire; absent paymentStatus means UNPAID, which is
        // PaymentStatusFilter's own default (null there = the client asked for ALL). Kept in a local
        // because the post-filter below decides from exactly this value — what the procedure was asked —
        // and nothing else.
        var paymentStatus = filters.PaymentStatusFilter?.ToString() ?? "ALL";

        var result = await gateway.SearchAsync(
            new SpDocumentSearchRequest(
                Target: target,
                SaleCode: filters.SaleCode!,
                SearchText: filters.SearchText,
                InsuredName: filters.InsuredName,
                CoverageStartFrom: filters.CoverageStartFrom,
                CoverageStartTo: filters.CoverageStartTo,
                CoverageEndFrom: filters.CoverageEndFrom,
                CoverageEndTo: filters.CoverageEndTo,
                PaymentStatus: paymentStatus,
                DocumentType: filters.DocumentType?.ToString() ?? "ALL",
                ProductGroup: productGroup,
                PolicyNo: filters.PolicyNo,
                ApplicationNo: filters.ApplicationNo,
                PaidDateFrom: filters.PaidDateFrom,
                PaidDateTo: filters.PaidDateTo,
                PageNo: query.Page,
                PageSize: query.Limit,
                CountMode: filters.CountModeValue),
            ct);

        var mapped = SpDocumentItemMapper.Map(result.Items);
        var inputs = new List<ProductInput>(mapped.Count);

        foreach (var row in mapped)
        {
            if (row.Input is not { } input)
            {
                // The page still answers on the rows it kept, and the procedure's totals still count the
                // dropped one — so this line is the only trace a bad upstream row leaves (REQ-7.7).
                logger.LogWarning("Products: skipped upstream document {DocumentNo} — {SkipReason}.",
                    row.Item.DocumentNo, row.SkipReason);
                continue;
            }

            if (input.PaymentStatus is DomainPaymentStatus.PAID && input.PaidDate is null)
                logger.LogWarning(
                    "Products: upstream document {DocumentNo} is PAID without a PaidDate; keeping the stored one.",
                    input.DocumentNo);

            inputs.Add(input);
        }

        var upserted = await products.UpsertByDocumentNoAsync(inputs, ct);
        var page = result.Page;

        // REQ-5.1 — the upstream is read-only to us, so it keeps listing documents this platform has
        // already sold. A search that asked for UNPAID drops the rows our own mirror says are PAID, which
        // is what stops a sold document being put back in a cart. The procedure's totals are left exactly
        // as it counted them (REQ-5.2): recounting here could only disagree with the paging it also owns.
        var sellable = paymentStatus == nameof(DomainPaymentStatus.UNPAID)
            ? upserted.Where(p => p.PaymentStatus is not DomainPaymentStatus.PAID)
            : upserted;

        return new ProductPage(
            [.. sellable.Select(ProductListItem.From)],
            page.TotalRows, page.TotalPages, page.PageNo, page.PageSize,
            page.HasNextPage, page.HasPreviousPage, page.CountMode, page.SearchWindowMonths);
    }

    /// <summary>Picks the side that answers, and the <c>@ProductGroup</c> that goes with it (REQ-6.1-6.4). The
    /// two catalogues are separate procedures with separate paging, so exactly one has to be chosen: a request
    /// naming neither side nor a group cannot be answered, and one naming both must have them agree.</summary>
    private static (InsuranceType Target, string ProductGroup) ResolveTarget(ProductFilterDto filters)
    {
        if (filters.ProductGroup is not { } group)
        {
            return filters.InsuranceType is { } side
                ? (side, "ALL")
                : throw new ArgumentException("insuranceType is required when productGroup is absent.");
        }

        var groupSide = group is Domain.ProductGroup.CMI or Domain.ProductGroup.VMI
            ? Domain.InsuranceType.Motor
            : Domain.InsuranceType.NonMotor;

        if (filters.InsuranceType is { } declared && declared != groupSide)
            throw new ArgumentException($"productGroup {group} is not a {declared} product group.");

        return (groupSide, group.ToString());
    }
}
