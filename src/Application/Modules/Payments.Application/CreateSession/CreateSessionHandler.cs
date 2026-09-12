using BuildingBlocks.Application;
using Mediator;
using Payments.Application.Capabilities;
using Payments.Application.Confirmation;
using Payments.Application.Ports;
using Payments.Domain;
using Payments.Domain.Psp;
using SharedKernel;

namespace Payments.Application.CreateSession;

/// <summary>
/// Persists a new <see cref="Session"/> in the <see cref="SessionStatus.Created"/> state, priced from the
/// order and routed to a PSP the server itself selects (REQ-6.8-6.18) — no caller supplies the connection.
///
/// Every check lives here rather than at the endpoint because the endpoint is only today's single entry
/// point, while "the amount is the order's own" is an invariant every caller must pass. The order of the
/// checks is itself contract: it decides which status code a caller sees (400 malformed method, 404 unknown
/// order, 409 for every server-state refusal), so it must not be rearranged.
///
/// Routing is selected LATE — inside the mint transaction, under the merchant shared lock, and only when a
/// brand-new session is minted. A resume or a confirm re-uses the session already attached to the order and
/// never re-routes, so a settings change between attempts can never turn a resumable session into a refusal
/// (REQ-2.8-2.10). The selected connection, secret version and environment are pinned onto the session at
/// <see cref="Session.Create"/> time.
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
    private readonly ISessionRepository _sessions;
    private readonly PaymentConfirmationService _confirmation;
    private readonly IDocumentSaleProbe _documentSales;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;
    private readonly IPaymentAuthorizationLockManager _authorizationLocks;
    private readonly IEffectivePaymentCapabilityResolver _capabilities;
    private readonly IPaymentRouteSelector _routeSelector;

    public CreateSessionHandler(
        IPayableOrderReader orders,
        ISessionRepository sessions,
        PaymentConfirmationService confirmation,
        IDocumentSaleProbe documentSales,
        IUnitOfWork unitOfWork,
        IClock clock,
        IPaymentAuthorizationLockManager authorizationLocks,
        IEffectivePaymentCapabilityResolver capabilities,
        IPaymentRouteSelector routeSelector)
    {
        _orders = orders;
        _sessions = sessions;
        _confirmation = confirmation;
        _documentSales = documentSales;
        _unitOfWork = unitOfWork;
        _clock = clock;
        _authorizationLocks = authorizationLocks;
        _capabilities = capabilities;
        _routeSelector = routeSelector;
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
        if (order.MerchantId != command.MerchantId)
            throw new NotFoundException($"Order {command.OrderId} not found.");
        var orderMethod = RequireOrderMethod(order);
        EnsureMethodMatches(method, orderMethod);

        if (!order.CanOpenPaymentAttempt)
            throw new InvalidOperationException(
                $"Order {order.OrderId} cannot open a payment session from status {order.Status}.");

        await EnsureNoDocumentSoldElsewhereAsync(command.OrderId, cancellationToken).ConfigureAwait(false);

        var open = await _sessions.GetOpenForOrderAsync(command.OrderId, cancellationToken).ConfigureAwait(false);

        if (open is not null && open.IsExpiredAt(_clock.UtcNow))
        {
            // PSP I/O must finish before the short Order -> Session transaction starts. Apply re-locks and
            // reloads the session, so a stale result cannot retire a changed charge or snapshot.
            var prepared = await _confirmation
                .PrepareAsync(open, access: null, pspEventId: null, cancellationToken)
                .ConfigureAwait(false);
            // REQ-3.2: releasing the stale session and minting its replacement land together or not at all —
            // a release that commits alone leaves the order with no way to pay until someone asks again.
            // TWO saves inside the one transaction, not one batch: the filtered unique index
            // IX_PaymentSessions_OrderId_Open forbids two chargeable rows for an order, and EF's
            // ModificationCommandComparer gives no guarantee that the UPDATE is sent before the INSERT.
            // Betting the money path on that ordering is how this deadlocks into a 409 nobody can clear.
            var staleResult = await _unitOfWork.ExecuteInTransactionAsync(
                ct => MintUnderOrderLockAsync(command, method, prepared, ct),
                cancellationToken).ConfigureAwait(false);
            return RequireMinted(staleResult, command.OrderId);
        }

        if (open is not null)
        {
            // Same channel: hand back the existing session instead of minting a second chargeable one. A
            // customer who abandoned the PSP page can then resume on the very same hosted charge. The PSP is
            // NOT compared — the session's route is pinned, so a routing change never blocks a resume.
            if (string.Equals(open.Method, orderMethod, StringComparison.Ordinal))
            {
                if (open.Status == SessionStatus.Redirected)
                    return new CreateSessionResult(open.Id);
                var resumed = await _unitOfWork.ExecuteInTransactionAsync(
                    ct => ResumeCreatedUnderOrderLockAsync(command, method, open.Id, ct),
                    cancellationToken).ConfigureAwait(false);
                return RequireMinted(resumed, command.OrderId);
            }

            // Different channel: there is no void/cancel at the PSP, so the open attempt cannot be replaced.
            throw new ConflictException(
                $"Order {command.OrderId} already has an open payment session on a different channel.");
        }

        PaymentConfirmationService.PreparedConfirmation? preparedToApply = null;
        if (order.PaymentSessionId is { } attachedSessionId)
        {
            // A terminal attached session is the retry path. Prepare its provider result before opening the
            // transaction; the apply phase below is the only code allowed to mutate it.
            var attached = await _sessions.GetByIdAsync(attachedSessionId, cancellationToken).ConfigureAwait(false);
            if (attached is not null && attached.Status is not (SessionStatus.Created or SessionStatus.Redirected))
            {
                preparedToApply = await _confirmation
                    .PrepareAsync(attached, access: null, pspEventId: null, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        var result = await _unitOfWork.ExecuteInTransactionAsync(
            ct => MintUnderOrderLockAsync(command, method, preparedToApply, ct),
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
    /// <para>Route selection runs here, LATE and only for a brand-new mint, under the merchant shared lock
    /// this method already holds (AC-4.1) — an environment activation (task 7) takes the same merchant lock
    /// exclusively, so the two serialize (AC-4.8).</para>
    /// </summary>
    private async Task<MintResult> MintUnderOrderLockAsync(
        CreateSessionCommand command,
        string method,
        PaymentConfirmationService.PreparedConfirmation? preparedToApply,
        CancellationToken cancellationToken)
    {
        await _authorizationLocks.AcquireMerchantSharedAsync(command.MerchantId, cancellationToken)
            .ConfigureAwait(false);
        var locked = await _orders.GetForMintAsync(command.OrderId, cancellationToken).ConfigureAwait(false);
        if (locked is not { CanOpenPaymentAttempt: true })
            throw new InvalidOperationException(
                $"Order {command.OrderId} cannot open a payment session from its current status.");
        if (locked.MerchantId != command.MerchantId)
            throw new NotFoundException($"Order {command.OrderId} not found.");
        var orderMethod = RequireOrderMethod(locked);
        EnsureMethodMatches(method, orderMethod);

        // Lock Order before touching its attached Session. Failed/Expired retries re-confirm that prior
        // attempt so a late PSP settlement becomes PaymentPaid before another chargeable attempt can exist.
        if (preparedToApply is null && locked.PaymentSessionId is { } attachedSessionId)
        {
            var attached = await _sessions.GetByIdAsync(attachedSessionId, cancellationToken).ConfigureAwait(false);
            if (attached is null)
                throw new ConflictException(
                    $"Order {command.OrderId} references a payment session that cannot be confirmed.");

            if (attached.Status is SessionStatus.Created or SessionStatus.Redirected)
            {
                // Method-only: the attached session's route is pinned and must not be re-selected on resume.
                if (string.Equals(attached.Method, orderMethod, StringComparison.Ordinal))
                {
                    if (attached.Status == SessionStatus.Created)
                        await EnsureAuthorizedAsync(locked, attached.Psp, cancellationToken);
                    return new MintResult(attached.Id, null);
                }

                return new MintResult(null, ConfirmationOutcome.Pending);
            }

            // The session changed to terminal after the unlocked preparation read. Do not fetch from the PSP
            // inside this transaction; let the caller retry and prepare against the current row.
            throw new ConflictException(
                $"Order {command.OrderId} payment session changed before confirmation; retry the request.");
        }

        if (preparedToApply is not null)
        {
            if (locked.PaymentSessionId is { } attachedPaymentSessionId
                && attachedPaymentSessionId != preparedToApply.SessionId)
                throw new ConflictException(
                    $"Order {command.OrderId} payment session changed before confirmation; retry the request.");

            var outcome = await _confirmation.ApplyPreparedAsync(preparedToApply, cancellationToken)
                .ConfigureAwait(false);
            if (outcome is not (ConfirmationOutcome.Expired or ConfirmationOutcome.Failed))
                return new MintResult(null, outcome);
        }

        // Select the route now, under the lock, for this new attempt only. The selector refuses (409) when
        // no eligible primary/fallback covers the method (REQ-6.18) — surfaced to the caller unchanged.
        var selection = await _routeSelector
            .SelectAsync(command.MerchantId, command.OrderId, orderMethod, cancellationToken)
            .ConfigureAwait(false);
        await EnsureAuthorizedAsync(locked, selection.Psp, cancellationToken);

        var session = Session.Create(
            command.MerchantId,
            command.OrderId,
            locked.Amount,
            orderMethod,
            selection.Psp,
            selection.PspConnectionId,
            selection.SecretVersionId,
            selection.Environment,
            _clock.UtcNow);

        await _orders.AttachAttemptAsync(
            command.OrderId, session.Id, cancellationToken).ConfigureAwait(false);
        _sessions.Add(session);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new MintResult(session.Id, null);
    }

    private async Task<MintResult> ResumeCreatedUnderOrderLockAsync(
        CreateSessionCommand command,
        string method,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        await _authorizationLocks.AcquireMerchantSharedAsync(command.MerchantId, cancellationToken)
            .ConfigureAwait(false);
        var locked = await _orders.GetForMintAsync(command.OrderId, cancellationToken).ConfigureAwait(false);
        if (locked is not { CanOpenPaymentAttempt: true })
            throw new InvalidOperationException(
                $"Order {command.OrderId} cannot open a payment session from its current status.");
        if (locked.MerchantId != command.MerchantId)
            throw new NotFoundException($"Order {command.OrderId} not found.");
        EnsureMethodMatches(method, RequireOrderMethod(locked));

        var session = await _sessions.GetByIdAsync(sessionId, cancellationToken).ConfigureAwait(false);
        // Method-only: a resume re-uses the session's pinned route, never re-selecting on current settings.
        if (session is null
            || session.Status != SessionStatus.Created
            || !string.Equals(session.Method, method, StringComparison.Ordinal))
            throw new ConflictException(
                $"Order {command.OrderId} no longer has the requested open payment session.");

        await EnsureAuthorizedAsync(locked, session.Psp, cancellationToken).ConfigureAwait(false);
        return new MintResult(session.Id, null);
    }

    private static CreateSessionResult RequireMinted(MintResult result, Guid orderId)
    {
        if (result.PaymentSessionId is { } paymentSessionId)
            return new CreateSessionResult(paymentSessionId);

        throw new ConflictException(
            $"Order {orderId} has a prior payment attempt that blocks retry ({result.BlockingOutcome}).");
    }

    private async Task EnsureAuthorizedAsync(PayableOrder order, Code psp, CancellationToken ct)
    {
        var subject = order.InitiatingAudience switch
        {
            PaymentAudience.User when order.InitiatingMerchantUserId is not null =>
                new PaymentCapabilitySubject(order.MerchantId, PaymentAudience.User,
                    order.InitiatingMerchantUserId),
            PaymentAudience.PlatformAdmin when order.InitiatingMerchantUserId is null =>
                new PaymentCapabilitySubject(order.MerchantId, PaymentAudience.PlatformAdmin, null),
            _ => throw new ConflictException(
                "Order has no trusted payment authorization context.", "payment_authorization_context_missing"),
        };
        var decision = await _capabilities.ResolveMethodAsync(
            new ResolvePaymentMethod(subject, RequireOrderMethod(order), psp.ToCode(), order.Amount), ct);
        if (decision.Allowed)
            return;
        if (decision.Denial is PaymentCapabilityDenial.UserNotActive or PaymentCapabilityDenial.UserPolicyDenied)
            throw new AccessDeniedException(
                "Payment method is not allowed for this Merchant User.", "payment_method_not_allowed");
        throw new ConflictException(
            "Payment method capability is unavailable.", "payment_capability_unavailable");
    }

    private static string RequireOrderMethod(PayableOrder order) => order.PaymentChannel
        ?? throw new ConflictException(
            "Order has no authoritative payment method.", "payment_authorization_context_missing");

    private static void EnsureMethodMatches(string requested, string orderMethod)
    {
        if (!string.Equals(requested, orderMethod, StringComparison.Ordinal))
            throw new ConflictException(
                "Payment Session method must match the Order method.", "payment_method_mismatch");
    }

    private sealed record MintResult(Guid? PaymentSessionId, ConfirmationOutcome? BlockingOutcome);
}
