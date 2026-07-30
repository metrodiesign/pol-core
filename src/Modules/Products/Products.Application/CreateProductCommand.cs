using BuildingBlocks.Application;
using Mediator;
using Products.Domain;

namespace Products.Application;

/// <summary>Creates a sellable insurance document for a merchant and returns its new identifier.
/// Carries the domain <see cref="ProductInput"/> verbatim; the merchant scope is surfaced for
/// <c>MerchantGuardBehavior</c> via <see cref="MerchantId"/>.</summary>
public sealed record CreateProductCommand(ProductInput Input) : ICommand<Guid>, IMerchantScoped
{
    public Guid MerchantId => Input.MerchantId;
}

/// <summary>Handles <see cref="CreateProductCommand"/>: builds the aggregate and stages it for commit.</summary>
public sealed class CreateProductHandler : ICommandHandler<CreateProductCommand, Guid>
{
    private readonly IProductRepository _repository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;

    public CreateProductHandler(IProductRepository repository, IUnitOfWork unitOfWork, IClock clock)
    {
        _repository = repository;
        _unitOfWork = unitOfWork;
        _clock = clock;
    }

    public async ValueTask<Guid> Handle(CreateProductCommand command, CancellationToken cancellationToken)
    {
        var product = Product.Create(command.Input, _clock.UtcNow);

        _repository.Add(product);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return product.Id;
    }
}
