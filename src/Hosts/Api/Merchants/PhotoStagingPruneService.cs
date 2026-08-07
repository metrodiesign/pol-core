using Merchants.Application.Users;
using Merchants.Infrastructure;

namespace Api.Merchants;

/// <summary>Periodically sweeps staged KYC/photo objects past <see cref="LocalPhotoStore.StagingTtl"/>, independent
/// of new upload traffic. <see cref="LocalPhotoStore.PutStagedAsync"/> only sweeps as a side effect of a NEW
/// staging call — if a process crashes after staging and no later upload ever arrives, that sweep never runs and
/// the advertised 24-hour retention bound silently does not hold (Codex review #191). No-ops when a future
/// production adapter (e.g. S3/Blob, which has its own native lifecycle policy) is wired instead of the local
/// dev/self-host store.</summary>
internal sealed class PhotoStagingPruneService : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Period = TimeSpan.FromHours(1);

    private readonly IPhotoStore _photos;
    private readonly ILogger<PhotoStagingPruneService> _logger;

    public PhotoStagingPruneService(IPhotoStore photos, ILogger<PhotoStagingPruneService> logger)
    {
        _photos = photos;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_photos is not LocalPhotoStore local)
            return;

        try
        {
            await Task.Delay(InitialDelay, stoppingToken); // let the host settle before the first sweep
            using var timer = new PeriodicTimer(Period);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await local.PruneExpiredStagedAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Photo staging prune sweep failed; retrying next tick.");
                }

                if (!await timer.WaitForNextTickAsync(stoppingToken))
                    break;
            }
        }
        catch (OperationCanceledException) { /* host stopping — normal shutdown */ }
    }
}
