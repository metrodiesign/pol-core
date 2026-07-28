using BuildingBlocks.Application;
using Contracts;
using Mediator;
using Payments.Application.Ports;
using Payments.Application.Ports.Psp;
using Payments.Domain.Psp;

namespace Payments.Application.HandlePspWebhook;

/// <summary>
/// The webhook is the source of truth; the browser return URL is UX only. This handler runs the
/// trusted ingest path: resolve the connection by id (never trust the URL) -> reveal secret -> verify
/// signature (reject if invalid, do not trust). Then, inside ONE transaction: parse the event ->
/// fetch-to-confirm with the PSP -> on a confirmed Paid charge, claim multi-key idempotency
/// (duplicate-safe, spent atomically with the transition — REQ-8.5) -> transition the session and
/// enqueue <see cref="PaymentPaid"/> via the outbox -> commit atomically (PLAN #9, #10).
/// </summary>
public sealed class HandlePspWebhookHandler : ICommandHandler<HandlePspWebhookCommand, WebhookHandled>
{
    private const string IdempotencyContext = "psp-webhook";

    private readonly IConnectionRepository _connections;
    private readonly ISessionRepository _sessions;
    private readonly IPspAdapterFactory _adapters;
    private readonly IVaultSecretStore _vault;
    private readonly IIdempotencyStore _idempotency;
    private readonly IOutbox _outbox;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;

    public HandlePspWebhookHandler(
        IConnectionRepository connections,
        ISessionRepository sessions,
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
        var secret = await _vault.RevealAsync(connection.MerchantId, connection.SecretRefName, cancellationToken).ConfigureAwait(false);

        if (!adapter.VerifyWebhook(command.RawPayload, command.Signature, secret))
            return new WebhookHandled(WebhookOutcome.Rejected);

        var outcome = await _unitOfWork.ExecuteInTransactionAsync(
            async ct =>
            {
                var evt = adapter.ParseWebhook(command.RawPayload);
                var pspCode = connection.Psp.ToCode();

                // Fetch-to-confirm: never trust the webhook body's status alone.
                var confirmed = await adapter.FetchChargeAsync(evt.ExternalChargeId, secret, ct).ConfigureAwait(false);
                if (confirmed.Status != PspChargeStatus.Paid)
                    return WebhookOutcome.Ignored;

                var session = await _sessions.GetByExternalChargeAsync(connection.Psp, evt.ExternalChargeId, ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException(
                        $"No PaymentSession for {pspCode} charge {evt.ExternalChargeId}.");

                // REQ-8.2: what the PSP actually collected must be what the order backs. Since the session
                // is priced from the order row, comparing here is the only place a wrong-amount collection
                // can be caught — the Orders-side check compares the session's own amount to itself. Money
                // is a record struct, so != covers BOTH the amount and the currency. A null Amount means the
                // PSP reported none (REQ-8.3): confirm on status alone, exactly as before this check existed.
                if (confirmed.Amount is { } collected && collected != session.Amount)
                    return WebhookOutcome.Ignored;

                // The claim is spent LAST, in the same transaction as the transition it protects (REQ-8.5).
                // Claiming before the fetch burned charge:{id}:Paid on an Ignored outcome, and TryBeginAsync
                // saves inside the ambient transaction which commits on any normal return — so a "paid"
                // notification arriving before paymentInquiry could see the settled charge poisoned the key
                // and every genuine redelivery after it was refused as Duplicate, forever (proven live on the
                // 2C2P sandbox, 2026-07-28). Keys are scoped by the PSP connection id so a webhook event id
                // that is unique only per-merchant (not globally) cannot collide across merchants/connections.
                var keys = new[]
                {
                    $"{pspCode}:{command.PspConnectionId}:event:{evt.EventId}",
                    $"{pspCode}:{command.PspConnectionId}:charge:{evt.ExternalChargeId}:{evt.Status}",
                };

                if (!await _idempotency.TryBeginAsync(keys, IdempotencyContext, ct).ConfigureAwait(false))
                    return WebhookOutcome.Duplicate;

                var occurredAt = _clock.UtcNow;
                session.MarkPaid(evt.ExternalChargeId, occurredAt);

                _outbox.Enqueue(new PaymentPaid(
                    session.Id,
                    session.OrderId,
                    session.MerchantId,
                    session.Amount,
                    pspCode,
                    evt.ExternalChargeId,
                    evt.EventId,
                    occurredAt));

                await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
                return WebhookOutcome.Processed;
            },
            cancellationToken).ConfigureAwait(false);

        return new WebhookHandled(outcome);
    }
}
