using Admins.Application;
using BuildingBlocks.Application;

namespace Api;

/// <summary>Periodically deletes admin sessions past their absolute expiry so the table does not grow unbounded
/// (REQ-11.5). Runs in the API host because the prune writes through the keyed pol_admin context (the Worker
/// connects as pol_worker, which has no grant on the control-plane session tables).</summary>
internal sealed class PlatformUserSessionPruneService : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Period = TimeSpan.FromHours(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IClock _clock;
    private readonly ILogger<PlatformUserSessionPruneService> _logger;

    public PlatformUserSessionPruneService(IServiceScopeFactory scopeFactory, IClock clock, ILogger<PlatformUserSessionPruneService> logger)
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
                    var store = scope.ServiceProvider.GetRequiredService<IPlatformUserSessionStore>();
                    var removed = await store.PruneAsync(_clock.UtcNow, stoppingToken);
                    if (removed > 0)
                        _logger.LogInformation("Pruned {Count} admin sessions past absolute expiry.", removed);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Admin session prune sweep failed; retrying next tick.");
                }

                if (!await timer.WaitForNextTickAsync(stoppingToken))
                    break;
            }
        }
        catch (OperationCanceledException) { /* host stopping — normal shutdown */ }
    }
}
