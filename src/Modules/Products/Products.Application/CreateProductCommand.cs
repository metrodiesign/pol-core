using BuildingBlocks.Application;
using Mediator;
using Products.Domain;

namespace Products.Application;

/// <summary>Creates a sellable insurance document in the central catalogue and returns its new
/// identifier. Carries the domain <see cref="ProductInput"/> verbatim. Not <c>IMerchantScoped</c>:
/// the catalogue belongs to no merchant, so the endpoint's authorization is the only gate.</summary>
public sealed record CreateProductCommand(ProductInput Input) : ICommand<Guid>;

/// <summary>Handles <see cref="CreateProductCommand"/>: builds the aggregate and stages it for commit.</summary>
public sealed class CreateProductHandler : ICommandHandler<CreateProductCommand, Guid>
{
    private readonly IProductRepository _repository;
    private readonly IUnitOfWork _unitOfWork;

    public CreateProductHandler(IProductRepository repository, IUnitOfWork unitOfWork)
    {
        _repository = repository;
        _unitOfWork = unitOfWork;
    }

    public async ValueTask<Guid> Handle(CreateProductCommand command, CancellationToken cancellationToken)
    {
        var product = Product.Create(command.Input);

        _repository.Add(product);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return product.Id;
    }
}
