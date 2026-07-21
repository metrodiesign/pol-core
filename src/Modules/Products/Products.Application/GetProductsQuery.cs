using BuildingBlocks.Application;
using Mediator;

namespace Products.Application;

/// <summary>Lists a merchant's products.</summary>
public sealed record GetProductsQuery(Guid MerchantId) : IQuery<IReadOnlyList<ProductView>>, IMerchantScoped;

/// <summary>Handles <see cref="GetProductsQuery"/>: projects the merchant's aggregates to read models.</summary>
public sealed class GetProductsHandler : IQueryHandler<GetProductsQuery, IReadOnlyList<ProductView>>
{
    private readonly IProductRepository _repository;

    public GetProductsHandler(IProductRepository repository) => _repository = repository;

    public async ValueTask<IReadOnlyList<ProductView>> Handle(GetProductsQuery query, CancellationToken cancellationToken)
    {
        var products = await _repository.ListByTenantAsync(query.MerchantId, cancellationToken);

        return products
            .Select(p => new ProductView(
                p.Id, p.MerchantId, p.Name, p.Price, p.SumInsured, p.CoverageDurationDays, p.Insurer, p.IsActive,
                p.CreatedAt))
            .ToList();
    }
}
