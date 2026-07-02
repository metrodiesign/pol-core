using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using BuildingBlocks.Application;
using Mediator;

namespace Products.Application;

/// <summary>
/// Scalar read model for the paged product list (the tenant-scoped SFS exemplar). Carries the price as two
/// scalar columns — NOT the computed <c>Product.Price</c> (<c>Money</c>), which EF cannot project in a
/// server-side <c>Select</c> (D15); reconstitute <c>Money.Of(PriceMinorUnits, PriceCurrency)</c> client-side if
/// a <c>Money</c> is needed. This is a NEW read model alongside <see cref="ProductView"/>, deliberately not a
/// redefinition of it (which would break <see cref="GetProductsHandler"/>).
/// </summary>
public sealed record ProductListItem(
    Guid Id, Guid TenantId, string Name, long PriceMinorUnits, string PriceCurrency, bool IsActive, DateTime CreatedAt);

/// <summary>
/// Optional strictly-validated filter surface for the product list (REQ-10). Parsed from the
/// <c>productFilters</c> JSON query param; malformed JSON or a broken DataAnnotations rule is a 400, not a
/// silent-drop (REQ-8.3). Coexists with the generic <c>filters[]</c> on the same query (REQ-10.3).
/// </summary>
public sealed record ProductFilterDto
{
    [Range(0, long.MaxValue)] public long? MinPriceMinorUnits { get; init; }
    [Range(0, long.MaxValue)] public long? MaxPriceMinorUnits { get; init; }
    public bool? ActiveOnly { get; init; }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Deserializes then DataAnnotations-validates the <c>productFilters</c> value. Absent -> null;
    /// malformed or invalid -> <see cref="ArgumentException"/> (mapped to 400 ProblemDetails). (REQ-10.1, REQ-8.3)</summary>
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
        return dto;
    }
}

/// <summary>
/// Lists a tenant's products with SFS (REQ-2, REQ-7). Tenant data -> <see cref="ITenantScoped"/>, so
/// <c>TenantGuardBehavior</c> rejects a request with no tenant context (REQ-7.2). <see cref="TenantId"/> is
/// bound from the authenticated principal by the endpoint, never supplied by the client.
/// </summary>
public sealed record ListProductsQuery : PagedQuery, IQuery<PagedResult<ProductListItem>>, ITenantScoped
{
    public required Guid TenantId { get; init; }
    public ProductFilterDto? ProductFilters { get; init; }
}

public sealed class ListProductsHandler(IProductRepository products)
    : IQueryHandler<ListProductsQuery, PagedResult<ProductListItem>>
{
    public async ValueTask<PagedResult<ProductListItem>> Handle(ListProductsQuery query, CancellationToken ct) =>
        await products.ListAsync(query, ct);
}
