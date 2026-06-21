using BuildingBlocks.Application;
using Contracts;
using Mediator;
using Payments.Application.Ports;
using Payments.Domain;

namespace Payments.Application.HandlePspWebhook;

/// <summary>
/// The webhook is the source of truth; the browser return URL is UX only. This handler runs the
/// trusted ingest path: resolve the connection by id (never trust the URL) -> reveal secret -> verify
/// signature (reject if invalid, do not trust). Then, inside ONE transaction: parse the event ->
/// claim multi-key idempotency (duplicate-safe) -> fetch-to-confirm with the PSP -> on a confirmed
/// Paid charge, transition the session and enqueue <see cref="PaymentPaid"/> via the outbox -> commit
/// atomically (PLAN #9, #10).
/// </summary>
public sealed class HandlePspWebhookHandler : ICommandHandler<HandlePspWebhookCommand, WebhookHandled>
{
    private const string IdempotencyContext = "psp-webhook";

    private readonly IPspConnectionRepository _connections;
    private readonly IPaymentSessionRepository _sessions;
    private readonly IPspAdapterFactory _adapters;
    private readonly IVaultSecretStore _vault;
    private readonly IIdempotencyStore _idempotency;
    private readonly IOutbox _outbox;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;

    public HandlePspWebhookHandler(
        IPspConnectionRepository connections,
        IPaymentSessionRepository sessions,
        IPspAdapterFactory adapters,
        IVaultSecretStore vault,
        IIdempotencyStore idempotency,
        IOutbox outbox,
        IUnitOfWork unitOfWork,
        IClock clock)
    {
        _connections = connections;
        _sessions = sessions;
        _adapters = adapters;
        _vault = vault;
        _idempotency = idempotency;
        _outbox = outbox;
        _unitOfWork = unitOfWork;
        _clock = clock;
    }

    public async ValueTask<WebhookHandled> Handle(
        HandlePspWebhookCommand command,
        CancellationToken cancellationToken)
    {
        var connection = await _connections.GetByIdAsync(command.PspConnectionId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"PSP connection {command.PspConnectionId} not found.");

        var adapter = _adapters.For(connection.Psp);
        var secret = await _vault.RevealAsync(connection.TenantId, connection.SecretRefName, cancellationToken).ConfigureAwait(false);

        if (!adapter.VerifyWebhook(command.RawPayload, command.Signature, secret))
            return new WebhookHandled(WebhookOutcome.Rejected);

        var outcome = await _unitOfWork.ExecuteInTransactionAsync(
            async ct =>
            {
                var evt = adapter.ParseWebhook(command.RawPayload);
                var pspCode = connection.Psp.ToCode();

                var keys = new[]
                {
                    $"{pspCode}:event:{evt.EventId}",
                    $"{pspCode}:charge:{evt.ExternalChargeId}:{evt.Status}",
                };

                if (!await _idempotency.TryBeginAsync(keys, IdempotencyContext, ct).ConfigureAwait(false))
                    return WebhookOutcome.Duplicate;

                // Fetch-to-confirm: never trust the webhook body's status alone.
                var confirmed = await adapter.FetchChargeAsync(evt.ExternalChargeId, secret, ct).ConfigureAwait(false);
                if (confirmed != PspChargeStatus.Paid)
                    return WebhookOutcome.Ignored;

                var session = await _sessions.GetByExternalChargeAsync(connection.Psp, evt.ExternalChargeId, ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException(
                        $"No PaymentSession for {pspCode} charge {evt.ExternalChargeId}.");

                var occurredAtUtc = _clock.UtcNow;
                session.MarkPaid(evt.ExternalChargeId, occurredAtUtc);

                _outbox.Enqueue(new PaymentPaid(
                    session.Id,
                    session.OrderId,
                    session.TenantId,
                    session.Amount,
                    pspCode,
                    evt.ExternalChargeId,
                    evt.EventId,
                    occurredAtUtc));

                await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
                return WebhookOutcome.Processed;
            },
            cancellationToken).ConfigureAwait(false);

        return new WebhookHandled(outcome);
    }
}
