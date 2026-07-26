using BuildingBlocks.Application;
using Mediator;
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
/// </summary>
public sealed class StartRedirectHandler : ICommandHandler<StartRedirectCommand, StartRedirectResult>
{
    private readonly ISessionRepository _sessions;
    private readonly IConnectionRepository _connections;
    private readonly IPspAdapterFactory _adapters;
    private readonly IVaultSecretStore _vault;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;

    public StartRedirectHandler(
        ISessionRepository sessions,
        IConnectionRepository connections,
        IPspAdapterFactory adapters,
        IVaultSecretStore vault,
        IUnitOfWork unitOfWork,
        IClock clock)
    {
        _sessions = sessions;
        _connections = connections;
        _adapters = adapters;
        _vault = vault;
        _unitOfWork = unitOfWork;
        _clock = clock;
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

        if (session.Status != SessionStatus.Created)
            throw new InvalidOperationException(
                $"PaymentSession {session.Id} cannot start a redirect from status {session.Status}.");

        // Eligibility is re-checked HERE, before the claim: the connection may have been disabled or its
        // enabled methods narrowed between create-session and now (REQ-3.5), and a request refused for that
        // must leave the session exactly as it found it (REQ-7.3). Refusing AFTER the claim — as this handler
        // used to — stranded the session at Redirected with no URL, which is 409 forever.
        var connection = await _connections.GetAsync(session.MerchantId, session.Psp, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"No PSP connection for merchant {session.MerchantId} and PSP {session.Psp}.");

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

        // This request owns the claim: create the hosted charge and bind it once.
        var secret = await _vault.RevealAsync(session.MerchantId, connection.SecretRefName, cancellationToken).ConfigureAwait(false);

        var adapter = _adapters.For(session.Psp);
        PspCharge charge;
        try
        {
            charge = await adapter.CreateRedirectChargeAsync(session, secret, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            // The attempt is over, so the claim must not outlive it (REQ-7.1/7.2). Failing the session here is
            // also what lets create-session open a fresh one for the same order (REQ-7.4). The reason carries
            // only the exception type and the adapter's own message (PSP code + HTTP status) — never the
            // revealed secret, and never the request body it was signed into.
            session.MarkFailed($"{failure.GetType().Name}: {failure.Message}", _clock.UtcNow);
            await PersistFailureAsync().ConfigureAwait(false);
            throw;
        }

        session.SetPspCharge(charge.ExternalChargeId, charge.RedirectUrl, _clock.UtcNow);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new StartRedirectResult(charge.RedirectUrl);
    }

    /// <summary>
    /// Records the <see cref="SessionStatus.Failed"/> transition best-effort. Deliberately NOT under the
    /// request's cancellation token: a caller that walked away is exactly when this matters, and saving under
    /// an already-cancelled token would leave behind the stuck session REQ-7.2 forbids. Deliberately does not
    /// surface its own fault either — the PSP failure is the caller's real answer, and reporting a database
    /// error for a charge the PSP actually declined would hide the cause and change the status code.
    /// </summary>
    private async Task PersistFailureAsync()
    {
        try
        {
            await _unitOfWork.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Nothing further to do here: the store keeps the claim, and the charge failure is what the caller
            // must see. Losing this transition is the one path that can still strand a session (a store that
            // is refusing writes), which is a DB-availability incident, not a decision this handler can make.
        }
    }
}
