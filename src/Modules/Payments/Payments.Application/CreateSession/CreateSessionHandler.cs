using BuildingBlocks.Application;
using Mediator;
using Payments.Application.Ports;
using Payments.Application.Ports.Psp;
using Payments.Domain;

namespace Payments.Application.CreateSession;

/// <summary>
/// Persists a new <see cref="Session"/> in the <see cref="SessionStatus.Created"/> state, priced from the
/// order and only for a channel this merchant's connection AND our adapter can actually charge.
///
/// Every check lives here rather than at the endpoint because the endpoint is only today's single entry
/// point, while "the amount is the order's own" is an invariant every caller must pass. The order of the
/// checks is itself contract: it decides which status code a caller sees (400 malformed method, 404 unknown
/// order, 409 for every server-state refusal), so it must not be rearranged.
/// </summary>
public sealed class CreateSessionHandler
    : ICommandHandler<CreateSessionCommand, CreateSessionResult>
{
    private readonly IPayableOrderReader _orders;
    private readonly IConnectionRepository _connections;
    private readonly IPspAdapterFactory _adapters;
    private readonly ISessionRepository _sessions;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;

    public CreateSessionHandler(
        IPayableOrderReader orders,
        IConnectionRepository connections,
        IPspAdapterFactory adapters,
        ISessionRepository sessions,
        IUnitOfWork unitOfWork,
        IClock clock)
    {
        _orders = orders;
        _connections = connections;
        _adapters = adapters;
        _sessions = sessions;
        _unitOfWork = unitOfWork;
        _clock = clock;
    }

    public async ValueTask<CreateSessionResult> Handle(
        CreateSessionCommand command,
        CancellationToken cancellationToken)
    {
        // Malformed client input (400) — distinct from a method the server merely has not enabled (409).
        var method = PaymentMethods.Normalize(command.Method);

        // Invisible under the merchant query filter reads exactly like absent: 404 either way, so an order
        // belonging to another company cannot be probed for existence.
        var order = await _orders.GetAsync(command.OrderId, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException($"Order {command.OrderId} not found.");

        if (!order.IsAwaitingPayment)
            throw new InvalidOperationException(
                $"Order {order.OrderId} is not awaiting payment; no payment session can be opened for it.");

        var connection = await _connections.GetAsync(command.MerchantId, command.Psp, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"No PSP connection for merchant {command.MerchantId} and PSP {command.Psp}.");

        // The company's commercial arrangement (connection) and what our adapter can actually drive today
        // are two different sets; a method has to clear both, or the customer gets sent down another channel.
        connection.EnsureEligible(method);

        if (!_adapters.For(command.Psp).SupportedMethods.Contains(method))
            throw new InvalidOperationException(
                $"The {command.Psp} adapter cannot honour method '{method}'.");

        var open = await _sessions.GetOpenForOrderAsync(command.OrderId, cancellationToken).ConfigureAwait(false);
        if (open is not null)
        {
            // Same channel: hand back the existing session instead of minting a second chargeable one. A
            // customer who abandoned the PSP page can then resume on the very same hosted charge.
            if (string.Equals(open.Method, method, StringComparison.Ordinal) && open.Psp == command.Psp)
                return new CreateSessionResult(open.Id);

            // Different channel: there is no void/cancel at the PSP, so the open attempt cannot be replaced.
            throw new ConflictException(
                $"Order {command.OrderId} already has an open payment session on a different channel.");
        }

        var session = Session.Create(
            command.MerchantId,
            command.OrderId,
            order.Amount,
            method,
            command.Psp,
            _clock.UtcNow);

        _sessions.Add(session);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new CreateSessionResult(session.Id);
    }
}
