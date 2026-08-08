using BuildingBlocks.Application;
using Mediator;
using Payments.Application.Confirmation;
using Payments.Application.Ports;
using Payments.Application.Ports.Psp;
using Payments.Domain;

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

    public StartRedirectHandler(
        ISessionRepository sessions,
        IConnectionRepository connections,
        IPspAdapterFactory adapters,
        IVaultSecretStore vault,
        IUnitOfWork unitOfWork,
        IClock clock,
        PaymentConfirmationService confirmation)
    {
        _sessions = sessions;
        _connections = connections;
        _adapters = adapters;
        _vault = vault;
        _unitOfWork = unitOfWork;
        _clock = clock;
        _confirmation = confirmation;
    }

    public async ValueTask<StartRedirectResult> Handle(
        StartRedirectCommand command,
        CancellationToken cancellationToken)
    {
        var session = await _sessions.GetByIdAsync(command.PaymentSessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException($"PaymentSession {command.PaymentSessionId} not found.");

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

        var connection = await _connections.GetAsync(session.MerchantId, session.Psp, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"No PSP connection for merchant {session.MerchantId} and PSP {session.Psp}.");

        if (!settlingClaim)
        {
            // Eligibility is re-checked HERE, before the claim: the connection may have been disabled or its
            // enabled methods narrowed between create-session and now (REQ-3.5), and a request refused for
            // that must leave the session exactly as it found it (REQ-7.3). Refusing AFTER the claim — as this
            // handler used to — stranded the session at Redirected with no URL, which is 409 forever. It gates
            // NEW claims only: a settling call is finishing an attempt the PSP may already have taken.
            connection.EnsureEligible(session.Method);

            // Claim the redirect BEFORE touching the PSP. The rowversion token makes this save atomic, so a
            // concurrent duplicate loses the claim here and never creates a charge.
            session.BeginRedirect(_clock.UtcNow);
            try
            {
                await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
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

        // Both failure paths below may only fail the session while `!settlingClaim`: on the settling path a
        // charge may already exist at the PSP, and a failed session lets the order open a replacement whose id
        // is a new idempotency key — a second charge (REQ-7.5).
        string secret;
        try
        {
            secret = await _vault.RevealAsync(session.MerchantId, connection.SecretRefName, cancellationToken).ConfigureAwait(false);
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
