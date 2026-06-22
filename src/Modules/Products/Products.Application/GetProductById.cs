using BuildingBlocks.Application;
using Mediator;

namespace Products.Application;

/// <summary>Reads one product for the bound tenant (RLS-scoped), or null if unknown / not this tenant's.
/// Used as the trusted price source when adding a catalog line to a cart (never trust a client price).</summary>
public sealed record GetProductByIdQuery(Guid TenantId, Guid ProductId) : IQuery<ProductView?>, ITenantScoped;

public sealed class GetProductByIdHandler : IQueryHandler<GetProductByIdQuery, ProductView?>
{
    private readonly IProductRepository _repository;

    public GetProductByIdHandler(IProductRepository repository) => _repository = repository;

    public async ValueTask<ProductView?> Handle(GetProductByIdQuery query, CancellationToken cancellationToken)
    {
        var product = await _repository.GetAsync(query.ProductId, cancellationToken);
        return product is null || product.TenantId != query.TenantId
            ? null
            : new ProductView(product.Id, product.TenantId, product.Name, product.Price, product.IsActive, product.CreatedAtUtc);
    }
}
