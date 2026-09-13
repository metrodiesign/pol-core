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
///   <item>A session may only be expired when NO charge can exist: a <c>Created</c> session without a
///   <c>PspExternalChargeId</c> is decidable offline, while a <c>Redirected</c> session without one may
///   still be in an ambiguous PSP claim. Anything else must be fetched from the PSP first. A fetch failure
///   is AMBIGUOUS and is deliberately NOT caught here — see <see cref="ConfirmAsync(Session, PspAccess?, string?, CancellationToken)"/>.</item>
///   <item>The confirmation claim <c>{psp}:{connection}:charge:{id}:confirmed</c> is shared by all callers
///   and spent LAST, in the caller's transaction, together with the transition it protects.</item>
///   <item><see cref="PaymentPaid"/> is enqueued only on a REAL transition, so two callers that each win a
///   different key still publish at most once.</item>
///   <item>Verified late money moves a Failed/Expired Session to Paid and emits PaymentPaid; Orders decides
///   whether that event is a retryable transition or a terminal reconciliation conflict.</item>
///   <item>The collected amount+currency is compared before any mark.</item>
/// </list>
/// It separates provider I/O from the apply phase. Apply locks and reloads the current Session, then commits
/// the claim, transition, inbound completion, and outbox changes in the caller's short unit-of-work
/// transaction.
/// </summary>
public sealed class PaymentConfirmationService
{
    private static readonly object PreparedConstructionToken = new();

    /// <summary>Internal provider evidence. Construction requires a private service-owned token, so API or
    /// request code cannot manufacture a prepared result.</summary>
    internal sealed class PreparedConfirmation
    {
        internal PreparedConfirmation(
            object constructionToken,
            Guid sessionId,
            SessionStatus preparedSessionStatus,
            Guid merchantId,
            Guid orderId,
            Code psp,
            Guid? pspConnectionId,
            Guid? providerAccountId,
            Guid? secretVersionId,
            PspEnvironment? environment,
            string? externalChargeId,
            Money amount,
            PspChargeStatus? providerStatus,
            Money? collectedAmount,
            string? pspEventId,
            DateTime occurredAt)
        {
            if (!ReferenceEquals(constructionToken, PreparedConstructionToken))
                throw new InvalidOperationException("Prepared confirmation evidence must be created by the service.");

            SessionId = sessionId;
            PreparedSessionStatus = preparedSessionStatus;
            MerchantId = merchantId;
            OrderId = orderId;
            Psp = psp;
            PspConnectionId = pspConnectionId;
            ProviderAccountId = providerAccountId;
            SecretVersionId = secretVersionId;
            Environment = environment;
            ExternalChargeId = externalChargeId;
            Amount = amount;
            ProviderStatus = providerStatus;
            CollectedAmount = collectedAmount;
            PspEventId = pspEventId;
            OccurredAt = occurredAt;
        }

        internal Guid SessionId { get; }
        internal SessionStatus PreparedSessionStatus { get; }
        internal Guid MerchantId { get; }
        internal Guid OrderId { get; }
        internal Code Psp { get; }
        internal Guid? PspConnectionId { get; }
        internal Guid? ProviderAccountId { get; }
        internal Guid? SecretVersionId { get; }
        internal PspEnvironment? Environment { get; }
        internal string? ExternalChargeId { get; }
        internal Money Amount { get; }
        internal PspChargeStatus? ProviderStatus { get; }
        internal Money? CollectedAmount { get; }
        internal string? PspEventId { get; }
        internal DateTime OccurredAt { get; }

    }

    private const string IdempotencyContext = "payment-confirmation";

    private readonly IConnectionRepository _connections;
    private readonly IPspAdapterFactory _adapters;
    private readonly IVaultSecretStore _vault;
    private readonly IIdempotencyStore _idempotency;
    private readonly IOutbox _outbox;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ISessionRepository _sessions;
    private readonly IClock _clock;
    private readonly ILogger<PaymentConfirmationService> _logger;

    public PaymentConfirmationService(
        IConnectionRepository connections,
        IPspAdapterFactory adapters,
        IVaultSecretStore vault,
        IIdempotencyStore idempotency,
        IOutbox outbox,
        IUnitOfWork unitOfWork,
        ISessionRepository sessions,
        IClock clock,
        ILogger<PaymentConfirmationService> logger)
    {
        _connections = connections;
        _adapters = adapters;
        _vault = vault;
        _idempotency = idempotency;
        _outbox = outbox;
        _unitOfWork = unitOfWork;
        _sessions = sessions;
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

        var prepared = await PrepareAsync(session, access, pspEventId, cancellationToken).ConfigureAwait(false);
        return await ApplyPreparedAsync(prepared, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Resolves credentials and asks the PSP without opening or mutating a database transaction.</summary>
    internal async Task<PreparedConfirmation> PrepareAsync(
        Session session,
        PspAccess? access,
        string? pspEventId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        var now = _clock.UtcNow;

        // A Created session without a charge has not reached the PSP. Redirected without a charge is an
        // in-flight claim because BeginRedirect is committed before PSP I/O, so it is not offline proof.
        if (session.PspExternalChargeId is not { } chargeId)
            return CreateOfflineEvidence(session, now);

        access ??= await ResolveAccessAsync(session, cancellationToken).ConfigureAwait(false);
        ValidateAccess(session, access.Connection);

        // Provider I/O is deliberately before ApplyPreparedAsync opens the short write transaction.
        var environment = session.PspEnvironment ?? access.Connection.ActiveSecretEnvironment;
        var confirmed = await _adapters.For(session.Psp)
            .FetchChargeAsync(chargeId, access.Secret, environment, cancellationToken)
            .ConfigureAwait(false);

        return CreateProviderEvidence(
            session, access.Connection, chargeId, confirmed, pspEventId, environment, now);
    }

    private static PreparedConfirmation CreateOfflineEvidence(Session session, DateTime occurredAt) =>
        new(
            PreparedConstructionToken,
            session.Id,
            session.Status,
            session.MerchantId,
            session.OrderId,
            session.Psp,
            session.PspConnectionId,
            providerAccountId: null,
            session.SecretVersionId,
            session.PspEnvironment,
            externalChargeId: null,
            session.Amount,
            providerStatus: null,
            collectedAmount: null,
            pspEventId: null,
            occurredAt);

    private static PreparedConfirmation CreateProviderEvidence(
        Session session,
        Connection connection,
        string chargeId,
        PspChargeConfirmation result,
        string? pspEventId,
        PspEnvironment environment,
        DateTime occurredAt) =>
        new(
            PreparedConstructionToken,
            session.Id,
            session.Status,
            session.MerchantId,
            session.OrderId,
            session.Psp,
            connection.Id,
            connection.PaymentProviderId,
            session.SecretVersionId,
            environment,
            chargeId,
            session.Amount,
            result.Status,
            result.Amount,
            pspEventId,
            occurredAt);

    /// <summary>Locks/reloads the current session, revalidates provider evidence, and applies it atomically.</summary>
    internal Task<ConfirmationOutcome> ApplyPreparedAsync(
        PreparedConfirmation prepared,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prepared);

        return _unitOfWork.ExecuteInTransactionAsync(
            ct => ApplyPreparedInTransactionAsync(prepared, ct), cancellationToken);
    }

    private async Task<ConfirmationOutcome> ApplyPreparedInTransactionAsync(
        PreparedConfirmation prepared,
        CancellationToken cancellationToken)
    {
        var session = await _sessions
            .GetByIdForUpdateAsync(prepared.SessionId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Payment session disappeared before confirmation apply.");

        ValidatePreparedSession(session, prepared);

        if (prepared.ProviderStatus is not null)
        {
            var connection = prepared.PspConnectionId is { } connectionId
                ? await _connections.GetByIdAsync(connectionId, cancellationToken).ConfigureAwait(false)
                : null;
            ValidatePreparedConnection(connection, session, prepared);
        }

        return prepared.ProviderStatus switch
        {
            PspChargeStatus.Paid => await ConfirmPaidAsync(
                session,
                prepared.PspConnectionId!.Value,
                prepared.ExternalChargeId!,
                prepared.CollectedAmount,
                prepared.PspEventId,
                prepared.OccurredAt,
                cancellationToken).ConfigureAwait(false),
            PspChargeStatus.Failed => await FailAsync(session, prepared.OccurredAt, cancellationToken).ConfigureAwait(false),
            PspChargeStatus.Pending => await ExpireIfStaleAsync(session, _clock.UtcNow, cancellationToken).ConfigureAwait(false),
            null => await ApplyOfflineEvidenceAsync(session, _clock.UtcNow, cancellationToken).ConfigureAwait(false),
            _ => throw new InvalidOperationException("Unknown PSP confirmation status."),
        };
    }

    private static void ValidateAccess(Session session, Connection connection)
    {
        if (connection.MerchantId != session.MerchantId
            || connection.Psp != session.Psp
            || (session.PspConnectionId is { } pinned && pinned != connection.Id))
            throw new InvalidOperationException("PSP access does not match the payment session.");
    }

    private static void ValidatePreparedSession(Session session, PreparedConfirmation prepared)
    {
        if (session.Id != prepared.SessionId
            || session.MerchantId != prepared.MerchantId
            || session.OrderId != prepared.OrderId
            || session.Psp != prepared.Psp
            || session.Amount != prepared.Amount
            || session.PspConnectionId is { } pinnedConnectionId
                && pinnedConnectionId != prepared.PspConnectionId
            || session.SecretVersionId is { } pinnedSecretVersionId
                && pinnedSecretVersionId != prepared.SecretVersionId
            || session.PspEnvironment is { } pinnedEnvironment
                && pinnedEnvironment != prepared.Environment
            || !string.Equals(session.PspExternalChargeId, prepared.ExternalChargeId, StringComparison.Ordinal)
            || prepared.ProviderStatus is null
                && Terminal(session.Status) is null
                && session.Status != prepared.PreparedSessionStatus)
            throw new InvalidOperationException("Prepared payment confirmation no longer matches the payment session.");
    }

    private static void ValidatePreparedConnection(
        Connection? connection,
        Session session,
        PreparedConfirmation prepared)
    {
        if (connection is null
            || prepared.PspConnectionId is not { } connectionId
            || connection.Id != connectionId
            || connection.MerchantId != prepared.MerchantId
            || connection.Psp != prepared.Psp
            || connection.PaymentProviderId != prepared.ProviderAccountId
            || session.PspEnvironment is null
                && connection.ActiveSecretEnvironment != prepared.Environment)
            throw new InvalidOperationException("Prepared payment confirmation no longer matches the PSP connection.");
    }

    private Task<ConfirmationOutcome> ApplyOfflineEvidenceAsync(
        Session session,
        DateTime now,
        CancellationToken cancellationToken)
    {
        if (Terminal(session.Status) is { } terminal)
            return Task.FromResult(terminal);

        // A Redirected session without a charge is an in-flight PSP claim, not proof that no charge exists.
        // Only an unchanged Created session is decidably chargeless and eligible for age-based expiry.
        return session.Status == SessionStatus.Created
            ? ExpireIfStaleAsync(session, now, cancellationToken)
            : Task.FromResult(ConfirmationOutcome.Pending);
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
        var connection = session.PspConnectionId is { } pinnedConnectionId
            ? await _connections.GetByIdAsync(pinnedConnectionId, cancellationToken).ConfigureAwait(false)
            : await _connections.GetAsync(session.MerchantId, session.Psp, cancellationToken).ConfigureAwait(false);
        if (connection is null)
            throw new InvalidOperationException(
                $"No PSP connection for merchant {session.MerchantId} and PSP {session.Psp}.");

        // Reveal the version the SESSION pinned, not the connection's current active version: a rotation
        // retires but keeps the pinned version readable, so this attempt keeps verifying/fetching against
        // the secret it was created with (merchant-psp-settings AC-4.3/6.5). Secret and environment must
        // come from one source — the pinned version reads under the pinned environment above. A legacy
        // version-0 session (no pinned version) falls back to the connection's active version / ref.
        var secret = session.SecretVersionId is { } pinnedVersion
            ? await _vault.ReadVersionForServerAsync(session.MerchantId, pinnedVersion, cancellationToken).ConfigureAwait(false)
            : connection.ActiveSecretVersionId is { } versionId
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
