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
/// One insurance document as the catalogue list returns it — a field-for-field mirror of the §5.2 result set of
/// <c>docs/reference/vcentralpay-sp-quick-reference.pdf</c> (all 32 fields, in document order), projected from the
/// central <see cref="DocumentView"/> the mapper produced. It carries NO technical <c>Id</c>: this repo no longer
/// mints a surrogate for a document (products-external-source-of-truth REQ-1.8/2.1); <see cref="DocumentNo"/> is
/// the identifier. <see cref="SoldByPlatform"/> is the one field added on top of §5.2 (REQ-5.9): whether an order
/// on THIS platform has already paid for the document, computed from Orders, without touching the upstream's own
/// <see cref="PaymentStatus"/>.
/// </summary>
public sealed record ProductListItem(
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
    DomainPaymentStatus PaymentStatus,
    bool SoldByPlatform)
{
    /// <summary>Stable cart identifier derived from the upstream document number.</summary>
    public string ProductCode => DocumentNo;

    /// <summary>Stable cart variant derived from the upstream product group.</summary>
    public string VariantCode => ProductGroup.ToString();

    /// <summary>§5.2 <c>InsuranceType</c> — derived from <see cref="ProductGroup"/>, never stored.</summary>
    public InsuranceType InsuranceType =>
        ProductGroup is ProductGroup.CMI or ProductGroup.VMI ? InsuranceType.Motor : InsuranceType.NonMotor;

    public static ProductListItem From(DocumentView v, bool soldByPlatform) => new(
        v.ProductGroup, v.DocumentType, v.DocumentNo, v.PolicyYear,
        v.ReferenceBranch, v.ReferencePre, v.PolicySequenceNo, v.ReferenceYear, v.ReferenceNo,
        v.PolicyBranch, v.PolicyType, v.SaleCode, v.SaleFullName, v.BrokerCode, v.BrokerName,
        v.PolicyNumber, v.ApplicationNumber, v.PreviousPolicyNumber, v.EndorsementNumber,
        v.StartDate, v.EndDate, v.ShowName,
        v.NetPremium, v.Stamp, v.TaxVat, v.TotalPremium, v.CommissionPercent, v.CommissionAmount,
        v.PaidDate, v.LicensePlateNumber, v.PaymentStatus, soldByPlatform);
}

/// <summary>
/// The validated filter surface for the document list, mirroring the §2 input rules of
/// <c>docs/reference/vcentralpay-sp-quick-reference.pdf</c> (minus paging, which <see cref="ListProductsQuery"/>
/// owns, and minus <c>@SaleCode</c>, which is server-side now — the merchant user's own code, never a client field,
/// products-external-source-of-truth REQ-4.8). Parsed from the <c>productFilters</c> JSON query param: absent or
/// blank is not a 400 <em>here</em> — it deserializes to "every default" — but the handler still needs a catalogue
/// side, so a request that names neither <see cref="ProductGroup"/> nor <see cref="InsuranceType"/> is rejected at
/// <c>ResolveTarget</c> (REQ-3.2, no default side). Dropping <c>saleCode</c> as a member (REQ-4.8) removed the only
/// member the client had to send, but it did not make the blob optional at the endpoint. Null members mean ALL.
/// Malformed JSON or a broken rule is still a 400.
/// <para>
/// One knowing deviation from §2: <c>@BranchCode</c> is not client input — the adapter fills it from
/// <c>SpDocumentOptions</c>. A <c>branchCode</c> member in the JSON is therefore ignored
/// (<see cref="JsonSerializerDefaults.Web"/> does not error on unknown members), and so is a stray
/// <c>saleCode</c> member (REQ-4.8 — the client cannot choose it).
/// </para>
/// </summary>
public sealed record ProductFilterDto
{
    [MaxLength(100)] public string? SearchText { get; init; }
    [MaxLength(200)] public string? InsuredName { get; init; }
    [MaxLength(30)] public string? PolicyNo { get; init; }
    [MaxLength(30)] public string? ApplicationNo { get; init; }
    public DocumentType? DocumentType { get; init; }
    public ProductGroup? ProductGroup { get; init; }

    /// <summary>Which upstream procedure answers the search: <c>Motor</c> | <c>NonMotor</c> (REQ-3.2). The two
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

    /// <summary>Deserializes then validates the optional <c>productFilters</c> value. Absent or blank ->
    /// every default (all filters open); malformed or invalid -> <see cref="ArgumentException"/> (mapped to 400
    /// ProblemDetails by <c>ProblemDetailsExceptionHandler</c>; never <c>BadHttpRequestException</c>,
    /// which is an <see cref="IOException"/> and would surface as a 500).</summary>
    public static ProductFilterDto Parse(string? raw)
    {
        // productFilters is no longer mandatory: saleCode was the only member that made it required, and that
        // moved server-side (REQ-4.8). An absent blob is "all defaults".
        if (string.IsNullOrWhiteSpace(raw))
            return new ProductFilterDto();

        ProductFilterDto? dto;
        try { dto = JsonSerializer.Deserialize<ProductFilterDto>(raw, Json); }
        catch (JsonException ex) { throw new ArgumentException("Malformed productFilters.", ex); }
        dto ??= new ProductFilterDto();

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
/// procedure reported. Nothing is recounted here: the procedure owns the window, the filtering and
/// the totals, so recomputing them locally could only ever disagree with it (REQ-1.4/1.5). <see cref="TotalRows"/>
/// and <see cref="TotalPages"/> are null under <c>countMode=FAST</c>, where the procedure deliberately does not
/// count. <see cref="Items"/> may be shorter than <see cref="PageSize"/> — and than <see cref="TotalRows"/>
/// implies — when a row on the page could not become a document (REQ-1.6) or was dropped as already sold (REQ-5.8).
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
/// Lists insurance documents live from the upstream catalogue (§2 input surface, products-external-source-of-truth
/// REQ-1). Not <see cref="IMerchantScoped"/>: the catalogue carries no merchant, so <see cref="SaleCode"/> — the
/// authenticated merchant user's own code, set by the endpoint from the actor (REQ-4.8), never by the client — is
/// the only scoping axis, and the endpoint's authorization is the access gate.
/// <para>
/// Deliberately does NOT inherit <c>PagedQuery</c>: §2 has no filter/sort/search concept, and inheriting would
/// leave settable <c>Filters</c>/<c>Sort</c>/<c>Search</c> that nothing reads.
/// </para>
/// </summary>
public sealed record ListProductsQuery : IQuery<ProductPage>
{
    /// <summary>§2 <c>@SaleCode</c> — the merchant user's own sale code, supplied by the endpoint from the
    /// authenticated actor (REQ-4.8). The client never sets it.</summary>
    public required string SaleCode { get; init; }

    public required ProductFilterDto ProductFilters { get; init; }

    /// <summary>§2 <c>@PageNo</c>; clamped to >= 1 at the Hosts layer.</summary>
    public int Page { get; init; } = 1;

    /// <summary>§2 <c>@PageSize</c>; capped at 25 at the Hosts layer.</summary>
    public int Limit { get; init; } = 25;
}

/// <summary>
/// Answers the document list from two live reads and NOTHING it stores: the upstream procedure for the documents
/// (REQ-1.1) and <see cref="IDocumentSaleProbe"/> — one read for the whole page (REQ-5.15) — for which of them an
/// order on this platform has already sold. There is no repository and no <c>SaveChanges</c>: the catalogue is
/// read-only now (REQ-1.2), so a document is never mirrored locally. The order is the procedure's; nothing
/// re-sorts it (REQ-1.4), and the totals are exactly the ones it reported (REQ-1.5).
/// </summary>
public sealed class ListProductsHandler(
    ISpDocumentGateway gateway, IDocumentSaleProbe documentSales, ILogger<ListProductsHandler> logger)
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
                SaleCode: query.SaleCode,
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

        var views = new List<DocumentView>(result.Items.Count);
        foreach (var row in SpDocumentItemMapper.Map(result.Items))
        {
            if (row.View is not { } view)
            {
                // The page still answers on the rows it kept, and the procedure's totals still count the
                // dropped one — so this line is the only trace a bad upstream row leaves (REQ-1.6/1.7).
                logger.LogWarning("Products: skipped upstream document {DocumentNo} — {SkipReason}.",
                    row.Item.DocumentNo, row.SkipReason);
                continue;
            }

            if (view.PaymentStatus is DomainPaymentStatus.PAID && view.PaidDate is null)
                logger.LogWarning(
                    "Products: upstream document {DocumentNo} is PAID without a PaidDate.", view.DocumentNo);

            views.Add(view);
        }

        // REQ-5.1/5.9/5.15 — the upstream is read-only to us, so it keeps listing documents this platform has
        // already sold. One probe read for the whole page (never one per row) tells which documents an order
        // here holds. Sold = an order in status Paid carries the (DocumentNo, ProductGroup) pair (REQ-5.1); the
        // in-flight case is not "sold through this platform" and is not flagged.
        var keys = views.Select(v => new DocumentKey(v.DocumentNo, v.ProductGroup.ToString())).ToArray();
        var statuses = keys.Length == 0
            ? []
            : await documentSales.ProbeAsync(keys, ct);
        var soldKeys = statuses
            .Where(s => s.State == DocumentSaleState.Sold)
            .Select(s => s.Key)
            .ToHashSet();

        // REQ-5.8 — a search that asked for UNPAID (the default) drops the documents already sold on this
        // platform, which is what stops a sold document being put back in a cart. The procedure's totals are
        // left exactly as it counted them (REQ-5.5): recounting here could only disagree with the paging it
        // also owns. An ALL/PAID search keeps every row and just flags it (REQ-5.9).
        var dropSold = paymentStatus == nameof(DomainPaymentStatus.UNPAID);

        var items = new List<ProductListItem>(views.Count);
        foreach (var view in views)
        {
            var sold = soldKeys.Contains(new DocumentKey(view.DocumentNo, view.ProductGroup.ToString()));
            if (sold && dropSold)
                continue;
            items.Add(ProductListItem.From(view, sold));
        }

        var page = result.Page;
        return new ProductPage(
            items,
            page.TotalRows, page.TotalPages, page.PageNo, page.PageSize,
            page.HasNextPage, page.HasPreviousPage, page.CountMode, page.SearchWindowMonths);
    }

    /// <summary>Picks the side that answers, and the <c>@ProductGroup</c> that goes with it (REQ-3.2). The
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
