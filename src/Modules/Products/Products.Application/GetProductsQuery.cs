using BuildingBlocks.Application;
using Mediator;

namespace Products.Application;

/// <summary>Lists a tenant's products.</summary>
public sealed record GetProductsQuery(Guid TenantId) : IQuery<IReadOnlyList<ProductView>>, ITenantScoped;

/// <summary>Handles <see cref="GetProductsQuery"/>: projects the tenant's aggregates to read models.</summary>
public sealed class GetProductsHandler : IQueryHandler<GetProductsQuery, IReadOnlyList<ProductView>>
{
    private readonly IProductRepository _repository;

    public GetProductsHandler(IProductRepository repository) => _repository = repository;

    public async ValueTask<IReadOnlyList<ProductView>> Handle(GetProductsQuery query, CancellationToken cancellationToken)
    {
        var products = await _repository.ListByTenantAsync(query.TenantId, cancellationToken);

        return products
            .Select(p => new ProductView(p.Id, p.TenantId, p.Name, p.Price, p.IsActive, p.CreatedAtUtc))
            .ToList();
    }
}
