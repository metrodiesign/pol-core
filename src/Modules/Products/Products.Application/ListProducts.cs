using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using BuildingBlocks.Application;
using Mediator;
using Products.Domain;
using SharedKernel;

namespace Products.Application;

/// <summary>
/// Read model for the paged insurance-document list (the merchant-scoped SFS exemplar), projected
/// server-side via EF complex-type selection. Deliberately a slim subset of <see cref="ProductView"/>.
/// </summary>
public sealed record ProductListItem(
    Guid Id, Guid MerchantId, string DocumentNo, DocumentType DocumentType, ProductGroup ProductGroup,
    string? ShowName, string? PolicyNumber, DateTime? StartDate, DateTime? EndDate, Money TotalPremium,
    PaymentStatus PaymentStatus, DateTime? PaidDate, bool IsActive, DateTime CreatedAt);

/// <summary>
/// Optional strictly-validated filter surface for the document list, mirroring the shared input rules
/// of the VCentralPay SP guide §2 (minus paging/count, which <see cref="PagedQuery"/> owns, and minus
/// BranchCode/SaleCode, which are an authorization scope — never client input). Parsed from the
/// <c>productFilters</c> JSON query param; malformed JSON or a broken rule is a 400, not a silent-drop.
/// Null enum members mean ALL.
/// </summary>
public sealed record ProductFilterDto
{
    [MaxLength(100)] public string? SearchText { get; init; }
    [MaxLength(200)] public string? InsuredName { get; init; }
    [MaxLength(30)] public string? PolicyNo { get; init; }
    [MaxLength(30)] public string? ApplicationNo { get; init; }
    public DocumentType? DocumentType { get; init; }
    public ProductGroup? ProductGroup { get; init; }
    public PaymentStatus? PaymentStatus { get; init; }
    public DateOnly? CoverageStartFrom { get; init; }
    public DateOnly? CoverageStartTo { get; init; }
    public DateOnly? CoverageEndFrom { get; init; }
    public DateOnly? CoverageEndTo { get; init; }
    public DateTime? PaidDateFrom { get; init; }
    public DateTime? PaidDateTo { get; init; }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    /// <summary>Deserializes then validates the <c>productFilters</c> value. Absent -> null;
    /// malformed or invalid -> <see cref="ArgumentException"/> (mapped to 400 ProblemDetails).</summary>
    public static ProductFilterDto? Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        ProductFilterDto? dto;
        try { dto = JsonSerializer.Deserialize<ProductFilterDto>(raw, Json); }
        catch (JsonException ex) { throw new ArgumentException("Malformed productFilters.", ex); }
        if (dto is null) return null;

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
/// Lists a merchant's insurance documents with SFS. Merchant data -> <see cref="IMerchantScoped"/>, so
/// <c>MerchantGuardBehavior</c> rejects a request with no merchant context. <see cref="MerchantId"/> is
/// bound from the authenticated principal by the endpoint, never supplied by the client.
/// </summary>
public sealed record ListProductsQuery : PagedQuery, IQuery<PagedResult<ProductListItem>>, IMerchantScoped
{
    public required Guid MerchantId { get; init; }
    public ProductFilterDto? ProductFilters { get; init; }
}

public sealed class ListProductsHandler(IProductRepository products)
    : IQueryHandler<ListProductsQuery, PagedResult<ProductListItem>>
{
    public async ValueTask<PagedResult<ProductListItem>> Handle(ListProductsQuery query, CancellationToken ct) =>
        await products.ListAsync(query, ct);
}
