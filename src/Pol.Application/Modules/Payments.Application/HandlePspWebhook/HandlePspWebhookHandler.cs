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
using SharedKernel;

namespace Payments.Application.HandlePspWebhook;

/// <summary>
/// The webhook is the source of truth; the browser return URL is UX only. This handler runs the trusted
/// ingest path with the pinned-secret contract (merchant-psp-settings task 8):
/// <list type="number">
///   <item>Resolve the connection BY ID (never the URL), then pull the BOUNDED, UNTRUSTED reference from the
///   body (<see cref="IPspAdapter.ExtractWebhookReference"/>) — no state changes, no 500 on a malformed
///   reference (AC-8.1/8.4).</item>
///   <item>Resolve the session the reference points at BEFORE verifying. 2C2P is deterministic
///   (<c>invoiceNo = Session.Id</c>); Omise is by external charge id. A cross-merchant reference resolves to
///   null under the merchant query filter, so it can never be acked (AC-8.2/8.3, adversarial #3).</item>
///   <item>An unresolved fetch-confirm-only (Omise) webhook is parked as a bounded pending match + rematch
///   (202); an unresolved signed (2C2P) webhook — or one whose charge is not yet bound — is deferred (503)
///   for provider retry after bind (AC-8.4/8.5).</item>
///   <item>Read the secret version the SESSION pinned (retired stays readable); an unreadable pinned version
///   defers (503) rather than false-acking (AC-8.5). Verify the signature with THAT secret (401 on failure),
///   then hand the session to <see cref="PaymentConfirmationService"/>, which fetches-to-confirm on the
///   pinned secret/environment and never trusts the body (AC-8.2/8.3, adversarial #2/#5/#8).</item>
/// </list>
/// No raw payload or secret is ever stored or returned (AC-8.6).
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
        var pspCode = connection.Psp.ToCode();
        var fingerprint = Fingerprint(command.RawPayload);
        var mode = adapter.WebhookVerificationMode;

        // Bounded, untrusted lookup key only — a malformed/over-long reference throws InvalidRequestException
        // (400 validation_failed) out to the endpoint, never a 500, and nothing is stored (AC-8.1/8.4 #4).
        var reference = adapter.ExtractWebhookReference(command.RawPayload);

        // Resolve the session that pins the secret BEFORE verifying (AC-8.1).
        var session = mode == WebhookVerificationMode.SignedDeterministicReference
            ? await ResolveDeterministicAsync(reference.ExternalChargeId, cancellationToken).ConfigureAwait(false)
            : await _sessions.GetByExternalChargeAsync(connection.Psp, reference.ExternalChargeId, cancellationToken).ConfigureAwait(false);

        if (session is null)
        {
            // Fetch-confirm-only (Omise) before bind: park a bounded pending match + enqueue rematch (202).
            // Signed (2C2P) unresolved: defer, provider retries after the charge binds (503) — no state,
            // no false ack (AC-8.4/8.5, design 347-352).
            if (mode == WebhookVerificationMode.FetchConfirmOnly)
            {
                await _inboundEvents.RecordPendingMatchAsync(connection.Id, connection.MerchantId, pspCode,
                    reference.ExternalEventId, reference.ExternalChargeId, fingerprint, cancellationToken).ConfigureAwait(false);
                return new WebhookHandled(WebhookOutcome.PendingMatch);
            }
            return new WebhookHandled(WebhookOutcome.Deferred);
        }

        // Read the version the SESSION pinned (retired stays readable, AC-6.5). An unreadable pinned secret
        // (rotation/lease mid-flight) defers rather than false-acks (AC-8.5).
        string secret;
        try
        {
            secret = await ReadPinnedSecretAsync(session, connection, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException)
        {
            return new WebhookHandled(WebhookOutcome.Deferred);
        }

        // Verify with the pinned secret. For 2C2P this is the authority (401 on failure, adversarial #2); for
        // Omise it is a well-formedness gate only and the fetch-to-confirm below is the authority (AC-8.3).
        if (!adapter.VerifyWebhook(command.RawPayload, command.Signature, secret))
        {
            await _inboundEvents.RecordRejectedAsync(connection.Id, connection.MerchantId, pspCode,
                fingerprint, signatureValid: false, "invalid_signature", cancellationToken).ConfigureAwait(false);
            return new WebhookHandled(WebhookOutcome.Rejected);
        }

        // Signed & verified but the charge is not bound yet (2C2P webhook racing our own bind): defer for
        // provider retry rather than fetch-confirming a charge id we do not hold (design 350-352).
        if (session.PspExternalChargeId is null)
            return new WebhookHandled(WebhookOutcome.Deferred);

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

        try
        {
            return await ConfirmInTransactionAsync(connection, session, secret, webhookEvent, fingerprint, mode, pspCode, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (PspAmbiguousException)
        {
            // Fetch-to-confirm was undecidable (timeout / 5xx / unverifiable): the PSP may hold money we have
            // not confirmed, so DO NOT ack and DO NOT fall back to the body status (adversarial #8). The
            // transaction rolled back with the throw, so no claim was spent; the provider retries (503).
            return new WebhookHandled(WebhookOutcome.Deferred);
        }
    }

    private async ValueTask<WebhookHandled> ConfirmInTransactionAsync(
        Connection connection, Session session, string secret, WebhookEvent webhookEvent,
        string fingerprint, WebhookVerificationMode mode, string pspCode, CancellationToken cancellationToken) =>
        await _unitOfWork.ExecuteInTransactionAsync(
            async ct =>
            {
                var claim = await _inboundEvents.ClaimAsync(connection.Id, connection.MerchantId, pspCode,
                    webhookEvent.EventId, fingerprint, mode, ct).ConfigureAwait(false);
                if (!CryptographicOperations.FixedTimeEquals(
                        Convert.FromHexString(claim.PayloadFingerprint), Convert.FromHexString(fingerprint)))
                {
                    await _inboundEvents.RecordRejectedAsync(connection.Id, connection.MerchantId, pspCode,
                        fingerprint, signatureValid: true, "event_id_reuse", ct).ConfigureAwait(false);
                    return new WebhookHandled(WebhookOutcome.Rejected);
                }
                if (claim.Status is not (InboundWebhookStatus.Received or InboundWebhookStatus.Ignored))
                    return new WebhookHandled(WebhookOutcome.Duplicate);

                // Confirm on the PINNED snapshot: the fetch is the authority (Omise body status is never
                // trusted, adversarial #5), an ambiguous fetch throws out to a 503 (adversarial #8), and the
                // amount is compared before any mark. The session is the one the reference resolved.
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

    /// <summary>2C2P <c>invoiceNo</c> IS the <see cref="Session.Id"/> ("N" format), so the session resolves
    /// deterministically even before its charge is bound. A reference that is not one of our session ids —
    /// including a cross-merchant id, which the merchant query filter hides — resolves to null and defers,
    /// never a false ack (adversarial #3).</summary>
    private Task<Session?> ResolveDeterministicAsync(string reference, CancellationToken cancellationToken) =>
        Guid.TryParseExact(reference, "N", out var sessionId) || Guid.TryParse(reference, out sessionId)
            ? _sessions.GetByIdAsync(sessionId, cancellationToken)
            : Task.FromResult<Session?>(null);

    private async Task<string> ReadPinnedSecretAsync(Session session, Connection connection, CancellationToken cancellationToken) =>
        session.SecretVersionId is { } pinned
            ? await _vault.ReadVersionForServerAsync(session.MerchantId, pinned, cancellationToken).ConfigureAwait(false)
            // Legacy version-0 session (no pinned version): fall back to the connection's active version /
            // ref. task 9 owns v0 remediation.
            : connection.ActiveSecretVersionId is { } versionId
                ? await _vault.ReadVersionForServerAsync(session.MerchantId, versionId, cancellationToken).ConfigureAwait(false)
                : await _vault.RevealAsync(session.MerchantId, connection.SecretRefName, cancellationToken).ConfigureAwait(false);

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
