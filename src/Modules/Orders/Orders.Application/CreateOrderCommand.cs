using BuildingBlocks.Application;
using Mediator;
using Orders.Domain;
using SharedKernel;

namespace Orders.Application;

/// <summary>
/// Opens a new order awaiting payment for the active tenant. Tenant-scoped: rejected by the tenant
/// guard if no tenant is bound to the request (PLAN decision #4). The amount is the money seam —
/// minor units + ISO 4217 code — validated by <see cref="Money.Of"/>.
/// </summary>
public sealed record CreateOrderCommand(Guid TenantId, long AmountMinorUnits, string Currency)
    : ICommand<CreateOrderResult>, ITenantScoped;

/// <summary>The identity of the newly created order.</summary>
public sealed record CreateOrderResult(Guid OrderId);

/// <summary>Handles <see cref="CreateOrderCommand"/>: builds the aggregate and commits it through
/// the unit of work. Scoped — depends on the Scoped repository/unit-of-work.</summary>
public sealed class CreateOrderHandler : ICommandHandler<CreateOrderCommand, CreateOrderResult>
{
    private readonly IOrderRepository _orders;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;

    public CreateOrderHandler(IOrderRepository orders, IUnitOfWork unitOfWork, IClock clock)
    {
        _orders = orders;
        _unitOfWork = unitOfWork;
        _clock = clock;
    }

    public async ValueTask<CreateOrderResult> Handle(CreateOrderCommand command, CancellationToken cancellationToken)
    {
        var amount = Money.Of(command.AmountMinorUnits, command.Currency);
        var order = Order.Create(command.TenantId, amount, _clock.UtcNow);

        _orders.Add(order);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new CreateOrderResult(order.Id);
    }
}
