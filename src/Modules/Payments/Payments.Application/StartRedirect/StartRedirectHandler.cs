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
        var connection = await _connections.GetAsync(session.MerchantId, session.Psp, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"No PSP connection for merchant {session.MerchantId} and PSP {session.Psp}.");

        var secret = await _vault.RevealAsync(session.MerchantId, connection.SecretRefName, cancellationToken).ConfigureAwait(false);

        var adapter = _adapters.For(session.Psp);
        var charge = await adapter.CreateRedirectChargeAsync(session, secret, cancellationToken).ConfigureAwait(false);

        session.SetPspCharge(charge.ExternalChargeId, charge.RedirectUrl, _clock.UtcNow);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new StartRedirectResult(charge.RedirectUrl);
    }
}
