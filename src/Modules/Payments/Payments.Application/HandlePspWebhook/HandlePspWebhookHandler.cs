using BuildingBlocks.Application;
using Contracts;
using Mediator;
using Payments.Application.Confirmation;
using Payments.Application.Ports;
using Payments.Application.Ports.Psp;
using Payments.Domain.Psp;

namespace Payments.Application.HandlePspWebhook;

/// <summary>
/// The webhook is the source of truth; the browser return URL is UX only. This handler runs the
/// trusted ingest path: resolve the connection by id (never trust the URL) -> reveal secret -> verify
/// signature (reject if invalid, do not trust). Then, inside ONE transaction: parse the event -> find the
/// session the charge belongs to -> hand it to <see cref="PaymentConfirmationService"/>, which fetches to
/// confirm, compares the collected amount, claims the shared idempotency key and transitions + enqueues
/// <see cref="PaymentPaid"/> atomically (PLAN #9, #10).
///
/// What is left here is only what is webhook-specific: the connection is resolved BY ID and its secret
/// revealed once (for the signature, then reused for the fetch), the delivery's event id is carried in as an
/// extra idempotency key, and the confirmation outcome is mapped to the HTTP-visible
/// <see cref="WebhookOutcome"/>. Everything a status check, a lazy expire or a release would also have to do
/// lives in the service, so all four paths cannot drift apart.
/// </summary>
public sealed class HandlePspWebhookHandler : ICommandHandler<HandlePspWebhookCommand, WebhookHandled>
{
    private readonly IConnectionRepository _connections;
    private readonly ISessionRepository _sessions;
    private readonly IPspAdapterFactory _adapters;
    private readonly IVaultSecretStore _vault;
    private readonly IUnitOfWork _unitOfWork;
    private readonly PaymentConfirmationService _confirmation;

    public HandlePspWebhookHandler(
        IConnectionRepository connections,
        ISessionRepository sessions,
        IPspAdapterFactory adapters,
        IVaultSecretStore vault,
        IUnitOfWork unitOfWork,
        PaymentConfirmationService confirmation)
    {
        _connections = connections;
        _sessions = sessions;
        _adapters = adapters;
        _vault = vault;
        _unitOfWork = unitOfWork;
        _confirmation = confirmation;
    }

    public async ValueTask<WebhookHandled> Handle(
        HandlePspWebhookCommand command,
        CancellationToken cancellationToken)
    {
        var connection = await _connections.GetByIdAsync(command.PspConnectionId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"PSP connection {command.PspConnectionId} not found.");

        var adapter = _adapters.For(connection.Psp);
        var secret = await _vault.RevealAsync(connection.MerchantId, connection.SecretRefName, cancellationToken).ConfigureAwait(false);

        if (!adapter.VerifyWebhook(command.RawPayload, command.Signature, secret))
            return new WebhookHandled(WebhookOutcome.Rejected);

        var outcome = await _unitOfWork.ExecuteInTransactionAsync(
            async ct =>
            {
                var evt = adapter.ParseWebhook(command.RawPayload);

                // Throwing here (rather than answering 200) is deliberate: a notification can beat our own
                // SetPspCharge commit, and a redelivery is how that race resolves.
                var session = await _sessions.GetByExternalChargeAsync(connection.Psp, evt.ExternalChargeId, ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException(
                        $"No PaymentSession for {connection.Psp.ToCode()} charge {evt.ExternalChargeId}.");

                return await _confirmation
                    .ConfirmAsync(session, new PspAccess(connection, secret), evt.EventId, ct)
                    .ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);

        return new WebhookHandled(Map(outcome));
    }

    /// <summary>
    /// The PSP only ever needs to know whether to redeliver. Everything the service decided — including the
    /// two Critical-logged states, an amount that does not back the order and money collected for a session
    /// already gone terminal — answers 200: no redelivery can change any of them, and a 500 would turn each
    /// into an endless retry loop against a state a human has to resolve (REQ-3.4/3.5). Only an ambiguous
    /// FETCH gets a retry, and that path throws out of the service instead of returning an outcome.
    /// </summary>
    private static WebhookOutcome Map(ConfirmationOutcome outcome) => outcome switch
    {
        ConfirmationOutcome.Paid or ConfirmationOutcome.Failed or ConfirmationOutcome.Expired => WebhookOutcome.Processed,
        ConfirmationOutcome.Duplicate or ConfirmationOutcome.AlreadyPaid => WebhookOutcome.Duplicate,
        _ => WebhookOutcome.Ignored,
    };
}
