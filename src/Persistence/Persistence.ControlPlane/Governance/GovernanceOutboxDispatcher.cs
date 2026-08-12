using System.Text.Json;
using System.Text.Json.Serialization;
using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Outbox;
using Contracts;
using Governance.Domain;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Persistence.ControlPlane.Governance;

internal static class GovernanceOutboxEventRegistry
{
    private static readonly JsonSerializerOptions Options = new(OutboxSerializer.Options)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static INotification Deserialize(string eventType, string schemaVersion, string payload)
    {
        if (schemaVersion != ApprovalDecided.SchemaVersion)
            throw new InvalidOperationException("Governance outbox event type/version is not registered.");
        return eventType switch
        {
            ApprovalRequested.EventType => (INotification?)JsonSerializer.Deserialize<ApprovalRequested>(payload, Options),
            ApprovalDecided.EventType => JsonSerializer.Deserialize<ApprovalDecided>(payload, Options),
            ApprovalExecutionReported.EventType => JsonSerializer.Deserialize<ApprovalExecutionReported>(payload, Options),
            _ => throw new InvalidOperationException("Governance outbox event type/version is not registered."),
        } ?? throw new JsonException("Governance outbox payload cannot be null.");
    }
}

internal sealed class GovernanceOutboxDispatcher(
    IServiceScopeFactory scopeFactory, ILogger<GovernanceOutboxDispatcher> logger) : BackgroundService
{
    private static readonly string Owner = $"{Environment.MachineName}:{Environment.ProcessId}";
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(1);
    private const int BatchSize = 50;
    private const int MaxAttempts = 8;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DispatchBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Governance outbox dispatch batch failed.");
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task DispatchBatchAsync(CancellationToken cancellationToken)
    {
        List<Guid> leasedIds;
        using (var leaseScope = scopeFactory.CreateScope())
        {
            var db = leaseScope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
            var clock = leaseScope.ServiceProvider.GetRequiredService<IClock>();
            if (db.Database.IsSqlServer())
            {
                const string leaseSql = """
                    UPDATE TOP ({0}) o
                    SET o.LeaseOwner = {1}, o.LeaseExpiresAt = {2}, o.Attempts = o.Attempts + 1
                    OUTPUT inserted.Id AS [Value]
                    FROM admin.GovernanceOutboxMessages AS o WITH (READPAST, UPDLOCK, ROWLOCK)
                    WHERE o.ProcessedAt IS NULL
                      AND (o.LeaseExpiresAt IS NULL OR o.LeaseExpiresAt < {3})
                      AND o.Attempts < {4};
                    """;
                leasedIds = await db.Database.SqlQueryRaw<Guid>(
                    leaseSql, BatchSize, Owner, clock.UtcNow.Add(LeaseDuration), clock.UtcNow, MaxAttempts)
                    .ToListAsync(cancellationToken);
            }
            else
            {
                leasedIds = await db.GovernanceOutboxMessages
                    .Where(x => x.ProcessedAt == null && x.Attempts < MaxAttempts)
                    .OrderBy(x => x.OccurredAt).Take(BatchSize).Select(x => x.Id).ToListAsync(cancellationToken);
            }
        }

        foreach (var id in leasedIds)
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
            var message = await db.GovernanceOutboxMessages.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (message is null)
                continue;
            IDisposable? binding = null;
            try
            {
                if (message.MerchantId is { } merchantId)
                    binding = scope.ServiceProvider.GetRequiredService<IActorScope>().Begin(merchantId);
                await scope.ServiceProvider.GetRequiredService<IPublisher>().Publish(
                    GovernanceOutboxEventRegistry.Deserialize(message.Type, message.SchemaVersion, message.Payload),
                    cancellationToken);
                message.MarkProcessed(scope.ServiceProvider.GetRequiredService<IClock>().UtcNow);
            }
            catch (Exception ex)
            {
                message.MarkFailed(ex.Message);
                logger.LogError(ex, "Failed to publish governance outbox message {OutboxId} ({Type}).", message.Id, message.Type);
            }
            finally
            {
                binding?.Dispose();
            }
            await db.SaveChangesAsync(cancellationToken);
        }
    }
}

public static class GovernanceOutboxDispatcherRegistration
{
    public static IServiceCollection AddGovernanceOutboxDispatcher(this IServiceCollection services)
    {
        services.AddHostedService<GovernanceOutboxDispatcher>();
        services.AddHostedService<OperationRecordPruneService>();
        return services;
    }
}

internal sealed class OperationRecordPruneService(
    IServiceScopeFactory scopeFactory,
    ILogger<OperationRecordPruneService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
                var now = scope.ServiceProvider.GetRequiredService<IClock>().UtcNow;
                var expired = await db.OperationRecords.Where(x => x.ExpiresAt < now)
                    .OrderBy(x => x.ExpiresAt).Take(1000).ToListAsync(stoppingToken);
                if (expired.Count > 0)
                {
                    db.OperationRecords.RemoveRange(expired);
                    await db.SaveChangesAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Governance operation-record prune failed.");
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
}
