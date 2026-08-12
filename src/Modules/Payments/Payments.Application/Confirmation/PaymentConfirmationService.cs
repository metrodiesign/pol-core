using BuildingBlocks.Application;
using Contracts;
using Microsoft.Extensions.Logging;
using Payments.Application.Ports;
using Payments.Application.Ports.Psp;
using Payments.Domain;
using Payments.Domain.Psp;
using SharedKernel;

namespace Payments.Application.Confirmation;

/// <summary>What the shared confirm line decided about one payment session.</summary>
public enum ConfirmationOutcome
{
    /// <summary>Still chargeable, inside its TTL, and the PSP has not settled it — nothing changed.</summary>
    Pending = 0,

    /// <summary>The session moved to Paid in THIS call; <see cref="PaymentPaid"/> was enqueued.</summary>
    Paid = 1,

    /// <summary>The session was already Paid — no transition, and deliberately no second event.</summary>
    AlreadyPaid = 2,

    /// <summary>Another caller already claimed this charge's confirmation — nothing changed here.</summary>
    Duplicate = 3,

    /// <summary>The session is Failed (the PSP refused the charge in this call, or it already was).</summary>
    Failed = 4,

    /// <summary>The session is Expired (proven chargeless/unsettled past its TTL, or it already was).</summary>
    Expired = 5,

    /// <summary>The PSP collected an amount/currency the session does not back. Logged Critical; the
    /// session was NOT marked paid.</summary>
    AmountMismatch = 7,
}

/// <summary>The PSP connection a session charges through, plus its revealed secret. The webhook path
/// resolves the connection BY ID (never from the URL) and reveals the secret to verify the signature, so it
/// hands its own down rather than paying for a second resolve + a second audited vault reveal.</summary>
public sealed record PspAccess(Connection Connection, string Secret);

/// <summary>
/// The one place a payment session's fate is decided: fetch -> verify -> claim -> mark -> enqueue. Every
/// caller that can move a session (the PSP webhook, the customer's payment-status check, the lazy expire in
/// create-session, and order-cancel's release) goes through here, so those paths share semantics
/// structurally instead of by convention — an ordering fix applied to one of four copies is how a money path
/// rots.
///
/// The rules it enforces, in the order they bite:
/// <list type="number">
///   <item>A session may only be expired when NO charge can exist: no <c>PspExternalChargeId</c> is
///   decidable offline; anything else must be fetched from the PSP first. A fetch failure is AMBIGUOUS and
///   is deliberately NOT caught here — see <see cref="ConfirmAsync(Session, PspAccess?, string?, CancellationToken)"/>.</item>
///   <item>The confirmation claim <c>{psp}:{connection}:charge:{id}:confirmed</c> is shared by all callers
///   and spent LAST, in the caller's transaction, together with the transition it protects.</item>
///   <item><see cref="PaymentPaid"/> is enqueued only on a REAL transition, so two callers that each win a
///   different key still publish at most once.</item>
///   <item>Verified late money moves a Failed/Expired Session to Paid and emits PaymentPaid; Orders decides
///   whether that event is a retryable transition or a terminal reconciliation conflict.</item>
///   <item>The collected amount+currency is compared before any mark.</item>
/// </list>
/// It owns no transaction: it saves the transition it makes (so the caller can treat that save as a phase)
/// and leaves the transaction boundary to the caller.
/// </summary>
public sealed class PaymentConfirmationService
{
    private const string IdempotencyContext = "payment-confirmation";

    private readonly IConnectionRepository _connections;
    private readonly IPspAdapterFactory _adapters;
    private readonly IVaultSecretStore _vault;
    private readonly IIdempotencyStore _idempotency;
    private readonly IOutbox _outbox;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;
    private readonly ILogger<PaymentConfirmationService> _logger;

    public PaymentConfirmationService(
        IConnectionRepository connections,
        IPspAdapterFactory adapters,
        IVaultSecretStore vault,
        IIdempotencyStore idempotency,
        IOutbox outbox,
        IUnitOfWork unitOfWork,
        IClock clock,
        ILogger<PaymentConfirmationService> logger)
    {
        _connections = connections;
        _adapters = adapters;
        _vault = vault;
        _idempotency = idempotency;
        _outbox = outbox;
        _unitOfWork = unitOfWork;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>Confirms a session the caller already loaded, resolving the PSP connection + secret itself.
    /// The form every caller but the webhook uses.</summary>
    public Task<ConfirmationOutcome> ConfirmAsync(Session session, CancellationToken cancellationToken) =>
        ConfirmAsync(session, access: null, pspEventId: null, cancellationToken);

    /// <summary>
    /// Decides <paramref name="session"/> against the PSP and applies the transition it earns.
    /// <para><paramref name="access"/> lets a caller that has already resolved the connection and revealed
    /// the secret pass them in; null resolves them here, LAZILY — a session that never got a charge is
    /// settled without touching the vault at all, so a removed connection cannot brick the order.</para>
    /// <para><paramref name="pspEventId"/> is the PSP delivery's own event id: it becomes an extra
    /// idempotency key (delivery-level dedup) and rides along on <see cref="PaymentPaid"/>. Null for callers
    /// that are asking rather than being told.</para>
    /// <para>The fetch is NOT wrapped in a catch. A timeout, a 5xx or an unreadable response is ambiguous —
    /// the PSP may be holding money we have not heard of — so this method decides NOTHING and lets the
    /// adapter's exception reach the caller, which answers per its context (the webhook 500s and is
    /// redelivered; request-driven callers translate it to 409/pending). Reading an ambiguous failure as
    /// "not paid" is exactly what would expire a session the customer has already paid for.</para>
    /// </summary>
    public async Task<ConfirmationOutcome> ConfirmAsync(
        Session session,
        PspAccess? access,
        string? pspEventId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);

        var now = _clock.UtcNow;

        // No charge id = no charge was ever created at the PSP, so no money can exist for this session. The
        // only case that is decidable without asking (REQ-8.13).
        if (session.PspExternalChargeId is not { } chargeId)
            return await ExpireIfStaleAsync(session, now, cancellationToken).ConfigureAwait(false);

        access ??= await ResolveAccessAsync(session, cancellationToken).ConfigureAwait(false);

        var confirmed = await _adapters.For(session.Psp)
            .FetchChargeAsync(chargeId, access.Secret, cancellationToken)
            .ConfigureAwait(false);

        return confirmed.Status switch
        {
            PspChargeStatus.Paid => await ConfirmPaidAsync(
                session, access.Connection.Id, chargeId, confirmed.Amount, pspEventId, now, cancellationToken).ConfigureAwait(false),
            PspChargeStatus.Failed => await FailAsync(session, now, cancellationToken).ConfigureAwait(false),
            _ => await ExpireIfStaleAsync(session, now, cancellationToken).ConfigureAwait(false),
        };
    }

    /// <summary>Nothing has been collected: expire the session once it is past its TTL, otherwise leave it
    /// exactly as it is (REQ-3.1/3.2). A session that is already terminal just reports what it is.</summary>
    private async Task<ConfirmationOutcome> ExpireIfStaleAsync(Session session, DateTime now, CancellationToken cancellationToken)
    {
        if (Terminal(session.Status) is { } terminal)
            return terminal;

        if (!session.IsExpiredAt(now))
            return ConfirmationOutcome.Pending;

        return await MarkExpiredAsync(session, now, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The PSP refused the charge. Failed sits OUTSIDE the one-open-session filtered index, so the
    /// order can open a fresh attempt immediately instead of waiting out the 24h TTL (REQ-8.5).</summary>
    private async Task<ConfirmationOutcome> FailAsync(Session session, DateTime now, CancellationToken cancellationToken)
    {
        if (Terminal(session.Status) is { } terminal)
            return terminal;

        return await MarkFailedAsync(session, "psp_failed", now, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Single emitter for a real Failed transition. ReasonCode is bounded internal vocabulary,
    /// never a raw PSP response or exception message.</summary>
    public async Task<ConfirmationOutcome> MarkFailedAsync(
        Session session,
        string reasonCode,
        DateTime occurredAt,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reasonCode);
        if (reasonCode.Length > 64 || reasonCode.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-')))
            throw new ArgumentException("ReasonCode must be a bounded internal code.", nameof(reasonCode));

        if (Terminal(session.Status) is { } terminal)
            return terminal;

        session.MarkFailed(reasonCode, occurredAt);
        _outbox.Enqueue(new PaymentFailed(
            Guid.CreateVersion7(),
            session.Id,
            session.OrderId,
            session.MerchantId,
            reasonCode,
            occurredAt));
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return ConfirmationOutcome.Failed;
    }

    /// <summary>Single emitter for a real Expired transition.</summary>
    public async Task<ConfirmationOutcome> MarkExpiredAsync(
        Session session,
        DateTime occurredAt,
        CancellationToken cancellationToken)
    {
        if (Terminal(session.Status) is { } terminal)
            return terminal;

        session.MarkExpired(occurredAt);
        _outbox.Enqueue(new PaymentExpired(
            Guid.CreateVersion7(),
            session.Id,
            session.OrderId,
            session.MerchantId,
            occurredAt));
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return ConfirmationOutcome.Expired;
    }

    private async Task<ConfirmationOutcome> ConfirmPaidAsync(
        Session session,
        Guid pspConnectionId,
        string chargeId,
        Money? collectedAmount,
        string? pspEventId,
        DateTime now,
        CancellationToken cancellationToken)
    {
        // What the PSP ACTUALLY collected must be what the session backs. Money is a record struct, so !=
        // covers both the amount and the currency. A null means the PSP reported no amount (REQ-8.3):
        // confirm on status alone — never read it as "collected zero".
        if (collectedAmount is { } collected && collected != session.Amount)
        {
            _logger.LogCritical(
                "Payment amount mismatch on order {OrderId}: PSP charge {ExternalChargeId} collected {CollectedAmount} {CollectedCurrency} "
                + "but payment session {PaymentSessionId} is priced {SessionAmount} {SessionCurrency}. The session was NOT marked paid.",
                session.OrderId, chargeId, collected.Amount, collected.Currency, session.Id, session.Amount.Amount, session.Amount.Currency);

            return ConfirmationOutcome.AmountMismatch;
        }

        // Claimed LAST, in the caller's transaction, together with the transition it protects: an outcome
        // that confirmed nothing must leave the key unspent for the delivery that will (live 2C2P repro,
        // 2026-07-28 — claiming before the fetch poisoned the key on a not-yet-settled notification). Keys
        // are scoped by connection id so an event id unique only per-merchant cannot collide.
        var pspCode = session.Psp.ToCode();
        var keys = pspEventId is null
            ? new[] { $"{pspCode}:{pspConnectionId}:charge:{chargeId}:confirmed" }
            : [$"{pspCode}:{pspConnectionId}:charge:{chargeId}:confirmed", $"{pspCode}:{pspConnectionId}:event:{pspEventId}"];

        if (!await _idempotency.TryBeginAsync(keys, IdempotencyContext, cancellationToken).ConfigureAwait(false))
            return ConfirmationOutcome.Duplicate;

        // The event rides the TRANSITION, not the claim: a session that is already Paid (a delivery that
        // arrived under a key this one had not spent) must not publish PaymentPaid a second time.
        var transitions = session.Status is not SessionStatus.Paid;
        session.MarkPaid(chargeId, now);
        if (!transitions)
            return ConfirmationOutcome.AlreadyPaid;

        _outbox.Enqueue(new PaymentPaid(
            Guid.CreateVersion7(),
            session.Id,
            session.OrderId,
            session.MerchantId,
            session.Amount,
            session.Method,
            pspCode,
            chargeId,
            pspEventId ?? $"inquiry:{chargeId}",
            now));

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return ConfirmationOutcome.Paid;
    }

    private async Task<PspAccess> ResolveAccessAsync(Session session, CancellationToken cancellationToken)
    {
        var connection = await _connections.GetAsync(session.MerchantId, session.Psp, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"No PSP connection for merchant {session.MerchantId} and PSP {session.Psp}.");

        var secret = connection.ActiveSecretVersionId is { } versionId
            ? await _vault.ReadVersionForServerAsync(session.MerchantId, versionId, cancellationToken).ConfigureAwait(false)
            : await _vault.RevealAsync(session.MerchantId, connection.SecretRefName, cancellationToken).ConfigureAwait(false);

        return new PspAccess(connection, secret);
    }

    /// <summary>The outcome a session that is ALREADY terminal reports, or null when it is still open.</summary>
    private static ConfirmationOutcome? Terminal(SessionStatus status) => status switch
    {
        SessionStatus.Paid => ConfirmationOutcome.AlreadyPaid,
        SessionStatus.Failed => ConfirmationOutcome.Failed,
        SessionStatus.Expired => ConfirmationOutcome.Expired,
        _ => null,
    };
}
