using BuildingBlocks.Application;
using Mediator;
using Payments.Application.Capabilities;
using Payments.Application.Confirmation;
using Payments.Application.Ports;
using Payments.Application.Ports.Psp;
using Payments.Domain;
using Payments.Domain.Psp;

namespace Payments.Application.StartRedirect;

/// <summary>
/// Starts a redirect for a payment session. To avoid orphaning PSP charges under concurrent
/// retries/clicks (PLAN #11), it CLAIMS the redirect first — transitioning the session to
/// <see cref="SessionStatus.Redirected"/> and saving under the optimistic-concurrency token — and only
/// the request that wins that claim goes on to create the hosted charge with the PSP and bind it. A
/// concurrent loser returns the winner's redirect URL instead of minting a second charge. The secret is
/// used only for the server-side PSP call and is never returned to the caller or logged.
///
/// The order of the steps is itself contract (captive-payment-alignment REQ-3.5/REQ-7): every refusal
/// happens BEFORE the claim, so a rejected request leaves the session untouched, and a charge the PSP
/// refuses fails the session instead of stranding it at <see cref="SessionStatus.Redirected"/> with no
/// URL — a state no later call can redirect from and none can replace, i.e. a permanently unpayable order.
///
/// A failed charge is only failed when the failure PROVES no charge exists
/// (<see cref="PspRejectedException"/>). An ambiguous one — timeout, transport fault, 5xx, an unreadable
/// response — leaves the claim standing, because the PSP may hold a charge keyed to this session and a
/// replacement session would carry a new key, i.e. a second charge (REQ-7.5). Such a claim is settled by the
/// next redirect call, which re-runs the charge under the SAME key (both adapters derive it from
/// <c>Session.Id</c>), gets the original charge back and binds it, so its webhook can correlate (REQ-7.6).
/// </summary>
public sealed class StartRedirectHandler : ICommandHandler<StartRedirectCommand, StartRedirectResult>
{
    private readonly ISessionRepository _sessions;
    private readonly IConnectionRepository _connections;
    private readonly IPspAdapterFactory _adapters;
    private readonly IVaultSecretStore _vault;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;
    private readonly PaymentConfirmationService _confirmation;
    private readonly IPayableOrderReader _orders;
    private readonly IPaymentAuthorizationLockManager _authorizationLocks;
    private readonly IEffectivePaymentCapabilityResolver _capabilities;

    public StartRedirectHandler(
        ISessionRepository sessions,
        IConnectionRepository connections,
        IPspAdapterFactory adapters,
        IVaultSecretStore vault,
        IUnitOfWork unitOfWork,
        IClock clock,
        PaymentConfirmationService confirmation,
        IPayableOrderReader orders,
        IPaymentAuthorizationLockManager authorizationLocks,
        IEffectivePaymentCapabilityResolver capabilities)
    {
        _sessions = sessions;
        _connections = connections;
        _adapters = adapters;
        _vault = vault;
        _unitOfWork = unitOfWork;
        _clock = clock;
        _confirmation = confirmation;
        _orders = orders;
        _authorizationLocks = authorizationLocks;
        _capabilities = capabilities;
    }

    public async ValueTask<StartRedirectResult> Handle(
        StartRedirectCommand command,
        CancellationToken cancellationToken)
    {
        var session = await _sessions.GetByIdAsync(command.PaymentSessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException($"PaymentSession {command.PaymentSessionId} not found.");
        if (command.ExpectedVersion is { } expected && session.Version != expected)
            throw new ConcurrencyConflictException("Payment session changed after it was read.");

        // Idempotent re-entry: a session already redirected (e.g. a retried click) returns its existing
        // hosted URL — never a second PSP charge.
        if (session is { Status: SessionStatus.Redirected, RedirectUrl: not null })
            return new StartRedirectResult(session.RedirectUrl);

        // Redirected with no URL is a claim whose charge ended ambiguously: the PSP may hold a charge keyed to
        // this session. Settle it by calling the PSP again under that same key (never BeginRedirect again) —
        // it returns the original charge, which gets bound here so its webhook can correlate (REQ-7.6).
        var settlingClaim = session.Status == SessionStatus.Redirected;

        if (!settlingClaim && session.Status != SessionStatus.Created)
            throw new InvalidOperationException(
                $"PaymentSession {session.Id} cannot start a redirect from status {session.Status}.");

        Connection connection;
        if (!settlingClaim)
        {
            try
            {
                connection = await _unitOfWork.ExecuteInTransactionAsync(
                    ct => ClaimFirstRedirectAsync(session, ct), cancellationToken).ConfigureAwait(false);
            }
            catch (ConcurrencyConflictException)
            {
                var winner = await _sessions.GetByIdAsync(command.PaymentSessionId, cancellationToken).ConfigureAwait(false);
                if (winner?.RedirectUrl is not null)
                    return new StartRedirectResult(winner.RedirectUrl);

                throw new InvalidOperationException(
                    $"PaymentSession {command.PaymentSessionId} redirect is already in progress; retry shortly.");
            }
        }
        else
        {
            // Existing claim may already represent an external charge. Current authorization state cannot
            // cancel it; load only routing material and settle under the same Session idempotency key.
            connection = await _connections.GetAsync(session.MerchantId, session.Psp, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"No PSP connection for merchant {session.MerchantId} and PSP {session.Psp}.");
        }

        // Both failure paths below may only fail the session while `!settlingClaim`: on the settling path a
        // charge may already exist at the PSP, and a failed session lets the order open a replacement whose id
        // is a new idempotency key — a second charge (REQ-7.5).
        string secret;
        try
        {
            secret = connection.ActiveSecretVersionId is { } versionId
                ? await _vault.ReadVersionForServerAsync(session.MerchantId, versionId, cancellationToken).ConfigureAwait(false)
                : await _vault.RevealAsync(session.MerchantId, connection.SecretRefName, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (!settlingClaim)
        {
            // Nothing has been sent to the PSP yet, so this is definitive: fail the claim rather than leave it
            // standing with no URL, which is the state REQ-7.2 forbids and which the one-open-session index
            // would otherwise make permanent.
            await FailSessionAsync(session, "pre_charge_failure").ConfigureAwait(false);
            throw;
        }

        PspCharge charge;
        try
        {
            charge = await _adapters.For(session.Psp)
                .CreateRedirectChargeAsync(session, connection.Id, secret, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (PspRejectedException) when (!settlingClaim)
        {
            // Proven charge-less: the request never left us or the PSP refused it outright. Failing the session
            // is what lets create-session open a fresh attempt for the order (REQ-7.1/7.4). Every OTHER failure
            // is ambiguous and deliberately uncaught — the claim survives and the next call settles it.
            await FailSessionAsync(session, "psp_rejected").ConfigureAwait(false);
            throw;
        }

        session.SetPspCharge(charge.ExternalChargeId, charge.RedirectUrl, _clock.UtcNow);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new StartRedirectResult(charge.RedirectUrl);
    }

    private async Task<Connection> ClaimFirstRedirectAsync(Session session, CancellationToken cancellationToken)
    {
        await _authorizationLocks.AcquireMerchantSharedAsync(session.MerchantId, cancellationToken)
            .ConfigureAwait(false);

        var order = await _orders.GetForMintAsync(session.OrderId, cancellationToken).ConfigureAwait(false)
            ?? throw new ConflictException(
                "Payment Session has no trusted Order authorization context.",
                "payment_authorization_context_missing");
        if (order.MerchantId != session.MerchantId
            || order.PaymentChannel is null
            || !string.Equals(order.PaymentChannel, session.Method, StringComparison.Ordinal))
            throw new ConflictException(
                "Payment Session does not match its authoritative Order.", "payment_method_mismatch");
        if (!order.CanOpenPaymentAttempt)
            throw new ConflictException(
                "Order cannot start a payment redirect from its current status.", "order_not_payable");

        var connection = await _connections.GetAsync(session.MerchantId, session.Psp, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"No PSP connection for merchant {session.MerchantId} and PSP {session.Psp}.");
        connection.EnsureEligible(session.Method);

        var subject = order.InitiatingAudience switch
        {
            PaymentAudience.User when order.InitiatingMerchantUserId is not null =>
                new PaymentCapabilitySubject(order.MerchantId, PaymentAudience.User,
                    order.InitiatingMerchantUserId),
            PaymentAudience.PlatformAdmin when order.InitiatingMerchantUserId is null =>
                new PaymentCapabilitySubject(order.MerchantId, PaymentAudience.PlatformAdmin, null),
            _ => throw new ConflictException(
                "Order has no trusted payment authorization context.",
                "payment_authorization_context_missing"),
        };
        var decision = await _capabilities.ResolveMethodAsync(
            new ResolvePaymentMethod(subject, session.Method, session.Psp.ToCode()), cancellationToken)
            .ConfigureAwait(false);
        if (!decision.Allowed)
        {
            if (decision.Denial is PaymentCapabilityDenial.UserNotActive or PaymentCapabilityDenial.UserPolicyDenied)
                throw new AccessDeniedException(
                    "Payment method is not allowed for this Merchant User.", "payment_method_not_allowed");
            throw new ConflictException(
                "Payment method capability is unavailable.", "payment_capability_unavailable");
        }

        // Transaction commits this claim before caller touches PSP. Exclusive revoke/status writers cannot
        // commit between resolver decision and claim because they use the same authorization lock resource.
        session.BeginRedirect(_clock.UtcNow);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    /// <summary>
    /// Marks the session <see cref="SessionStatus.Failed"/> and persists that best-effort. The reason carries
    /// only the exception type and its message (the adapters name the PSP, a status or a decline code) — never
    /// the revealed secret. Deliberately NOT saved under the request's cancellation token: a caller that
    /// walked away is exactly when this matters, and saving under an already-cancelled token would leave the
    /// stuck session REQ-7.2 forbids. Deliberately does not surface its own fault either — the charge failure
    /// is the caller's real answer, and reporting a database error for a charge the PSP refused would hide the
    /// cause and change the status code.
    /// </summary>
    private async Task FailSessionAsync(Session session, string reasonCode)
    {
        try
        {
            await _confirmation
                .MarkFailedAsync(session, reasonCode, _clock.UtcNow, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Nothing further to do here: the store keeps the claim, and the charge failure is what the caller
            // must see. Losing this transition is the one path that can still strand a session (a store that
            // is refusing writes), which is a DB-availability incident, not a decision this handler can make.
        }
    }
}
