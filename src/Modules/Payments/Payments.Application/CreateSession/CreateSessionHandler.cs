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
    private readonly IDocumentSaleProbe _documentSales;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;

    public CreateSessionHandler(
        IPayableOrderReader orders,
        IConnectionRepository connections,
        IPspAdapterFactory adapters,
        ISessionRepository sessions,
        PaymentConfirmationService confirmation,
        IDocumentSaleProbe documentSales,
        IUnitOfWork unitOfWork,
        IClock clock)
    {
        _orders = orders;
        _connections = connections;
        _adapters = adapters;
        _sessions = sessions;
        _confirmation = confirmation;
        _documentSales = documentSales;
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

        if (!order.CanOpenPaymentAttempt)
            throw new InvalidOperationException(
                $"Order {order.OrderId} cannot open a payment session from status {order.Status}.");

        await EnsureNoDocumentSoldElsewhereAsync(command.OrderId, cancellationToken).ConfigureAwait(false);

        var connection = await _connections.GetAsync(command.MerchantId, command.Psp, cancellationToken).ConfigureAwait(false)
            ?? throw new ConflictException(
                $"No PSP connection for merchant {command.MerchantId} and PSP {command.Psp}.",
                "psp-unavailable");

        // The company's commercial arrangement (connection) and what our adapter can actually drive today
        // are two different sets; a method has to clear both, or the customer gets sent down another channel.
        try
        {
            connection.EnsureEligible(method);
        }
        catch (InvalidOperationException exception)
        {
            throw new ConflictException(exception.Message, "psp-unavailable", exception);
        }

        if (!_adapters.For(command.Psp).SupportedMethods.Contains(method))
            throw new ConflictException(
                $"The {command.Psp} adapter cannot honour method '{method}'.",
                "psp-unavailable");

        var open = await _sessions.GetOpenForOrderAsync(command.OrderId, cancellationToken).ConfigureAwait(false);

        if (open is not null && open.IsExpiredAt(_clock.UtcNow))
        {
            // REQ-3.2: releasing the stale session and minting its replacement land together or not at all —
            // a release that commits alone leaves the order with no way to pay until someone asks again.
            // TWO saves inside the one transaction, not one batch: the filtered unique index
            // IX_PaymentSessions_OrderId_Open forbids two chargeable rows for an order, and EF's
            // ModificationCommandComparer gives no guarantee that the UPDATE is sent before the INSERT.
            // Betting the money path on that ordering is how this deadlocks into a 409 nobody can clear.
            var staleResult = await _unitOfWork.ExecuteInTransactionAsync(
                ct => MintUnderOrderLockAsync(command, method, open, ct),
                cancellationToken).ConfigureAwait(false);
            return RequireMinted(staleResult, command.OrderId);
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

        var result = await _unitOfWork.ExecuteInTransactionAsync(
            ct => MintUnderOrderLockAsync(command, method, sessionToConfirm: null, ct),
            cancellationToken).ConfigureAwait(false);
        return RequireMinted(result, command.OrderId);
    }

    /// <summary>
    /// The last gate before a charge exists at the PSP (products-external-source-of-truth REQ-5.6): an
    /// insurance document is sold once, and between checkout and this call another order — very possibly
    /// another merchant's — may have paid for it. Refusing here means the customer sees a 409 instead of a
    /// receipt for a document they can never be given.
    /// <para>Holds by THIS order are not a conflict: its own in-flight payment session is exactly what a
    /// resume/retry looks like. The message names neither the holding order nor its merchant (REQ-5.7) —
    /// that pair only ever appears in <see cref="DocumentSaleStatus.HeldByOrderId"/>, which stays here.</para>
    /// </summary>
    private async Task EnsureNoDocumentSoldElsewhereAsync(Guid orderId, CancellationToken cancellationToken)
    {
        var keys = await _orders.GetDocumentKeysAsync(orderId, cancellationToken).ConfigureAwait(false);
        if (keys.Count == 0)
            return;

        var statuses = await _documentSales.ProbeAsync(keys, cancellationToken).ConfigureAwait(false);
        if (statuses.Any(status => status.HeldByOrderId != orderId))
            throw new ConflictException(
                "An insurance document on this order is no longer available for sale.");
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
    private async Task<MintResult> MintUnderOrderLockAsync(
        CreateSessionCommand command,
        string method,
        Session? sessionToConfirm,
        CancellationToken cancellationToken)
    {
        var locked = await _orders.GetForMintAsync(command.OrderId, cancellationToken).ConfigureAwait(false);
        if (locked is not { CanOpenPaymentAttempt: true })
            throw new InvalidOperationException(
                $"Order {command.OrderId} cannot open a payment session from its current status.");

        // Lock Order before touching its attached Session. Failed/Expired retries re-confirm that prior
        // attempt so a late PSP settlement becomes PaymentPaid before another chargeable attempt can exist.
        if (sessionToConfirm is null && locked.PaymentSessionId is { } attachedSessionId)
        {
            sessionToConfirm = await _sessions.GetByIdAsync(attachedSessionId, cancellationToken).ConfigureAwait(false);
            if (sessionToConfirm is null)
                throw new ConflictException(
                    $"Order {command.OrderId} references a payment session that cannot be confirmed.");

            if (sessionToConfirm.Status is SessionStatus.Created or SessionStatus.Redirected)
            {
                if (sessionToConfirm.Psp == command.Psp
                    && string.Equals(sessionToConfirm.Method, method, StringComparison.Ordinal))
                {
                    return new MintResult(sessionToConfirm.Id, null);
                }

                return new MintResult(null, ConfirmationOutcome.Pending);
            }
        }

        if (sessionToConfirm is not null)
        {
            var outcome = await _confirmation.ConfirmAsync(sessionToConfirm, cancellationToken).ConfigureAwait(false);
            if (outcome is not (ConfirmationOutcome.Expired or ConfirmationOutcome.Failed))
                return new MintResult(null, outcome);
        }

        var session = Session.Create(
            command.MerchantId,
            command.OrderId,
            locked.Amount,
            method,
            command.Psp,
            _clock.UtcNow);

        await _orders.AttachAttemptAsync(
            command.OrderId, session.Id, method, cancellationToken).ConfigureAwait(false);
        _sessions.Add(session);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new MintResult(session.Id, null);
    }

    private static CreateSessionResult RequireMinted(MintResult result, Guid orderId)
    {
        if (result.PaymentSessionId is { } paymentSessionId)
            return new CreateSessionResult(paymentSessionId);

        throw new ConflictException(
            $"Order {orderId} has a prior payment attempt that blocks retry ({result.BlockingOutcome}).");
    }

    private sealed record MintResult(Guid? PaymentSessionId, ConfirmationOutcome? BlockingOutcome);
}
