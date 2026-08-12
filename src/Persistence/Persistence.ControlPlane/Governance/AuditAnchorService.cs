using System.Security.Cryptography;
using Governance.Application;
using Governance.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Persistence.ControlPlane.Governance;

internal sealed class AuditAnchorHealthState
{
    private long _lastHealthyTicks;
    private int _failed;

    public void MarkHealthy(DateTime now)
    {
        Interlocked.Exchange(ref _lastHealthyTicks, now.Ticks);
        Volatile.Write(ref _failed, 0);
    }

    public void MarkFailed() => Volatile.Write(ref _failed, 1);

    public bool IsHealthy(DateTime now) =>
        Volatile.Read(ref _failed) == 0
        && Interlocked.Read(ref _lastHealthyTicks) is var ticks
        && ticks > 0
        && now - new DateTime(ticks, DateTimeKind.Utc) <= TimeSpan.FromSeconds(30);
}

internal sealed class AuditAnchorReadinessCheck(
    IAuditAnchorStore store,
    AuditAnchorHealthState state) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(!store.IsEnabled || state.IsHealthy(DateTime.UtcNow)
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("audit anchor is unavailable or stale"));
}

internal sealed class AuditAnchorService(
    IServiceScopeFactory scopeFactory,
    IAuditAnchorStore store,
    AuditAnchorHealthState health,
    ILogger<AuditAnchorService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!store.IsEnabled)
        {
            health.MarkHealthy(DateTime.UtcNow);
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await AnchorAndVerifyAsync(stoppingToken);
                health.MarkHealthy(DateTime.UtcNow);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                health.MarkFailed();
                logger.LogError(ex, "Audit anchor refresh or verification failed.");
            }

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task AnchorAndVerifyAsync(CancellationToken cancellationToken)
    {
        var existing = await store.ReadAllLatestAsync(cancellationToken);
        List<AuditHeadProjection> heads;
        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
            heads = await db.AuditHeads.AsNoTracking()
                .Where(x => x.LastSequence > 0)
                .Select(x => new AuditHeadProjection(x.Id, x.LastSequence, x.LastHash))
                .ToListAsync(cancellationToken);

            if (existing.Keys.Any(scopeKey => heads.All(x => x.ScopeKey != scopeKey)))
                throw new AuditIntegrityException("An anchored audit scope is missing from the database.");
            foreach (var head in heads)
                await VerifyDatabaseTailAsync(db, head, existing.GetValueOrDefault(head.ScopeKey), cancellationToken);
        }

        foreach (var head in heads)
        {
            await store.AppendAsync(new AuditAnchorCheckpoint(
                head.ScopeKey,
                head.Sequence,
                Convert.ToHexString(head.Hash).ToLowerInvariant(),
                DateTime.UtcNow), cancellationToken);
        }

        var anchors = await store.ReadAllLatestAsync(cancellationToken);
        if (anchors.Count != heads.Count)
            throw new AuditIntegrityException("Audit anchor and database scope inventories differ.");
        foreach (var head in heads)
        {
            if (!anchors.TryGetValue(head.ScopeKey, out var anchor)
                || anchor.Sequence != head.Sequence
                || !CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(anchor.Hash), head.Hash))
                throw new AuditIntegrityException("Audit anchor does not match the database head.");
        }
    }

    private static async Task VerifyDatabaseTailAsync(
        ControlPlaneDbContext db,
        AuditHeadProjection head,
        AuditAnchorCheckpoint? anchor,
        CancellationToken cancellationToken)
    {
        var prior = AuditRecord.Genesis;
        long expected = 1;
        if (anchor is not null)
        {
            if (anchor.Sequence > head.Sequence || anchor.Hash.Length != 64)
                throw new AuditIntegrityException("Audit anchor is ahead of or malformed for the database head.");
            byte[] anchoredHash;
            try
            {
                anchoredHash = Convert.FromHexString(anchor.Hash);
            }
            catch (FormatException)
            {
                throw new AuditIntegrityException("Audit anchor hash is malformed.");
            }
            var anchoredRecord = await db.AuditRecords.AsNoTracking().SingleOrDefaultAsync(
                x => x.ScopeKey == head.ScopeKey && x.Sequence == anchor.Sequence,
                cancellationToken);
            if (anchoredRecord is null
                || !CryptographicOperations.FixedTimeEquals(anchoredRecord.Hash, anchoredHash)
                || !anchoredRecord.HasValidHash())
                throw new AuditIntegrityException("Database history no longer matches its signed audit anchor.");
            prior = anchoredHash;
            expected = anchor.Sequence + 1;
        }

        var tail = await db.AuditRecords.AsNoTracking()
            .Where(x => x.ScopeKey == head.ScopeKey && x.Sequence >= expected)
            .OrderBy(x => x.Sequence)
            .ToListAsync(cancellationToken);
        foreach (var record in tail)
        {
            if (record.Sequence != expected
                || !CryptographicOperations.FixedTimeEquals(record.PreviousHash, prior)
                || !record.HasValidHash())
                throw new AuditIntegrityException("Audit database tail verification failed before anchoring.");
            prior = record.Hash;
            expected++;
        }
        if (head.Sequence != expected - 1
            || !CryptographicOperations.FixedTimeEquals(head.Hash, prior))
            throw new AuditIntegrityException("Audit database head does not match the verified tail.");
    }

    private sealed record AuditHeadProjection(string ScopeKey, long Sequence, byte[] Hash);
}
