using BuildingBlocks.Application;
using Contracts;
using Mediator;
using Payments.Application.Confirmation;
using Payments.Application.Ports;
using Payments.Application.Ports.Psp;
using Payments.Domain;

namespace Payments.Application.HandlePspWebhook;

/// <summary>
/// Closes the fetch-confirm-only (Omise) webhook/charge-bind race from BOTH directions with one idempotent
/// rematch (merchant-psp-settings AC-8.4, design 354, 483-486): <see cref="InboundWebhookMatchRequested"/>
/// fires when a webhook was parked before its charge bound, and <see cref="PspChargeBound"/> fires when the
/// bind lands with a pending match already waiting. Whichever commits first drives the rematch; the other
/// finds no pending row and is a no-op, and the shared confirmation idempotency key guarantees at most one
/// transition and one <c>PaymentPaid</c> even under a race. No raw payload is used — the confirm re-fetches
/// on the pinned secret/environment (AC-8.6).
/// </summary>
public sealed class InboundWebhookRematcher
    : INotificationHandler<InboundWebhookMatchRequested>, INotificationHandler<PspChargeBound>
{
    private readonly IConnectionRepository _connections;
    private readonly ISessionRepository _sessions;
    private readonly PaymentConfirmationService _confirmation;
    private readonly IInboundWebhookRecorder _inboundEvents;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;

    public InboundWebhookRematcher(
        IConnectionRepository connections,
        ISessionRepository sessions,
        PaymentConfirmationService confirmation,
        IInboundWebhookRecorder inboundEvents,
        IUnitOfWork unitOfWork,
        IClock clock)
    {
        _connections = connections;
        _sessions = sessions;
        _confirmation = confirmation;
        _inboundEvents = inboundEvents;
        _unitOfWork = unitOfWork;
        _clock = clock;
    }

    public ValueTask Handle(InboundWebhookMatchRequested notification, CancellationToken cancellationToken) =>
        new(RematchAsync(notification.PspConnectionId, notification.ExternalChargeId, cancellationToken));

    public ValueTask Handle(PspChargeBound notification, CancellationToken cancellationToken) =>
        new(RematchAsync(notification.PspConnectionId, notification.ExternalChargeId, cancellationToken));

    private async Task RematchAsync(Guid connectionId, string externalChargeId, CancellationToken cancellationToken)
    {
        var pending = await _inboundEvents
            .FindPendingMatchesAsync(connectionId, externalChargeId, cancellationToken).ConfigureAwait(false);
        if (pending.Count == 0)
            return;

        var connection = await _connections.GetByIdAsync(connectionId, cancellationToken).ConfigureAwait(false);
        if (connection is null)
            return;

        var session = await _sessions
            .GetByExternalChargeAsync(connection.Psp, externalChargeId, cancellationToken).ConfigureAwait(false);
        // Charge not bound to a session yet: the PspChargeBound direction will drive the rematch once it is.
        if (session is null)
            return;

        // Resolve the pinned secret and fetch the PSP result before opening the short write transaction.
        var prepared = await _confirmation
            .PrepareAsync(session, access: null, pending[0].ExternalEventId, cancellationToken)
            .ConfigureAwait(false);

        await _unitOfWork.ExecuteInTransactionAsync(
            async ct =>
            {
                // Re-read tracked inside the transaction: a concurrent rematch may have resolved them.
                var rows = await _inboundEvents
                    .FindPendingMatchesAsync(connectionId, externalChargeId, ct).ConfigureAwait(false);
                if (rows.Count == 0)
                    return 0;

                // Apply only the evidence prepared outside this transaction. The service locks/reloads the
                // current Session before claiming or mutating it.
                var confirmation = await _confirmation
                    .ApplyPreparedAsync(prepared, ct).ConfigureAwait(false);
                var outcome = OutcomeCode(confirmation);
                foreach (var row in rows)
                    row.Complete(session.Id, session.OrderId, outcome, _clock.UtcNow);
                await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
                return rows.Count;
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static string OutcomeCode(ConfirmationOutcome outcome) => outcome switch
    {
        ConfirmationOutcome.Paid or ConfirmationOutcome.Failed or ConfirmationOutcome.Expired => "processed",
        ConfirmationOutcome.Duplicate or ConfirmationOutcome.AlreadyPaid => "duplicate",
        _ => "ignored",
    };
}
