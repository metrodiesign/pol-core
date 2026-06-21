using BuildingBlocks.Application;
using Mediator;
using Payments.Application.Ports;

namespace Payments.Application.StartRedirect;

/// <summary>
/// Loads the session, reveals the connection secret from the vault, asks the PSP adapter for a hosted
/// charge, binds it to the session once, and returns the redirect URL. The secret is used only for the
/// server-side PSP call and is never returned to the caller or logged.
/// </summary>
public sealed class StartRedirectHandler : ICommandHandler<StartRedirectCommand, StartRedirectResult>
{
    private readonly IPaymentSessionRepository _sessions;
    private readonly IPspConnectionRepository _connections;
    private readonly IPspAdapterFactory _adapters;
    private readonly IVaultSecretStore _vault;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;

    public StartRedirectHandler(
        IPaymentSessionRepository sessions,
        IPspConnectionRepository connections,
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
            ?? throw new InvalidOperationException($"PaymentSession {command.PaymentSessionId} not found.");

        var connection = await _connections.GetAsync(session.TenantId, session.Psp, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"No PSP connection for tenant {session.TenantId} and PSP {session.Psp}.");

        var secret = await _vault.RevealAsync(session.TenantId, connection.SecretRefName, cancellationToken).ConfigureAwait(false);

        var adapter = _adapters.For(session.Psp);
        var charge = await adapter.CreateRedirectChargeAsync(session, secret, cancellationToken).ConfigureAwait(false);

        session.AttachPspCharge(charge.ExternalChargeId, charge.RedirectUrl, _clock.UtcNow);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new StartRedirectResult(charge.RedirectUrl);
    }
}
