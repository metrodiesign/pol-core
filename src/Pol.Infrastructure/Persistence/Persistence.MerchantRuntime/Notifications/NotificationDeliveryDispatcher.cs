using System.Diagnostics;
using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Notifications.Application;
using Notifications.Domain;

namespace Persistence.MerchantRuntime.Notifications;

internal sealed class NotificationDeliveryProcessor(
    CommerceDbContext db,
    IUnitOfWork unitOfWork,
    IClock clock,
    IEmailSenderPort email,
    ISmsSenderPort sms,
    IBusinessWebhookSender? webhook = null)
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(1);
    private static readonly string Owner = $"{Environment.MachineName}:{Environment.ProcessId}";

    public async Task ProcessAsync(Guid deliveryId, CancellationToken cancellationToken)
    {
        DeliverySnapshot? snapshot = null;
        var claimed = await unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            var row = await db.Deliveries.SingleOrDefaultAsync(x => x.Id == deliveryId, ct);
            if (row is null)
                return false;

            var now = clock.UtcNow;
            if (row.Channel == "sms" && !sms.IsConfigured)
            {
                row.BlockNotConfigured("sms_not_configured", now);
                await unitOfWork.SaveChangesAsync(ct);
                return false;
            }

            row.Claim(Owner, now, now.Add(LeaseDuration));
            snapshot = new DeliverySnapshot(
                row.Id,
                row.Channel,
                row.RecipientSnapshot,
                row.TemplateSubjectSnapshot,
                row.TemplateContentSnapshot,
                row.TemplateVersion,
                row.AttemptCount,
                row.EndpointUrlSnapshot,
                row.ProtectedEndpointSecretSnapshot,
                row.PayloadSnapshot);
            await unitOfWork.SaveChangesAsync(ct);
            return true;
        }, cancellationToken);

        if (!claimed || snapshot is null)
            return;

        var startedAt = clock.UtcNow;
        var timer = Stopwatch.StartNew();
        DeliveryProviderResult result;
        try
        {
            result = snapshot.Channel switch
            {
                "email" => await email.SendAsync(new EmailDeliveryRequest(
                    snapshot.Recipient, snapshot.Subject, snapshot.Body, snapshot.TemplateVersion, snapshot.Id),
                    cancellationToken),
                "sms" => await sms.SendAsync(new SmsDeliveryRequest(
                    snapshot.Recipient, snapshot.Body, snapshot.TemplateVersion, snapshot.Id), cancellationToken),
                "business_webhook" when webhook is not null
                    && snapshot.EndpointUrl is not null
                    && snapshot.ProtectedEndpointSecret is not null =>
                    await webhook.SendAsync(new BusinessWebhookDeliveryRequest(
                        snapshot.Id,
                        snapshot.EndpointUrl,
                        snapshot.ProtectedEndpointSecret,
                        snapshot.Payload), cancellationToken),
                _ => new DeliveryProviderResult(DeliveryProviderOutcome.Failed, FailureCode: "channel_unsupported"),
            };
        }
        catch (DeliveryProviderAmbiguousException)
        {
            result = new DeliveryProviderResult(
                DeliveryProviderOutcome.Unknown,
                FailureCode: "provider_response_unknown");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            result = new DeliveryProviderResult(
                DeliveryProviderOutcome.Unknown,
                FailureCode: "provider_timeout");
        }
        catch (Exception exception) when (exception is IOException or HttpRequestException)
        {
            result = new DeliveryProviderResult(
                DeliveryProviderOutcome.Failed,
                FailureCode: "provider_unavailable");
        }
        catch (Exception)
        {
            result = new DeliveryProviderResult(
                DeliveryProviderOutcome.Failed,
                FailureCode: "provider_error");
        }

        await unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            var row = await db.Deliveries.SingleAsync(x => x.Id == snapshot.Id, ct);
            var now = clock.UtcNow;
            var retryAt = Delivery.RetryDelay(row.AttemptCount) is { } delay ? now + delay : (DateTime?)null;
            switch (result.Outcome)
            {
                case DeliveryProviderOutcome.Accepted:
                    row.MarkAccepted(result.ProviderMessageId, now);
                    break;
                case DeliveryProviderOutcome.Delivered:
                    row.MarkDelivered(result.ProviderMessageId, now);
                    break;
                case DeliveryProviderOutcome.Unknown:
                    row.MarkUnknown(result.FailureCode ?? "provider_response_unknown", now, retryAt);
                    break;
                case DeliveryProviderOutcome.BlockedNotConfigured:
                    row.MarkUnknown(result.FailureCode ?? "not_configured", now, retryAt);
                    break;
                default:
                    row.MarkFailed(result.FailureCode ?? "provider_failed", now, retryAt);
                    break;
            }

            db.DeliveryAttempts.Add(DeliveryAttempt.Record(
                row.Id,
                row.MerchantId,
                row.AttemptCount,
                result.Outcome.ToString().ToUpperInvariant(),
                startedAt,
                now,
                result.ProviderMessageId,
                result.FailureCode,
                (int)Math.Min(int.MaxValue, timer.ElapsedMilliseconds)));
            await unitOfWork.SaveChangesAsync(ct);
            return true;
        }, cancellationToken);
    }

    private sealed record DeliverySnapshot(
        Guid Id,
        string Channel,
        string Recipient,
        string Subject,
        string Body,
        string TemplateVersion,
        int AttemptCount,
        string? EndpointUrl,
        string? ProtectedEndpointSecret,
        string Payload);
}

internal sealed class NotificationDeliveryDispatcher(
    IServiceScopeFactory scopeFactory,
    ILogger<NotificationDeliveryDispatcher> logger) : BackgroundService
{
    private static readonly string Owner = $"{Environment.MachineName}:{Environment.ProcessId}";
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(1);
    private const int BatchSize = 50;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunBatchAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Notification delivery batch failed.");
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

    internal async Task RunBatchAsync(CancellationToken cancellationToken)
    {
        List<(Guid Id, Guid MerchantId)> leased;
        using (var discovery = scopeFactory.CreateScope())
        {
            var db = discovery.ServiceProvider.GetRequiredService<CommerceDbContext>();
            var now = discovery.ServiceProvider.GetRequiredService<IClock>().UtcNow;
            var leaseUntil = now.Add(LeaseDuration);
            const string leaseSql = """
                UPDATE TOP ({0}) d
                SET d.LeaseOwner = {1}, d.LeaseExpiresAt = {2}
                OUTPUT inserted.Id AS [Value]
                FROM txn.Deliveries AS d WITH (READPAST, UPDLOCK, ROWLOCK)
                WHERE ((d.Status IN (1, 6) AND d.NextAttemptAt <= {3})
                    OR (d.Status = 2 AND d.LeaseExpiresAt < {3}));
                """;
            var ids = await db.Database.SqlQueryRaw<Guid>(
                leaseSql, BatchSize, Owner, leaseUntil, now).ToListAsync(cancellationToken).ConfigureAwait(false);
            if (ids.Count == 0)
                return;
            var rows = await db.Deliveries.IgnoreQueryFilters().Where(x => ids.Contains(x.Id))
                .Select(x => new { x.Id, x.MerchantId }).ToListAsync(cancellationToken)
                .ConfigureAwait(false)
                ;
            leased = rows.Select(x => (x.Id, x.MerchantId)).ToList();
        }

        foreach (var (id, merchantId) in leased)
        {
            using var scope = scopeFactory.CreateScope();
            using var actor = scope.ServiceProvider.GetRequiredService<IActorScope>().Begin(merchantId);
            try
            {
                await scope.ServiceProvider.GetRequiredService<NotificationDeliveryProcessor>()
                    .ProcessAsync(id, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Notification delivery {DeliveryId} failed.", id);
            }
        }
    }
}
