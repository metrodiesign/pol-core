using BuildingBlocks.Application;
using Mediator;
using Payments.Application.Confirmation;
using Payments.Application.Ports;
using Payments.Application.Ports.Psp;
using Payments.Domain;
using SharedKernel;

namespace Payments.Application.CreateSession;

/// <summary>
/// Persists a new <see cref="Session"/> in the <see cref="SessionStatus.Created"/> state, priced from the
/// order and only for a channel this merchant's connection AND our adapter can actually charge.
///
/// Every check lives here rather than at the endpoint because the endpoint is only today's single entry
/// point, while "the amount is the order's own" is an invariant every caller must pass. The order of the
/// checks is itself contract: it decides which status code a caller sees (400 malformed method, 404 unknown
/// order, 409 for every server-state refusal), so it must not be rearranged.
///
/// The one-open-session rule is released lazily rather than by a sweeper: an order whose previous attempt
/// aged past <see cref="Session.OpenTtl"/> gets it retired HERE, at the only moment it blocks anyone
/// (REQ-3.1/3.2). The age check runs BEFORE the same-channel resume, because a dead hosted page is not
/// something to hand back to a customer.
/// </summary>
public sealed class CreateSessionHandler
    : ICommandHandler<CreateSessionCommand, CreateSessionResult>
{
    private readonly IPayableOrderReader _orders;
    private readonly IConnectionRepository _connections;
    private readonly IPspAdapterFactory _adapters;
    private readonly ISessionRepository _sessions;
    private readonly PaymentConfirmationService _confirmation;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;

    public CreateSessionHandler(
        IPayableOrderReader orders,
        IConnectionRepository connections,
        IPspAdapterFactory adapters,
        ISessionRepository sessions,
        PaymentConfirmationService confirmation,
        IUnitOfWork unitOfWork,
        IClock clock)
    {
        _orders = orders;
        _connections = connections;
        _adapters = adapters;
        _sessions = sessions;
        _confirmation = confirmation;
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

        if (open is not null && open.IsExpiredAt(_clock.UtcNow))
        {
            // REQ-3.2: releasing the stale session and minting its replacement land together or not at all —
            // a release that commits alone leaves the order with no way to pay until someone asks again.
            // TWO saves inside the one transaction, not one batch: the filtered unique index
            // IX_PaymentSessions_OrderId_Open forbids two chargeable rows for an order, and EF's
            // ModificationCommandComparer gives no guarantee that the UPDATE is sent before the INSERT.
            // Betting the money path on that ordering is how this deadlocks into a 409 nobody can clear.
            return await _unitOfWork.ExecuteInTransactionAsync(
                async ct =>
                {
                    // Verify-first, ALWAYS: a session carrying a PSP charge may be holding the customer's
                    // money, so only the confirmation service may retire it. Anything other than a session
                    // that is now provably terminal-without-payment (Expired/Failed) means we do not know it
                    // is safe to open a second chargeable attempt — refuse rather than risk a second charge.
                    var outcome = await _confirmation.ConfirmAsync(open, ct).ConfigureAwait(false);
                    if (outcome is not (ConfirmationOutcome.Expired or ConfirmationOutcome.Failed))
                        throw new ConflictException(
                            $"Order {command.OrderId} has a payment session that could not be released ({outcome}).");

                    var lockedAmount = await EnsureStillMintableAsync(command.OrderId, ct).ConfigureAwait(false);
                    return await MintAsync(command, lockedAmount, method, ct).ConfigureAwait(false);
                },
                cancellationToken).ConfigureAwait(false);
        }

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

        return await _unitOfWork.ExecuteInTransactionAsync(
            async ct =>
            {
                var lockedAmount = await EnsureStillMintableAsync(command.OrderId, ct).ConfigureAwait(false);
                return await MintAsync(command, lockedAmount, method, ct).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The mint-side half of the mint-vs-cancel race closure (REQ-3.6): re-reads the order UNDER A ROW LOCK
    /// held to the end of the surrounding transaction, so "still AwaitingPayment" is true at COMMIT time,
    /// not merely at the unlocked read at the top of the handler. Cancel takes the same row's write lock
    /// before it re-checks for sessions, so whichever of the two commits first, the other sees it and
    /// refuses. Always called BEFORE the session row is added — both paths acquire the order row first,
    /// which is what makes them deadlock-free. Returns the locked row's amount so the mint prices from the
    /// same read that proved the order mintable.
    /// </summary>
    private async Task<Money> EnsureStillMintableAsync(Guid orderId, CancellationToken cancellationToken)
    {
        var locked = await _orders.GetForMintAsync(orderId, cancellationToken).ConfigureAwait(false);
        if (locked is not { IsAwaitingPayment: true })
            throw new InvalidOperationException(
                $"Order {orderId} is not awaiting payment; no payment session can be opened for it.");

        return locked.Amount;
    }

    private async Task<CreateSessionResult> MintAsync(
        CreateSessionCommand command,
        Money amount,
        string method,
        CancellationToken cancellationToken)
    {
        var session = Session.Create(
            command.MerchantId,
            command.OrderId,
            amount,
            method,
            command.Psp,
            _clock.UtcNow);

        _sessions.Add(session);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new CreateSessionResult(session.Id);
    }
}
