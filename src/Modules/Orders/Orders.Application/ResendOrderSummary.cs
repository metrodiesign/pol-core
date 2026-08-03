using BuildingBlocks.Application;
using Contracts;
using Mediator;

namespace Orders.Application;

/// <summary>Merchant-user-triggered resend of a customer's summary link: rotates the order's token and extends
/// its TTL, invalidating the old link, and re-notifies the customer when a recipient was captured (REQ-2.5).
/// Merchant-scoped; RLS confines the lookup to the bound merchant.</summary>
public sealed record ResendOrderSummaryCommand(Guid OrderId, Guid MerchantId)
    : ICommand<ResendOrderSummaryResult>, IMerchantScoped;

public sealed record ResendOrderSummaryResult(string SummaryToken, DateTime ExpiresAt);

public sealed class ResendOrderSummaryHandler : ICommandHandler<ResendOrderSummaryCommand, ResendOrderSummaryResult>
{
    private readonly IOrderRepository _orders;
    private readonly IOutbox _outbox;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;

    public ResendOrderSummaryHandler(IOrderRepository orders, IOutbox outbox, IUnitOfWork unitOfWork, IClock clock)
    {
        _orders = orders;
        _outbox = outbox;
        _unitOfWork = unitOfWork;
        _clock = clock;
    }

    public async ValueTask<ResendOrderSummaryResult> Handle(ResendOrderSummaryCommand command, CancellationToken cancellationToken)
    {
        var order = await _orders.GetAsync(command.OrderId, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException($"Order {command.OrderId} was not found.");

        order.ReissueSummary(_clock.UtcNow); // rejects a non-awaiting order -> InvalidOperationException -> 409

        // Re-notify the customer with the rotated link, enqueued in the SAME unit of work as the rotation
        // (mirrors the create paths; the background worker delivers it). No stored recipient -> nothing to send.
        if (!string.IsNullOrWhiteSpace(order.NotificationRecipient))
            _outbox.Enqueue(new CustomerOrderNotification(
                order.MerchantId, order.Id, order.NotificationRecipient, order.SummaryToken, _clock.UtcNow,
                order.OrderNo));

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new ResendOrderSummaryResult(order.SummaryToken, order.SummaryTokenExpiresAt);
    }
}
