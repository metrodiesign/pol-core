using BuildingBlocks.Application;
using Mediator;

namespace Products.Application;

/// <summary>Reads one product for the bound merchant (RLS-scoped), or null if unknown / not this merchant's.
/// Used as the trusted price source when adding a catalog line to a cart (never trust a client price).</summary>
public sealed record GetProductByIdQuery(Guid MerchantId, Guid ProductId) : IQuery<ProductListItem?>, IMerchantScoped;

public sealed class GetProductByIdHandler : IQueryHandler<GetProductByIdQuery, ProductListItem?>
{
    private readonly IProductRepository _repository;

    public GetProductByIdHandler(IProductRepository repository) => _repository = repository;

    public async ValueTask<ProductListItem?> Handle(GetProductByIdQuery query, CancellationToken cancellationToken)
    {
        var product = await _repository.GetAsync(query.ProductId, cancellationToken);
        return product is null || product.MerchantId != query.MerchantId
            ? null
            : ProductListItem.From(product);
    }
}
