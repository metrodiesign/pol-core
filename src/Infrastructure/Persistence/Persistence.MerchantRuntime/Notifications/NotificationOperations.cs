using System.Net.Mail;
using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Notifications.Application;
using Notifications.Domain;

namespace Persistence.MerchantRuntime.Notifications;

internal sealed class NotificationOperations(
    CommerceDbContext db,
    IUnitOfWork unitOfWork,
    IClock clock,
    IAdminOperationExecutor? operations = null) : INotificationOperations
{
    public async Task<PagedResult<CommerceNotificationView>> SearchAsync(
        NotificationSearchQuery query, DeliveryAccess access, CancellationToken cancellationToken)
    {
        ValidatePage(query.Page, query.Limit);
        var source = Scope(db.Notifications.AsNoTracking().IgnoreQueryFilters(), access);
        if (query.MerchantId is { } merchantId)
        {
            if (!access.Allows(merchantId))
                return new([], query.Page, query.Limit, 0);
            source = source.Where(x => x.MerchantId == merchantId);
        }
        if (!string.IsNullOrWhiteSpace(query.OrderNo))
            source = source.Where(x => x.OrderNo == query.OrderNo.Trim());
        if (!string.IsNullOrWhiteSpace(query.TransactionNo))
            source = source.Where(x => x.TransactionNo == query.TransactionNo.Trim());
        if (!string.IsNullOrWhiteSpace(query.CorrelationId))
            source = source.Where(x => x.CorrelationId == query.CorrelationId.Trim());

        var total = await PlatformReadGuard.ReadAsync(ct => source.LongCountAsync(ct), cancellationToken);
        var rows = await PlatformReadGuard.ReadAsync(ct => source.OrderByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id)
            .Skip((query.Page - 1) * query.Limit).Take(query.Limit).ToListAsync(ct), cancellationToken);
        return new(rows.Select(ToView).ToArray(), query.Page, query.Limit, total);
    }

    public async Task<CommerceNotificationView?> GetAsync(
        Guid notificationId, DeliveryAccess access, CancellationToken cancellationToken)
    {
        var row = await PlatformReadGuard.ReadAsync(ct => Scope(db.Notifications.AsNoTracking()
                .IgnoreQueryFilters(), access)
            .SingleOrDefaultAsync(x => x.Id == notificationId, ct), cancellationToken);
        return row is null ? null : ToView(row);
    }

    public async Task<CommerceDeliveryView?> GetDeliveryAsync(
        Guid deliveryId, DeliveryAccess access, CancellationToken cancellationToken)
    {
        var row = await PlatformReadGuard.ReadAsync(ct => Scope(db.Deliveries.AsNoTracking()
                .IgnoreQueryFilters(), access)
            .SingleOrDefaultAsync(x => x.Id == deliveryId, ct), cancellationToken);
        return row is null ? null : ToView(row);
    }

    public async Task<IReadOnlyList<CommerceDeliveryAttemptView>> ListAttemptsAsync(
        Guid deliveryId, DeliveryAccess access, CancellationToken cancellationToken)
    {
        var deliveryExists = await PlatformReadGuard.ReadAsync(ct => Scope(db.Deliveries.AsNoTracking()
                .IgnoreQueryFilters(), access)
            .AnyAsync(x => x.Id == deliveryId, ct), cancellationToken);
        if (!deliveryExists)
            return [];
        var rows = await PlatformReadGuard.ReadAsync(ct => Scope(db.DeliveryAttempts.AsNoTracking()
                .IgnoreQueryFilters(), access)
            .Where(x => x.DeliveryId == deliveryId)
            .OrderBy(x => x.AttemptNo).ThenBy(x => x.Id)
            .ToListAsync(ct), cancellationToken);
        return rows.Select(x => new CommerceDeliveryAttemptView(
            x.Id, x.DeliveryId, x.AttemptNo, x.Outcome, x.ProviderMessageId,
            x.FailureCode, x.LatencyMs, x.StartedAt, x.CompletedAt)).ToArray();
    }

    public Task<CommerceDeliveryView?> RetryAsync(
        Guid deliveryId, Guid actorId, DeliveryAccess access, CancellationToken cancellationToken) =>
        RetryDirectAsync(deliveryId, actorId, access, cancellationToken);

    public async Task<CommerceDeliveryView?> RetryAsync(
        Guid deliveryId, Guid actorId, string reason, string idempotencyKey,
        DeliveryAccess access, CancellationToken cancellationToken)
    {
        if (actorId == Guid.Empty)
            throw new InvalidRequestException("Actor is required.", "actor_required");
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > 1000)
            throw new InvalidRequestException("Retry reason is invalid.", "validation_failed");
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 200)
            throw new InvalidRequestException("Idempotency-Key is invalid.", "validation_failed");

        var merchantId = await PlatformReadGuard.ReadAsync(ct => Scope(db.Deliveries.AsNoTracking()
                .IgnoreQueryFilters(), access)
            .Where(x => x.Id == deliveryId)
            .Select(x => (Guid?)x.MerchantId)
            .SingleOrDefaultAsync(ct), cancellationToken);
        if (merchantId is null)
            return null;
        if (operations is null)
            return await RetryDirectAsync(deliveryId, actorId, access, cancellationToken);

        var result = await operations.ExecuteAsync(
            new AdminOperationRequest(
                merchantId.Value,
                actorId,
                "notification-delivery.retry",
                idempotencyKey.Trim(),
                System.Text.Json.JsonSerializer.Serialize(new { deliveryId, reason = reason.Trim() }),
                202),
            async ct =>
            {
                var row = await PlatformReadGuard.ReadAsync(readCt => Scope(db.Deliveries
                        .IgnoreQueryFilters(), access)
                    .SingleAsync(x => x.Id == deliveryId, readCt), ct);
                try
                {
                    row.Retry(clock.UtcNow);
                }
                catch (InvalidOperationException ex)
                {
                    throw new ConflictException(ex.Message, "delivery_not_retryable");
                }
                await unitOfWork.SaveChangesAsync(ct);
                return ToView(row);
            },
            value => value.Id.ToString("D"),
            cancellationToken);
        return result.Value;
    }

    private async Task<CommerceDeliveryView?> RetryDirectAsync(
        Guid deliveryId, Guid actorId, DeliveryAccess access, CancellationToken cancellationToken)
    {
        if (actorId == Guid.Empty)
            throw new InvalidRequestException("Actor is required.", "actor_required");
        return await unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            var row = await PlatformReadGuard.ReadAsync(readCt => Scope(db.Deliveries
                    .IgnoreQueryFilters(), access)
                .SingleOrDefaultAsync(x => x.Id == deliveryId, readCt), ct);
            if (row is null)
                return null;
            try
            {
                row.Retry(clock.UtcNow);
            }
            catch (InvalidOperationException ex)
            {
                throw new ConflictException(ex.Message, "delivery_not_retryable");
            }
            await unitOfWork.SaveChangesAsync(ct);
            return ToView(row);
        }, cancellationToken);
    }

    public async Task<NotificationReceiptView?> ApplyReceiptAsync(
        NotificationReceipt receipt, CancellationToken cancellationToken)
    {
        if (receipt.DeliveryId == Guid.Empty || string.IsNullOrWhiteSpace(receipt.ProviderMessageId))
            throw new InvalidRequestException("Receipt identity is invalid.", "validation_failed");
        return await unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            var row = await PlatformReadGuard.ReadAsync(readCt => db.Deliveries.IgnoreQueryFilters()
                .SingleOrDefaultAsync(x => x.Id == receipt.DeliveryId, readCt), ct);
            if (row is null)
                return null;

            var marker = $"RECEIPT_{receipt.Outcome.ToString().ToUpperInvariant()}";
            var existing = await PlatformReadGuard.ReadAsync(readCt => db.DeliveryAttempts.IgnoreQueryFilters()
                .AsNoTracking()
                .SingleOrDefaultAsync(x => x.DeliveryId == receipt.DeliveryId
                    && x.ProviderMessageId == receipt.ProviderMessageId, readCt), ct);
            if (existing is not null)
            {
                if (!string.Equals(existing.Outcome, marker, StringComparison.Ordinal))
                    throw new ConflictException("Receipt idempotency key was reused with a different outcome.",
                        "idempotency_conflict");
                return new NotificationReceiptView(
                    row.Id, StatusToken(row.Status), row.ProviderMessageId, Replayed: true);
            }

            var now = clock.UtcNow;
            try
            {
                switch (receipt.Outcome)
                {
                    case NotificationReceiptOutcome.Accepted:
                        if (row.Status is not (DeliveryStatus.Accepted or DeliveryStatus.Delivered))
                            row.MarkAccepted(receipt.ProviderMessageId, now);
                        break;
                    case NotificationReceiptOutcome.Delivered:
                        if (row.Status != DeliveryStatus.Delivered)
                            row.MarkDelivered(receipt.ProviderMessageId, now);
                        break;
                    case NotificationReceiptOutcome.Failed:
                        if (row.Status is not (DeliveryStatus.Failed or DeliveryStatus.ManualQueue))
                            row.MarkFailed(receipt.FailureCode ?? "provider_failed", now, retryAt: null);
                        break;
                    case NotificationReceiptOutcome.Unknown:
                        if (row.Status is not (DeliveryStatus.Unknown or DeliveryStatus.ManualQueue))
                            row.MarkUnknown(receipt.FailureCode ?? "provider_response_unknown", now,
                                Delivery.RetryDelay(Math.Max(1, row.AttemptCount)) is { } delay
                                    ? now + delay : null);
                        break;
                }
            }
            catch (InvalidOperationException ex)
            {
                throw new ConflictException(ex.Message, "receipt_state_conflict");
            }

            var nextAttempt = await PlatformReadGuard.ReadAsync(readCt => db.DeliveryAttempts.IgnoreQueryFilters()
                .Where(x => x.DeliveryId == receipt.DeliveryId)
                .Select(x => (int?)x.AttemptNo)
                .MaxAsync(readCt), ct) ?? 0;
            db.DeliveryAttempts.Add(DeliveryAttempt.Record(
                row.Id,
                row.MerchantId,
                nextAttempt + 1,
                marker,
                now,
                now,
                receipt.ProviderMessageId,
                receipt.FailureCode));
            await unitOfWork.SaveChangesAsync(ct);
            return new NotificationReceiptView(
                row.Id, StatusToken(row.Status), row.ProviderMessageId, Replayed: false);
        }, cancellationToken);
    }

    public Task<Guid?> ResolveDeliveryMerchantAsync(
        Guid deliveryId, CancellationToken cancellationToken) =>
        PlatformReadGuard.ReadAsync(ct => db.Deliveries.IgnoreQueryFilters()
            .Where(x => x.Id == deliveryId)
            .Select(x => (Guid?)x.MerchantId)
            .SingleOrDefaultAsync(ct), cancellationToken);

    public async Task<NotificationReviewNoteView> AddReviewNoteAsync(
        Guid merchantId, Guid? notificationId, Guid? deliveryId, Guid actorId, string note,
        DeliveryAccess access, CancellationToken cancellationToken)
    {
        EnsureAccess(access, merchantId);
        if (actorId == Guid.Empty)
            throw new InvalidRequestException("Actor is required.", "actor_required");
        return await unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            if (notificationId is { } notification)
            {
                var belongs = await PlatformReadGuard.ReadAsync(readCt => db.Notifications
                    .IgnoreQueryFilters()
                    .AnyAsync(x => x.Id == notification && x.MerchantId == merchantId, readCt), ct);
                if (!belongs)
                    throw new NotFoundException("Notification was not found.");
            }
            if (deliveryId is { } delivery)
            {
                var belongs = await PlatformReadGuard.ReadAsync(readCt => db.Deliveries
                    .IgnoreQueryFilters()
                    .AnyAsync(x => x.Id == delivery && x.MerchantId == merchantId, readCt), ct);
                if (!belongs)
                    throw new NotFoundException("Delivery was not found.");
            }

            var row = NotificationReviewNote.Add(
                merchantId,
                actorId,
                note,
                clock.UtcNow,
                notificationId,
                deliveryId,
                CorrelationId.Current);
            db.NotificationReviewNotes.Add(row);
            await unitOfWork.SaveChangesAsync(ct);
            return new NotificationReviewNoteView(
                row.Id,
                row.MerchantId,
                row.NotificationId,
                row.DeliveryId,
                row.ActorId,
                row.Note,
                row.CorrelationId,
                row.CreatedAt);
        }, cancellationToken);
    }

    private static IQueryable<global::Notifications.Domain.Notification> Scope(
        IQueryable<global::Notifications.Domain.Notification> source, DeliveryAccess access) =>
        access.IsUnrestricted ? source : source.Where(x => access.MerchantIds.Contains(x.MerchantId));

    private static IQueryable<global::Notifications.Domain.Delivery> Scope(
        IQueryable<global::Notifications.Domain.Delivery> source, DeliveryAccess access) =>
        access.IsUnrestricted ? source : source.Where(x => access.MerchantIds.Contains(x.MerchantId));

    private static IQueryable<global::Notifications.Domain.DeliveryAttempt> Scope(
        IQueryable<global::Notifications.Domain.DeliveryAttempt> source, DeliveryAccess access) =>
        access.IsUnrestricted ? source : source.Where(x => access.MerchantIds.Contains(x.MerchantId));

    private static CommerceNotificationView ToView(global::Notifications.Domain.Notification row) => new(
        row.Id,
        row.SourceEventId,
        row.MerchantId,
        row.EventType,
        row.RegistrationId,
        row.RegistrationAttemptId,
        row.OrderId,
        row.OrderNo,
        row.TransactionId,
        row.TransactionNo,
        row.CorrelationId,
        row.OccurredAt,
        row.CreatedAt);

    private static CommerceDeliveryView ToView(global::Notifications.Domain.Delivery row) => new(
        row.Id,
        row.NotificationId,
        row.MerchantId,
        row.Channel,
        Mask(row.RecipientSnapshot),
        row.TemplateVersion,
        row.TemplateLocale,
        StatusToken(row.Status),
        row.AttemptCount,
        row.NextAttemptAt,
        row.CompletedAt,
        row.ProviderMessageId,
        row.FailureCode);

    private static string StatusToken(DeliveryStatus status) => status switch
    {
        DeliveryStatus.Pending => "PENDING",
        DeliveryStatus.Processing => "PROCESSING",
        DeliveryStatus.Accepted => "ACCEPTED",
        DeliveryStatus.Delivered => "DELIVERED",
        DeliveryStatus.Failed => "FAILED",
        DeliveryStatus.Unknown => "UNKNOWN",
        DeliveryStatus.BlockedNotConfigured => "BLOCKED_NOT_CONFIGURED",
        DeliveryStatus.ManualQueue => "MANUAL_QUEUE",
        _ => "UNKNOWN",
    };

    private static string Mask(string value)
    {
        if (MailAddress.TryCreate(value, out var email) && email.User.Length > 0)
            return $"{email.User[0]}***@{email.Host}";
        var digits = new string(value.Where(char.IsDigit).ToArray());
        if (digits.Length >= 4)
            return $"***{digits[^4..]}";
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
            return $"{uri.Scheme}://{uri.Host}/***";
        return "***";
    }

    private static void ValidatePage(int page, int limit)
    {
        if (page < 1 || limit is < 1 or > 100)
            throw new InvalidRequestException("Page and limit are invalid.", "invalid_filter");
    }

    private static void EnsureAccess(DeliveryAccess access, Guid merchantId)
    {
        if (!access.Allows(merchantId))
            throw new AccessDeniedException("Merchant is outside current scope.");
    }
}
