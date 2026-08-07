using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Outbox;
using Contracts;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Persistence.MerchantUsers.Outbox;

/// <summary>
/// Polls <c>merch.UserOutbox</c> and publishes pending registration events at-least-once (task 8.5.6) —
/// mirrors <c>BuildingBlocks.Infrastructure.Outbox.OutboxDispatcher</c>'s poll/lease/publish/mark shape for
/// <c>txn.OutboxMessages</c>, adapted to this outbox's tracked-EF-LINQ lease (<see cref="IMerchantUserOutboxDrain"/>,
/// task 5) instead of a raw-SQL claim. Lives inside this assembly (not <c>Hosts/Worker</c>) because the
/// per-message mark-processed/mark-failed step needs <see cref="MerchantUserDbContext"/> directly, which is
/// <c>internal</c> — no assembly outside this one may name it (design.md "no InternalsVisibleTo(Api)").
/// Exposed to the Worker host only via <see cref="MerchantUserOutboxDispatcherRegistration.AddMerchantUserOutboxDispatcher"/>.
/// </summary>
internal sealed class MerchantUserOutboxDispatcher : BackgroundService
{
    private static readonly string Owner = $"{Environment.MachineName}:{Environment.ProcessId}";
    private const int BatchSize = 50;
    private const int MaxAttempts = 8;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MerchantUserOutboxDispatcher> _logger;

    public MerchantUserOutboxDispatcher(IServiceScopeFactory scopeFactory, ILogger<MerchantUserOutboxDispatcher> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DispatchBatchAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MerchantUser outbox dispatch batch failed.");
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false);
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
        using (var leaseScope = _scopeFactory.CreateScope())
        {
            var drain = leaseScope.ServiceProvider.GetRequiredService<IMerchantUserOutboxDrain>();
            var clock = leaseScope.ServiceProvider.GetRequiredService<IClock>();

            var leased = await drain.LeaseNextBatchAsync(
                BatchSize, Owner, clock.UtcNow, LeaseDuration, MaxAttempts, cancellationToken).ConfigureAwait(false);
            leasedIds = [.. leased.Select(m => m.Id)];
        }

        if (leasedIds.Count == 0)
            return;

        // Process each message in its OWN scope/DbContext, same isolation reasoning as OutboxDispatcher:
        // disposing the scope before the next message resets the pooled context.
        foreach (var id in leasedIds)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MerchantUserDbContext>();
            var drain = scope.ServiceProvider.GetRequiredService<IMerchantUserOutboxDrain>();
            var publisher = scope.ServiceProvider.GetRequiredService<IPublisher>();
            var clock = scope.ServiceProvider.GetRequiredService<IClock>();

            // Through the drain port, not db.UserOutbox: the dispatcher is unbound, so the per-merchant
            // query filter would hide every leased row and the message would never publish.
            var message = await drain.FindLeasedAsync(id, Owner, cancellationToken).ConfigureAwait(false);
            if (message is null)
                continue;

            try
            {
                await PublishAsync(publisher, message, cancellationToken).ConfigureAwait(false);
                message.MarkProcessed(clock.UtcNow);
            }
            catch (Exception ex)
            {
                message.MarkFailed(ex.Message);
                _logger.LogError(ex, "Failed to publish MerchantUser outbox message {OutboxId} ({Type}).", message.Id, message.Type);
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task PublishAsync(IPublisher publisher, MerchantUserOutbox message, CancellationToken cancellationToken)
    {
        var notification = MerchantUserOutboxEventRegistry.Deserialize(message.Type, message.Payload);
        await publisher.Publish(notification, cancellationToken).ConfigureAwait(false);
    }
}

public static class MerchantUserOutboxDispatcherRegistration
{
    public static IServiceCollection AddMerchantUserOutboxDispatcher(this IServiceCollection services)
    {
        services.AddHostedService<MerchantUserOutboxDispatcher>();
        return services;
    }
}
