using BuildingBlocks.Application;
using Merchants.Application;

namespace Api;

/// <summary>Periodically deletes merchant-user sessions past their absolute expiry so the table does not grow
/// unbounded (REQ-10.4). Runs in the API host because the prune writes through the keyed pol_admin context (the
/// Worker connects as pol_worker, which has no grant on the control-plane merchant-user session tables).</summary>
// ponytail: DUPLICATE of Api.AdminSessionPruneService (IAdminSessionStore -> IMerchantUserSessionStore) — deliberate debt.
internal sealed class MerchantUserSessionPruneService : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Period = TimeSpan.FromHours(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IClock _clock;
    private readonly ILogger<MerchantUserSessionPruneService> _logger;

    public MerchantUserSessionPruneService(IServiceScopeFactory scopeFactory, IClock clock, ILogger<MerchantUserSessionPruneService> logger)
    {
        _scopeFactory = scopeFactory;
        _clock = clock;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(InitialDelay, stoppingToken); // let the host settle before the first sweep
            using var timer = new PeriodicTimer(Period);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var store = scope.ServiceProvider.GetRequiredService<IMerchantUserSessionStore>();
                    var removed = await store.PruneAsync(_clock.UtcNow, stoppingToken);
                    if (removed > 0)
                        _logger.LogInformation("Pruned {Count} merchant-user sessions past absolute expiry.", removed);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Merchant-user session prune sweep failed; retrying next tick.");
                }

                if (!await timer.WaitForNextTickAsync(stoppingToken))
                    break;
            }
        }
        catch (OperationCanceledException) { /* host stopping — normal shutdown */ }
    }
}
