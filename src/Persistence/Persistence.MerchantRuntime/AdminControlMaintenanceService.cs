using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Vault;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Persistence.MerchantRuntime;

internal sealed class AdminControlMaintenanceService(
    IServiceScopeFactory scopeFactory,
    ILogger<AdminControlMaintenanceService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<MerchantRuntimeDbContext>();
                var now = scope.ServiceProvider.GetRequiredService<IClock>().UtcNow;
                var expiredOperations = await db.AdminOperationRecords.IgnoreQueryFilters()
                    .Where(x => x.ExpiresAt < now).OrderBy(x => x.ExpiresAt).Take(1000).ToListAsync(stoppingToken);
                db.AdminOperationRecords.RemoveRange(expiredOperations);

                var references = await db.PspConnections.IgnoreQueryFilters()
                    .Where(x => x.ActiveSecretVersionId != null || x.PendingSecretVersionId != null)
                    .Select(x => new { x.ActiveSecretVersionId, x.PendingSecretVersionId })
                    .ToListAsync(stoppingToken);
                var referencedVersions = references
                    .SelectMany(x => new[] { x.ActiveSecretVersionId, x.PendingSecretVersionId })
                    .Where(x => x.HasValue).Select(x => x!.Value).ToHashSet();
                var expiredStages = await db.VaultSecretVersions.IgnoreQueryFilters()
                    .Where(x => x.State == VaultSecretVersionState.Staged && x.ExpiresAt < now
                                && !referencedVersions.Contains(x.Id))
                    .OrderBy(x => x.ExpiresAt).Take(1000).ToListAsync(stoppingToken);
                foreach (var version in expiredStages)
                    version.Discard(now);

                if (expiredOperations.Count > 0 || expiredStages.Count > 0)
                    await db.SaveChangesAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Admin control-plane maintenance failed.");
            }

            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
