using System.Net;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Notifications.Application;
using Notifications.Domain;
using Persistence.ControlPlane.Governance;

namespace Persistence.ControlPlane.Notifications;

internal sealed class SafeDestinationValidator : ISafeDestinationValidator
{
    public async Task<ValidatedDestination> ResolveAsync(string url, CancellationToken cancellationToken)
    {
        if (url.Length > 2048 || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps || uri.Port != 443
            || string.IsNullOrWhiteSpace(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Fragment))
            throw Unsafe("Webhook destination must be an absolute HTTPS URL on port 443.");
        IPAddress[] addresses;
        try { addresses = await Dns.GetHostAddressesAsync(uri.DnsSafeHost, cancellationToken); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw Unsafe("Webhook destination DNS resolution failed.");
        }
        if (addresses.Length == 0 || addresses.Any(IsUnsafe))
            throw Unsafe("Webhook destination resolves to a non-public address.");
        var selected = addresses.Distinct().OrderBy(x => x.AddressFamily).ThenBy(x => x.ToString(), StringComparer.Ordinal).First();
        return new ValidatedDestination(uri, selected);
    }

    internal static bool IsUnsafe(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) return IsUnsafe(address.MapToIPv4());
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)
            || address.Equals(IPAddress.None) || address.Equals(IPAddress.IPv6None)) return true;
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            return bytes[0] is 0 or 10 or 127 || bytes[0] >= 224
                || (bytes[0] == 100 && bytes[1] is >= 64 and <= 127)
                || (bytes[0] == 169 && bytes[1] == 254)
                || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
                || (bytes[0] == 192 && bytes[1] == 168)
                || (bytes[0] == 192 && bytes[1] == 0 && bytes[2] is 0 or 2)
                || (bytes[0] == 198 && bytes[1] is 18 or 19)
                || (bytes[0] == 198 && bytes[1] == 51 && bytes[2] == 100)
                || (bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113);
        return address.IsIPv6LinkLocal || address.IsIPv6Multicast || address.IsIPv6SiteLocal
            || (bytes[0] & 0xfe) == 0xfc
            || (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0d && bytes[3] == 0xb8);
    }

    private static InvalidRequestException Unsafe(string message) => new(message, "unsafe_destination");
}

internal sealed class DeliveryControlStore(
    ControlPlaneDbContext db,
    IClock clock,
    IDataProtectionProvider protection,
    ISafeDestinationValidator destinations,
    IUnitOfWork unitOfWork,
    ControlPlaneOperationExecutor operations) : IDeliveryControlStore, IDeliveryEventSink
{
    private static readonly string[] SupportedEvents = ["payment.paid", "payment.failed", "payment.expired"];
    private readonly IDataProtector _protector = protection.CreateProtector("pol-core/delivery-secret/v1");

    public async Task<PagedResult<WebhookEndpointView>> ListEndpointsAsync(
        WebhookEndpointQuery query, DeliveryAccess access, CancellationToken ct)
    {
        var source = Scope(db.WebhookEndpoints.AsNoTracking(), access);
        if (query.MerchantId is { } merchantId) source = source.Where(x => x.MerchantId == merchantId);
        if (query.Enabled is { } enabled) source = source.Where(x => x.Enabled == enabled);
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var pattern = $"%{SfsLike.Escape(query.Search.Trim())}%";
            source = source.Where(x => EF.Functions.Like(x.Name, pattern, "\\")
                || EF.Functions.Like(x.Url, pattern, "\\")
                || EF.Functions.Like(x.EventsCsv, pattern, "\\"));
        }
        var total = await source.LongCountAsync(ct);
        var rows = await source.OrderBy(x => x.Name).ThenBy(x => x.Id)
            .Skip((query.Page - 1) * query.Limit).Take(query.Limit).ToListAsync(ct);
        return new(rows.Select(Endpoint).ToArray(), query.Page, query.Limit, total);
    }

    public async Task<WebhookEndpointView?> GetEndpointAsync(Guid id, DeliveryAccess access, CancellationToken ct)
    {
        var row = await Scope(db.WebhookEndpoints.AsNoTracking(), access).SingleOrDefaultAsync(x => x.Id == id, ct);
        return row is null ? null : Endpoint(row);
    }

    public async Task<WebhookEndpointCreated> CreateEndpointAsync(Guid merchantId, string name, string url,
        IReadOnlyList<string> events, Guid actorId, string idempotencyKey, DeliveryAccess access, CancellationToken ct)
    {
        EnsureAccess(access, merchantId); ValidateEvents(events); await destinations.ResolveAsync(url, ct);
        string? issuedSecret = null;
        var result = await operations.ExecuteAsync(
            actorId, merchantId, "webhook-endpoint.create", idempotencyKey,
            new { merchantId, name, url, events }, 201,
            async token =>
            {
                issuedSecret = Token(32);
                var secretId = Guid.CreateVersion7();
                var endpoint = WebhookEndpoint.Create(merchantId, name, url, events, secretId,
                    $"••••{issuedSecret[^4..]}", clock.UtcNow);
                var secret = DeliverySecretVersion.Stage(secretId, endpoint.Id, merchantId, "webhook-endpoint",
                    _protector.Protect(issuedSecret), clock.UtcNow);
                secret.Activate(clock.UtcNow);
                db.WebhookEndpoints.Add(endpoint); db.DeliverySecretVersions.Add(secret);
                await Task.CompletedTask;
                return new WebhookEndpointMutation(Endpoint(endpoint), Replayed: false);
            }, ct);
        return new WebhookEndpointCreated(result.Value.Endpoint, result.Replayed ? null : issuedSecret, result.Replayed);
    }

    public async Task<WebhookEndpointMutation?> UpdateEndpointAsync(Guid id, string name, string url,
        IReadOnlyList<string> events, bool enabled, long version, Guid actorId, string idempotencyKey,
        DeliveryAccess access, CancellationToken ct)
    {
        ValidateEvents(events); await destinations.ResolveAsync(url, ct);
        var merchantId = await EndpointMerchantAsync(id, access, ct); if (merchantId is null) return null;
        var result = await operations.ExecuteAsync(
            actorId, merchantId.Value, "webhook-endpoint.update", idempotencyKey,
            new { id, name, url, events, enabled, version }, 200,
            async token =>
            {
                var row = await Scope(db.WebhookEndpoints, access).SingleAsync(x => x.Id == id, token);
                EnsureVersion(row.Version, version); row.Update(name, url, events, enabled, clock.UtcNow);
                return new WebhookEndpointMutation(Endpoint(row), Replayed: false);
            }, ct);
        return result.Value with { Replayed = result.Replayed };
    }

    public async Task<bool?> DeleteEndpointAsync(Guid id, long version, Guid actorId, string idempotencyKey,
        DeliveryAccess access, CancellationToken ct)
    {
        var merchantId = await EndpointMerchantAsync(id, access, ct); if (merchantId is null) return null;
        var result = await operations.ExecuteAsync(
            actorId, merchantId.Value, "webhook-endpoint.delete", idempotencyKey,
            new { id, version }, 204,
            async token =>
            {
                var row = await Scope(db.WebhookEndpoints, access).SingleAsync(x => x.Id == id, token);
                EnsureVersion(row.Version, version);
                if (await db.WebhookDeliveries.AnyAsync(x => x.EndpointId == id, token))
                    throw new ConflictException("Webhook endpoint has delivery history.", "endpoint_referenced");
                var secret = await db.DeliverySecretVersions.SingleAsync(x => x.Id == row.ActiveSecretVersionId, token);
                secret.Retire(clock.UtcNow); db.WebhookEndpoints.Remove(row);
                return new DeleteMutation(true);
            }, ct);
        return result.Value.Deleted;
    }

    public async Task<PagedResult<WebhookDeliveryView>> ListWebhookDeliveriesAsync(
        WebhookDeliveryQuery query, DeliveryAccess access, CancellationToken ct)
    {
        var source = Scope(db.WebhookDeliveries.AsNoTracking(), access);
        if (query.MerchantId is { } merchantId) source = source.Where(x => x.MerchantId == merchantId);
        if (!string.IsNullOrWhiteSpace(query.Status))
        {
            var status = ParseStatus(query.Status); source = source.Where(x => x.Status == status);
        }
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var pattern = $"%{SfsLike.Escape(query.Search.Trim())}%";
            source = source.Where(x => EF.Functions.Like(x.EventType, pattern, "\\")
                || x.TransactionId != null && EF.Functions.Like(x.TransactionId, pattern, "\\"));
        }
        var total = await source.LongCountAsync(ct);
        var rows = await source.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id)
            .Skip((query.Page - 1) * query.Limit).Take(query.Limit).ToListAsync(ct);
        return new(rows.Select(Webhook).ToArray(), query.Page, query.Limit, total);
    }

    public async Task<WebhookDeliveryView?> GetWebhookDeliveryAsync(Guid id, DeliveryAccess access, CancellationToken ct)
    {
        var row = await Scope(db.WebhookDeliveries.AsNoTracking(), access).SingleOrDefaultAsync(x => x.Id == id, ct);
        return row is null ? null : Webhook(row);
    }

    public async Task<WebhookReplayResult?> ReplayAsync(Guid id, Guid actorId, string idempotencyKey,
        DeliveryAccess access, CancellationToken ct)
    {
        ValidateKey(idempotencyKey);
        return await unitOfWork.ExecuteInTransactionAsync(async token =>
        {
            var prior = await Scope(db.WebhookDeliveries, access).SingleOrDefaultAsync(
                x => x.OriginalDeliveryId == id && x.ReplayKey == idempotencyKey, token);
            if (prior is not null) return new WebhookReplayResult(Webhook(prior), Replayed: true);
            var source = await Scope(db.WebhookDeliveries, access).SingleOrDefaultAsync(x => x.Id == id, token);
            if (source is null) return null;
            WebhookDelivery replay;
            try { replay = WebhookDelivery.Replay(source, idempotencyKey, clock.UtcNow); }
            catch (InvalidOperationException ex) { throw new ConflictException(ex.Message, "replay_ineligible"); }
            db.WebhookDeliveries.Add(replay); await unitOfWork.SaveChangesAsync(token);
            return new WebhookReplayResult(Webhook(replay), Replayed: false);
        }, ct);
    }

    public async Task<PagedResult<NotificationRuleView>> ListRulesAsync(
        NotificationRuleQuery query, DeliveryAccess access, CancellationToken ct)
    {
        var source = Scope(db.NotificationRules.AsNoTracking(), access);
        if (query.MerchantId is { } merchantId) source = source.Where(x => x.MerchantId == merchantId);
        if (query.Enabled is { } enabled) source = source.Where(x => x.Enabled == enabled);
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var pattern = $"%{SfsLike.Escape(query.Search.Trim())}%";
            source = source.Where(x => EF.Functions.Like(x.EventType, pattern, "\\")
                || EF.Functions.Like(x.Channel, pattern, "\\"));
        }
        var total = await source.LongCountAsync(ct);
        var rows = await source.OrderBy(x => x.EventType).ThenBy(x => x.Id)
            .Skip((query.Page - 1) * query.Limit).Take(query.Limit).ToListAsync(ct);
        return new(rows.Select(Rule).ToArray(), query.Page, query.Limit, total);
    }

    public async Task<NotificationRuleView?> GetRuleAsync(Guid id, DeliveryAccess access, CancellationToken ct)
    {
        var row = await Scope(db.NotificationRules.AsNoTracking(), access).SingleOrDefaultAsync(x => x.Id == id, ct);
        return row is null ? null : Rule(row);
    }

    public async Task<NotificationRuleMutation> CreateRuleAsync(Guid merchantId, string eventType, string channel,
        string destination, string? threshold, bool enabled, Guid actorId, string idempotencyKey,
        DeliveryAccess access, CancellationToken ct)
    {
        EnsureAccess(access, merchantId); ValidateRule(eventType, channel, destination);
        var result = await operations.ExecuteAsync(
            actorId, merchantId, "notification-rule.create", idempotencyKey,
            new { merchantId, eventType, channel, destination, threshold, enabled }, 201,
            async token =>
            {
                var row = NotificationRule.Create(merchantId, eventType, channel, destination, threshold, enabled, clock.UtcNow);
                db.NotificationRules.Add(row); await Task.CompletedTask;
                return new NotificationRuleMutation(Rule(row), Replayed: false);
            }, ct);
        return result.Value with { Replayed = result.Replayed };
    }

    public async Task<NotificationRuleMutation?> UpdateRuleAsync(Guid id, string eventType, string channel,
        string destination, string? threshold, bool enabled, long version, Guid actorId, string idempotencyKey,
        DeliveryAccess access, CancellationToken ct)
    {
        ValidateRule(eventType, channel, destination);
        var merchantId = await RuleMerchantAsync(id, access, ct); if (merchantId is null) return null;
        var result = await operations.ExecuteAsync(
            actorId, merchantId.Value, "notification-rule.update", idempotencyKey,
            new { id, eventType, channel, destination, threshold, enabled, version }, 200,
            async token =>
            {
                var row = await Scope(db.NotificationRules, access).SingleAsync(x => x.Id == id, token);
                EnsureVersion(row.Version, version); row.Update(eventType, channel, destination, threshold, enabled, clock.UtcNow);
                return new NotificationRuleMutation(Rule(row), Replayed: false);
            }, ct);
        return result.Value with { Replayed = result.Replayed };
    }

    public async Task<bool?> DeleteRuleAsync(Guid id, long version, Guid actorId, string idempotencyKey,
        DeliveryAccess access, CancellationToken ct)
    {
        var merchantId = await RuleMerchantAsync(id, access, ct); if (merchantId is null) return null;
        var result = await operations.ExecuteAsync(
            actorId, merchantId.Value, "notification-rule.delete", idempotencyKey,
            new { id, version }, 204,
            async token =>
            {
                var row = await Scope(db.NotificationRules, access).SingleAsync(x => x.Id == id, token);
                EnsureVersion(row.Version, version);
                if (await db.NotificationDeliveries.AnyAsync(x => x.RuleId == id, token))
                    throw new ConflictException("Notification rule has delivery history.", "rule_referenced");
                db.NotificationRules.Remove(row); return new DeleteMutation(true);
            }, ct);
        return result.Value.Deleted;
    }

    public async Task<PagedResult<NotificationDeliveryView>> ListNotificationDeliveriesAsync(
        NotificationDeliveryQuery query, DeliveryAccess access, CancellationToken ct)
    {
        var source = Scope(db.NotificationDeliveries.AsNoTracking(), access);
        if (query.MerchantId is { } merchantId) source = source.Where(x => x.MerchantId == merchantId);
        if (!string.IsNullOrWhiteSpace(query.Channel))
        {
            var channel = query.Channel.Trim().ToLowerInvariant();
            source = source.Where(x => x.Channel == channel);
        }
        if (!string.IsNullOrWhiteSpace(query.Status))
        {
            var status = ParseStatus(query.Status); source = source.Where(x => x.Status == status);
        }
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var pattern = $"%{SfsLike.Escape(query.Search.Trim())}%";
            source = source.Where(x => EF.Functions.Like(x.EventType, pattern, "\\")
                || EF.Functions.Like(x.Channel, pattern, "\\"));
        }
        var total = await source.LongCountAsync(ct);
        var rows = await source.OrderByDescending(x => x.SentAt).ThenByDescending(x => x.Id)
            .Skip((query.Page - 1) * query.Limit).Take(query.Limit).ToListAsync(ct);
        return new(rows.Select(Notification).ToArray(), query.Page, query.Limit, total);
    }

    public async Task<NotificationDeliveryView?> GetNotificationDeliveryAsync(
        Guid id, DeliveryAccess access, CancellationToken ct)
    {
        var row = await Scope(db.NotificationDeliveries.AsNoTracking(), access).SingleOrDefaultAsync(x => x.Id == id, ct);
        return row is null ? null : Notification(row);
    }

    public Task EnqueueAsync(Guid sourceEventId, Guid merchantId, string eventType,
        string? transactionId, string payload, CancellationToken ct)
    {
        ValidateEvent(eventType);
        if (Encoding.UTF8.GetByteCount(payload) > 256 * 1024)
            throw new InvalidRequestException("Webhook payload exceeds 256 KiB.", "payload_too_large");
        return unitOfWork.ExecuteInTransactionAsync(async token =>
        {
            var endpoints = await db.WebhookEndpoints.Where(x => x.MerchantId == merchantId && x.Enabled).ToListAsync(token);
            foreach (var endpoint in endpoints.Where(x => x.Events().Contains(eventType, StringComparer.Ordinal)))
            {
                if (!await db.WebhookDeliveries.AnyAsync(
                    x => x.EndpointId == endpoint.Id && x.SourceEventId == sourceEventId && x.OriginalDeliveryId == null, token))
                    db.WebhookDeliveries.Add(WebhookDelivery.Create(
                        endpoint.Id, merchantId, sourceEventId, eventType, transactionId, payload, clock.UtcNow));
            }
            var rules = await db.NotificationRules.Where(
                x => x.MerchantId == merchantId && x.Enabled && x.EventType == eventType).ToListAsync(token);
            foreach (var rule in rules)
            {
                if (!await db.NotificationDeliveries.AnyAsync(
                    x => x.RuleId == rule.Id && x.SourceEventId == sourceEventId, token))
                    db.NotificationDeliveries.Add(NotificationDelivery.Record(
                        rule, sourceEventId, Mask(rule.Destination), delivered: true, failureCode: null, clock.UtcNow));
            }
            await unitOfWork.SaveChangesAsync(token); return true;
        }, ct);
    }

    private async Task<Guid?> EndpointMerchantAsync(Guid id, DeliveryAccess access, CancellationToken ct) =>
        await Scope(db.WebhookEndpoints.AsNoTracking(), access).Where(x => x.Id == id)
            .Select(x => (Guid?)x.MerchantId).SingleOrDefaultAsync(ct);
    private async Task<Guid?> RuleMerchantAsync(Guid id, DeliveryAccess access, CancellationToken ct) =>
        await Scope(db.NotificationRules.AsNoTracking(), access).Where(x => x.Id == id)
            .Select(x => (Guid?)x.MerchantId).SingleOrDefaultAsync(ct);

    private static void ValidateRule(string eventType, string channel, string destination)
    {
        ValidateEvent(eventType);
        if (!string.Equals(channel, "inapp", StringComparison.Ordinal))
            throw new InvalidRequestException("Notification channel is unavailable.", "channel_unavailable");
        if (!string.Equals(destination, "admin-console", StringComparison.Ordinal))
            throw new InvalidRequestException("In-app destination must be admin-console.", "validation_failed");
    }

    private static void ValidateEvents(IReadOnlyCollection<string> events)
    {
        if (events.Count == 0 || events.Count != events.Distinct(StringComparer.Ordinal).Count()
            || events.Any(x => !SupportedEvents.Contains(x, StringComparer.Ordinal)))
            throw new InvalidRequestException("Webhook events are invalid.", "validation_failed");
    }

    private static void ValidateEvent(string value)
    {
        if (!SupportedEvents.Contains(value, StringComparer.Ordinal))
            throw new InvalidRequestException("Event is unsupported.", "validation_failed");
    }

    private static void ValidateKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 200)
            throw new InvalidRequestException("Idempotency-Key is invalid.", "validation_failed");
    }

    private static DeliveryStatus ParseStatus(string value) => value.Trim().ToLowerInvariant() switch
    {
        "pending" => DeliveryStatus.Pending,
        "processing" => DeliveryStatus.Processing,
        "delivered" => DeliveryStatus.Delivered,
        "failed" => DeliveryStatus.Failed,
        _ => throw new InvalidRequestException("Delivery status is invalid.", "invalid_filter"),
    };

    private static void EnsureAccess(DeliveryAccess access, Guid merchantId)
    {
        if (!access.Allows(merchantId)) throw new AccessDeniedException("Merchant is outside current scope.");
    }
    private static void EnsureVersion(long actual, long expected)
    {
        if (actual != expected) throw new ConcurrencyConflictException("Resource changed.");
    }

    private static IQueryable<WebhookEndpoint> Scope(IQueryable<WebhookEndpoint> q, DeliveryAccess a) =>
        a.IsUnrestricted ? q : q.Where(x => a.MerchantIds.Contains(x.MerchantId));
    private static IQueryable<WebhookDelivery> Scope(IQueryable<WebhookDelivery> q, DeliveryAccess a) =>
        a.IsUnrestricted ? q : q.Where(x => a.MerchantIds.Contains(x.MerchantId));
    private static IQueryable<NotificationRule> Scope(IQueryable<NotificationRule> q, DeliveryAccess a) =>
        a.IsUnrestricted ? q : q.Where(x => a.MerchantIds.Contains(x.MerchantId));
    private static IQueryable<NotificationDelivery> Scope(IQueryable<NotificationDelivery> q, DeliveryAccess a) =>
        a.IsUnrestricted ? q : q.Where(x => a.MerchantIds.Contains(x.MerchantId));

    private static WebhookEndpointView Endpoint(WebhookEndpoint x) => new(
        x.Id, x.MerchantId, x.Name, x.Url, x.Events(), x.Enabled, x.SecretHint, x.CreatedAt, x.UpdatedAt, x.Version);
    private static WebhookDeliveryView Webhook(WebhookDelivery x) => new(
        x.Id, x.EndpointId, x.MerchantId, x.OriginalDeliveryId, x.EventType, x.TransactionId,
        x.Status.ToString().ToLowerInvariant(), x.AttemptCount, x.LatencyMs, x.FailureCode,
        x.CreatedAt, x.CompletedAt, x.Status == DeliveryStatus.Failed);
    private static NotificationRuleView Rule(NotificationRule x) => new(
        x.Id, x.MerchantId, x.EventType, x.Channel, Mask(x.Destination), x.Threshold,
        x.Enabled, x.CreatedAt, x.UpdatedAt, x.Version);
    private static NotificationDeliveryView Notification(NotificationDelivery x) => new(
        x.Id, x.RuleId, x.MerchantId, x.EventType, x.Channel, x.DestinationMasked,
        x.Status.ToString().ToLowerInvariant(), x.FailureCode, x.SentAt);

    private static string Mask(string value)
    {
        if (MailAddress.TryCreate(value, out var email) && email.User.Length > 0) return $"{email.User[0]}***@{email.Host}";
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri)) return $"{uri.Scheme}://{uri.Host}/***";
        return value == "admin-console" ? value : "***";
    }

    private static string Token(int bytes) => Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private sealed record DeleteMutation(bool Deleted);
}

internal sealed class WebhookEndpointConfiguration : IEntityTypeConfiguration<WebhookEndpoint>
{
    public void Configure(EntityTypeBuilder<WebhookEndpoint> b)
    {
        b.ToTable("WebhookEndpoints", SchemaNames.Admin); b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasMaxLength(160).IsRequired(); b.Property(x => x.Url).HasMaxLength(2048).IsRequired();
        b.Property(x => x.EventsCsv).HasMaxLength(2000).IsRequired(); b.Property(x => x.SecretHint).HasMaxLength(32).IsRequired();
        b.Property(x => x.Version).IsConcurrencyToken(); b.HasIndex(x => new { x.MerchantId, x.Enabled });
        TenantKeyDescriptor.Require(b.Metadata, nameof(WebhookEndpoint.MerchantId));
    }
}

internal sealed class WebhookDeliveryConfiguration : IEntityTypeConfiguration<WebhookDelivery>
{
    public void Configure(EntityTypeBuilder<WebhookDelivery> b)
    {
        b.ToTable("WebhookDeliveries", SchemaNames.Admin); b.HasKey(x => x.Id);
        b.Property(x => x.EventType).HasMaxLength(160).IsRequired(); b.Property(x => x.TransactionId).HasMaxLength(200);
        b.Property(x => x.Payload).IsRequired(); b.Property(x => x.ReplayKey).HasMaxLength(200);
        b.Property(x => x.Status).HasConversion<int>(); b.Property(x => x.FailureCode).HasMaxLength(120);
        b.Property(x => x.LeaseOwner).HasMaxLength(200); b.HasIndex(x => new { x.Status, x.NextAttemptAt, x.LeaseExpiresAt });
        b.HasIndex(x => new { x.EndpointId, x.SourceEventId }).IsUnique().HasFilter("[OriginalDeliveryId] IS NULL");
        b.HasIndex(x => new { x.OriginalDeliveryId, x.ReplayKey }).IsUnique().HasFilter("[OriginalDeliveryId] IS NOT NULL");
        b.HasIndex(x => new { x.MerchantId, x.Status, x.CreatedAt });
        TenantKeyDescriptor.Require(b.Metadata, nameof(WebhookDelivery.MerchantId));
    }
}

internal sealed class NotificationRuleConfiguration : IEntityTypeConfiguration<NotificationRule>
{
    public void Configure(EntityTypeBuilder<NotificationRule> b)
    {
        b.ToTable("NotificationRules", SchemaNames.Admin); b.HasKey(x => x.Id);
        b.Property(x => x.EventType).HasMaxLength(160).IsRequired(); b.Property(x => x.Channel).HasMaxLength(32).IsRequired();
        b.Property(x => x.Destination).HasMaxLength(2048).IsRequired(); b.Property(x => x.Threshold).HasMaxLength(200);
        b.Property(x => x.Version).IsConcurrencyToken(); b.HasIndex(x => new { x.MerchantId, x.Enabled });
        TenantKeyDescriptor.Require(b.Metadata, nameof(NotificationRule.MerchantId));
    }
}

internal sealed class NotificationDeliveryConfiguration : IEntityTypeConfiguration<NotificationDelivery>
{
    public void Configure(EntityTypeBuilder<NotificationDelivery> b)
    {
        b.ToTable("NotificationDeliveries", SchemaNames.Admin); b.HasKey(x => x.Id);
        b.Property(x => x.EventType).HasMaxLength(160).IsRequired(); b.Property(x => x.Channel).HasMaxLength(32).IsRequired();
        b.Property(x => x.DestinationMasked).HasMaxLength(256).IsRequired(); b.Property(x => x.Status).HasConversion<int>();
        b.Property(x => x.FailureCode).HasMaxLength(120); b.HasIndex(x => new { x.MerchantId, x.SentAt });
        b.HasIndex(x => new { x.RuleId, x.SourceEventId }).IsUnique();
        TenantKeyDescriptor.Require(b.Metadata, nameof(NotificationDelivery.MerchantId));
        AppendOnlyDescriptor.Mark(b.Metadata);
    }
}

internal sealed class DeliverySecretVersionConfiguration : IEntityTypeConfiguration<DeliverySecretVersion>
{
    public void Configure(EntityTypeBuilder<DeliverySecretVersion> b)
    {
        b.ToTable("DeliverySecretVersions", SchemaNames.Admin); b.HasKey(x => x.Id);
        b.Property(x => x.OwnerType).HasMaxLength(64).IsRequired(); b.Property(x => x.ProtectedSecret).HasMaxLength(4096).IsRequired();
        b.Property(x => x.State).HasConversion<int>(); b.HasIndex(x => new { x.OwnerType, x.OwnerId, x.State });
        TenantKeyDescriptor.Require(b.Metadata, nameof(DeliverySecretVersion.MerchantId));
    }
}
