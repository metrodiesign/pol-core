using System.Diagnostics;
using System.Text.Json;
using BuildingBlocks.Application;
using Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Notifications.Application;
using Notifications.Domain;
using Orders.Application;

namespace Persistence.MerchantRuntime.Notifications;

internal sealed class NotificationDeliveryProcessor(
    CommerceDbContext db,
    IUnitOfWork unitOfWork,
    IClock clock,
    IEmailSenderPort email,
    ISmsSenderPort sms,
    IBusinessWebhookSender? webhook = null,
    IPaymentLinkNotificationProtector? paymentLinkNotifications = null)
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(1);
    private static readonly string Owner = $"{Environment.MachineName}:{Environment.ProcessId}";

    public async Task ProcessAsync(Guid deliveryId, CancellationToken cancellationToken)
    {
        DeliverySnapshot? snapshot = null;
        var claimOwner = $"{Owner}:{Guid.CreateVersion7():N}";
        var claimed = await unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            var row = await db.Deliveries.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == deliveryId, ct);
            if (row is null)
                return false;

            var now = clock.UtcNow;
            var leaseUntil = now.Add(LeaseDuration);
            var affected = await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE txn.Deliveries
                SET Status = {(int)DeliveryStatus.Processing},
                    AttemptCount = AttemptCount + 1,
                    LastAttemptAt = {now},
                    LeaseOwner = {claimOwner},
                    LeaseExpiresAt = {leaseUntil},
                    FailureCode = NULL
                WHERE Id = {deliveryId}
                  AND ((Status IN ({(int)DeliveryStatus.Pending}, {(int)DeliveryStatus.Unknown})
                        AND NextAttemptAt <= {now})
                    OR (Status = {(int)DeliveryStatus.Processing}
                        AND LeaseExpiresAt < {now}));
                """, ct).ConfigureAwait(false);
            if (affected != 1)
                return false;

            db.ChangeTracker.Clear();
            row = await db.Deliveries.SingleAsync(x => x.Id == deliveryId, ct);
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
                row.PayloadSnapshot,
                claimOwner);
            if (row.Channel == "sms" && !sms.IsConfigured)
            {
                row.BlockNotConfigured("sms_not_configured", now, snapshot.LeaseOwner);
                await unitOfWork.SaveChangesAsync(ct);
                return false;
            }
            return true;
        }, cancellationToken);

        if (!claimed || snapshot is null)
            return;

        var startedAt = clock.UtcNow;
        var timer = Stopwatch.StartNew();
        DeliveryProviderResult result;
        try
        {
            var body = RenderPaymentLinkToken(snapshot.Body, snapshot.Payload, paymentLinkNotifications);
            result = snapshot.Channel switch
            {
                "email" => await email.SendAsync(new EmailDeliveryRequest(
                    snapshot.Recipient, snapshot.Subject, body, snapshot.TemplateVersion, snapshot.Id),
                    cancellationToken),
                "sms" => await sms.SendAsync(new SmsDeliveryRequest(
                    snapshot.Recipient, body, snapshot.TemplateVersion, snapshot.Id), cancellationToken),
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

        try
        {
            await unitOfWork.ExecuteInTransactionAsync(async ct =>
            {
                db.ChangeTracker.Clear();
                var row = await db.Deliveries
                    .FromSqlInterpolated($"""
                        SELECT *
                        FROM txn.Deliveries WITH (UPDLOCK, ROWLOCK)
                        WHERE Id = {snapshot.Id}
                        """)
                    .IgnoreQueryFilters()
                    .SingleOrDefaultAsync(ct);
                if (row is null
                    || row.Status != DeliveryStatus.Processing
                    || !string.Equals(row.LeaseOwner, snapshot.LeaseOwner, StringComparison.Ordinal)
                    || row.AttemptCount != snapshot.AttemptCount)
                    return false;

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
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (ConcurrencyConflictException)
        {
            // Another worker acquired the expired lease before this provider result was committed.
            // The owner/attempt predicate above makes the stale result a no-op.
        }
    }

    private static string RenderPaymentLinkToken(
        string body,
        string payload,
        IPaymentLinkNotificationProtector? protector)
    {
        const string marker = "{{paymentLinkToken}}";
        if (!body.Contains(marker, StringComparison.Ordinal))
            return body;
        if (protector is null)
            throw new InvalidOperationException("Payment-link notification protection is not configured.");
        var eventPayload = JsonSerializer.Deserialize<PaymentLinkNotificationRequestedV1>(
            payload,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var protectedToken = eventPayload?.ProtectedRawToken;
        if (string.IsNullOrWhiteSpace(protectedToken))
            throw new InvalidOperationException("Payment-link notification payload is missing protection.");
        var rawToken = protector.Unprotect(protectedToken);
        if (string.IsNullOrWhiteSpace(rawToken))
            throw new InvalidOperationException("Payment-link notification protection could not be opened.");
        return body.Replace(marker, rawToken, StringComparison.Ordinal);
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
        string Payload,
        string LeaseOwner);
}

internal sealed class NotificationDeliveryDispatcher(
    IServiceScopeFactory scopeFactory,
    ILogger<NotificationDeliveryDispatcher> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
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
        List<(Guid Id, Guid MerchantId)> candidates;
        using (var discovery = scopeFactory.CreateScope())
        {
            var db = discovery.ServiceProvider.GetRequiredService<CommerceDbContext>();
            var now = discovery.ServiceProvider.GetRequiredService<IClock>().UtcNow;
            const string candidateSql = """
                SELECT TOP ({0}) d.Id AS [Value]
                FROM txn.Deliveries AS d WITH (READPAST, ROWLOCK)
                WHERE ((d.Status IN (1, 6) AND d.NextAttemptAt <= {1})
                    OR (d.Status = 2 AND d.LeaseExpiresAt < {1}))
                """;
            var ids = await db.Database.SqlQueryRaw<Guid>(
                candidateSql, BatchSize, now).ToListAsync(cancellationToken).ConfigureAwait(false);
            if (ids.Count == 0)
                return;
            var rows = await db.Deliveries.IgnoreQueryFilters().Where(x => ids.Contains(x.Id))
                .Select(x => new { x.Id, x.MerchantId }).ToListAsync(cancellationToken)
                .ConfigureAwait(false)
                ;
            candidates = rows.Select(x => (x.Id, x.MerchantId)).ToList();
        }

        foreach (var (id, merchantId) in candidates)
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
