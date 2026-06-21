using BuildingBlocks.Application;
using Mediator;
using Products.Domain;
using SharedKernel;

namespace Products.Application;

/// <summary>Creates a catalog product for a tenant and returns its new identifier.</summary>
public sealed record CreateProductCommand(Guid TenantId, string Name, long PriceMinorUnits, string Currency)
    : ICommand<Guid>, ITenantScoped;

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
        var price = Money.Of(command.PriceMinorUnits, command.Currency);
        var product = Product.Create(command.TenantId, command.Name, price, _clock.UtcNow);

        _repository.Add(product);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return product.Id;
    }
}
