using BuildingBlocks.Application;
using Mediator;

namespace Orders.Application;

/// <summary>Producer-triggered resend of a customer's summary link: rotates the order's token and extends
/// its TTL, invalidating the old link (REQ-2.5). Tenant-scoped; RLS confines the lookup to the bound tenant.</summary>
public sealed record ResendOrderSummaryCommand(Guid OrderId, Guid TenantId)
    : ICommand<ResendOrderSummaryResult>, ITenantScoped;

public sealed record ResendOrderSummaryResult(string SummaryToken, DateTime ExpiresAtUtc);

public sealed class ResendOrderSummaryHandler : ICommandHandler<ResendOrderSummaryCommand, ResendOrderSummaryResult>
{
    private readonly IOrderRepository _orders;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;

    public ResendOrderSummaryHandler(IOrderRepository orders, IUnitOfWork unitOfWork, IClock clock)
    {
        _orders = orders;
        _unitOfWork = unitOfWork;
        _clock = clock;
    }

    public async ValueTask<ResendOrderSummaryResult> Handle(ResendOrderSummaryCommand command, CancellationToken cancellationToken)
    {
        var order = await _orders.GetAsync(command.OrderId, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException($"Order {command.OrderId} was not found.");

        order.ReissueSummary(_clock.UtcNow); // rejects a non-awaiting order -> InvalidOperationException -> 409
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new ResendOrderSummaryResult(order.SummaryToken, order.SummaryTokenExpiresAtUtc);
    }
}
