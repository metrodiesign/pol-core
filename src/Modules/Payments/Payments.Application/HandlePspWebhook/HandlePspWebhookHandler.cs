using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BuildingBlocks.Application;
using Contracts;
using Mediator;
using Payments.Application.Confirmation;
using Payments.Application.Ports;
using Payments.Application.Ports.Psp;
using Payments.Domain;
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
    private readonly IInboundWebhookRecorder _inboundEvents;
    private readonly IClock _clock;

    public HandlePspWebhookHandler(
        IConnectionRepository connections,
        ISessionRepository sessions,
        IPspAdapterFactory adapters,
        IVaultSecretStore vault,
        IUnitOfWork unitOfWork,
        PaymentConfirmationService confirmation,
        IInboundWebhookRecorder inboundEvents,
        IClock clock)
    {
        _connections = connections;
        _sessions = sessions;
        _adapters = adapters;
        _vault = vault;
        _unitOfWork = unitOfWork;
        _confirmation = confirmation;
        _inboundEvents = inboundEvents;
        _clock = clock;
    }

    public async ValueTask<WebhookHandled> Handle(
        HandlePspWebhookCommand command,
        CancellationToken cancellationToken)
    {
        var connection = await _connections.GetByIdAsync(command.PspConnectionId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"PSP connection {command.PspConnectionId} not found.");

        var adapter = _adapters.For(connection.Psp);
        var secret = connection.ActiveSecretVersionId is { } versionId
            ? await _vault.ReadVersionForServerAsync(connection.MerchantId, versionId, cancellationToken).ConfigureAwait(false)
            : await _vault.RevealAsync(connection.MerchantId, connection.SecretRefName, cancellationToken).ConfigureAwait(false);
        var pspCode = connection.Psp.ToCode();
        var fingerprint = Fingerprint(command.RawPayload);

        if (!adapter.VerifyWebhook(command.RawPayload, command.Signature, secret))
        {
            await _inboundEvents.RecordRejectedAsync(connection.Id, connection.MerchantId, pspCode,
                fingerprint, signatureValid: false, "invalid_signature", cancellationToken).ConfigureAwait(false);
            return new WebhookHandled(WebhookOutcome.Rejected);
        }

        WebhookEvent webhookEvent;
        try
        {
            webhookEvent = adapter.ParseWebhook(command.RawPayload);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException or FormatException)
        {
            await _inboundEvents.RecordRejectedAsync(connection.Id, connection.MerchantId, pspCode,
                fingerprint, signatureValid: true, "invalid_payload", cancellationToken).ConfigureAwait(false);
            return new WebhookHandled(WebhookOutcome.Rejected);
        }

        return await _unitOfWork.ExecuteInTransactionAsync(
            async ct =>
            {
                var claim = await _inboundEvents.ClaimAsync(connection.Id, connection.MerchantId, pspCode,
                    webhookEvent.EventId, fingerprint, ct).ConfigureAwait(false);
                if (!CryptographicOperations.FixedTimeEquals(
                        Convert.FromHexString(claim.PayloadFingerprint), Convert.FromHexString(fingerprint)))
                {
                    await _inboundEvents.RecordRejectedAsync(connection.Id, connection.MerchantId, pspCode,
                        fingerprint, signatureValid: true, "event_id_reuse", ct).ConfigureAwait(false);
                    return new WebhookHandled(WebhookOutcome.Rejected);
                }
                if (claim.Status is not (InboundWebhookStatus.Received or InboundWebhookStatus.Ignored))
                    return new WebhookHandled(WebhookOutcome.Duplicate);

                // Throwing here (rather than answering 200) is deliberate: a notification can beat our own
                // SetPspCharge commit, and a redelivery is how that race resolves.
                var session = await _sessions.GetByExternalChargeAsync(
                        connection.Psp, webhookEvent.ExternalChargeId, ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException(
                        $"No PaymentSession for {pspCode} charge {webhookEvent.ExternalChargeId}.");

                var confirmation = await _confirmation
                    .ConfirmAsync(session, new PspAccess(connection, secret), webhookEvent.EventId, ct)
                    .ConfigureAwait(false);
                var outcome = Map(confirmation);
                var inboundEvent = await _inboundEvents.LoadAsync(claim.EventId, ct).ConfigureAwait(false);
                inboundEvent.Complete(session.Id, session.OrderId, outcome.ToString().ToLowerInvariant(), _clock.UtcNow);
                await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
                return new WebhookHandled(outcome);
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static string Fingerprint(string payload) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();

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
