using BuildingBlocks.Application;
using Mediator;

namespace Products.Application;

/// <summary>Reads one document from the central catalogue, or null if unknown. The catalogue carries no
/// merchant of its own, so there is nothing to scope on here — the caller's authorization is the gate.
/// Used as the trusted price source when adding a catalog line to a cart (never trust a client price).</summary>
public sealed record GetProductByIdQuery(Guid ProductId) : IQuery<ProductListItem?>;

public sealed class GetProductByIdHandler : IQueryHandler<GetProductByIdQuery, ProductListItem?>
{
    private readonly IProductRepository _repository;

    public GetProductByIdHandler(IProductRepository repository) => _repository = repository;

    public async ValueTask<ProductListItem?> Handle(GetProductByIdQuery query, CancellationToken cancellationToken)
    {
        var product = await _repository.GetAsync(query.ProductId, cancellationToken);
        return product is null ? null : ProductListItem.From(product);
    }
}
