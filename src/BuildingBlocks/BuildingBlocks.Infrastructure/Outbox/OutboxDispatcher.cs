using System.Text.Json;
using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Persistence;
using Contracts;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BuildingBlocks.Infrastructure.Outbox;

/// <summary>
/// Polls the producer outbox and publishes pending integration events at-least-once (PLAN
/// decision #10). Rows are claimed with a SQL Server lease (READPAST + UPDLOCK + per-row owner and
/// expiry) so multiple dispatcher instances never publish the same row; a row that fails
/// <see cref="MaxAttempts"/> times stops being leased and is left for the poison/DLQ review.
/// Consumers must dedupe by event id — this guarantees exactly-once *effect*, not delivery.
/// </summary>
public sealed class OutboxDispatcher : BackgroundService
{
    private static readonly string Owner = $"{Environment.MachineName}:{Environment.ProcessId}";
    private const int BatchSize = 50;
    private const int MaxAttempts = 8;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OutboxDispatcher> _logger;

    public OutboxDispatcher(IServiceScopeFactory scopeFactory, ILogger<OutboxDispatcher> logger)
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
                _logger.LogError(ex, "Outbox dispatch batch failed.");
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
        // Lease in a no-tenant scope: the OutboxMessages table carries only a BLOCK-after-insert RLS
        // predicate (no FILTER), so the worker principal sees and leases rows across every tenant.
        List<(Guid Id, Guid TenantId)> leased;
        using (var leaseScope = _scopeFactory.CreateScope())
        {
            var db = leaseScope.ServiceProvider.GetRequiredService<ProducerDbContext>();
            var clock = leaseScope.ServiceProvider.GetRequiredService<IClock>();

            var leaseUntil = clock.UtcNow.Add(LeaseDuration);
            const string leaseSql = """
                UPDATE TOP ({0}) o
                SET o.LeaseOwner = {1}, o.LeaseExpiresAtUtc = {2}, o.Attempts = o.Attempts + 1
                OUTPUT inserted.Id AS [Value]
                FROM producer.OutboxMessages AS o WITH (READPAST, UPDLOCK, ROWLOCK)
                WHERE o.ProcessedAtUtc IS NULL
                  AND (o.LeaseExpiresAtUtc IS NULL OR o.LeaseExpiresAtUtc < {3})
                  AND o.Attempts < {4};
                """;

            var leasedIds = await db.Database
                .SqlQueryRaw<Guid>(leaseSql, BatchSize, Owner, leaseUntil, clock.UtcNow, MaxAttempts)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            if (leasedIds.Count == 0)
                return;

            var rows = await db.OutboxMessages
                .Where(m => leasedIds.Contains(m.Id))
                .Select(m => new { m.Id, m.TenantId })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            leased = rows.Select(r => (r.Id, r.TenantId)).ToList();
        }

        // Process each message in its OWN scope/DbContext/connection, bound to the message's tenant so
        // in-process consumers (e.g. OrderPaidConsumer writing producer.Orders) run RLS-scoped. The
        // TenantId is trustworthy: the BLOCK-after-insert predicate guaranteed it matched the writer's
        // SESSION_CONTEXT. Disposing the scope before the next message resets the pooled context.
        foreach (var (id, tenantId) in leased)
        {
            using var scope = _scopeFactory.CreateScope();
            using var tenantBinding = scope.ServiceProvider.GetRequiredService<ITenantScope>().Begin(tenantId);

            var db = scope.ServiceProvider.GetRequiredService<ProducerDbContext>();
            var publisher = scope.ServiceProvider.GetRequiredService<IPublisher>();
            var clock = scope.ServiceProvider.GetRequiredService<IClock>();

            var message = await db.OutboxMessages
                .FirstOrDefaultAsync(m => m.Id == id, cancellationToken)
                .ConfigureAwait(false);
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
                _logger.LogError(ex, "Failed to publish outbox message {OutboxId} ({Type}).", message.Id, message.Type);
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    // ponytail: PaymentPaid is the only integration event today. Replace this switch with a
    // type-name -> CLR-type map when a second event ships, rather than reflecting all assemblies.
    private static async Task PublishAsync(IPublisher publisher, OutboxMessage message, CancellationToken cancellationToken)
    {
        switch (message.Type)
        {
            case nameof(PaymentPaid):
                var paymentPaid = JsonSerializer.Deserialize<PaymentPaid>(message.Payload, OutboxSerializer.Options)
                    ?? throw new InvalidOperationException($"Outbox payload for {message.Id} deserialised to null.");
                await publisher.Publish(paymentPaid, cancellationToken).ConfigureAwait(false);
                break;

            case nameof(CustomerOrderNotification):
                var notification = JsonSerializer.Deserialize<CustomerOrderNotification>(message.Payload, OutboxSerializer.Options)
                    ?? throw new InvalidOperationException($"Outbox payload for {message.Id} deserialised to null.");
                await publisher.Publish(notification, cancellationToken).ConfigureAwait(false);
                break;

            case nameof(CheckoutConfirmed):
                var checkoutConfirmed = JsonSerializer.Deserialize<CheckoutConfirmed>(message.Payload, OutboxSerializer.Options)
                    ?? throw new InvalidOperationException($"Outbox payload for {message.Id} deserialised to null.");
                await publisher.Publish(checkoutConfirmed, cancellationToken).ConfigureAwait(false);
                break;

            default:
                throw new InvalidOperationException($"No outbox publisher registered for type '{message.Type}'.");
        }
    }
}
