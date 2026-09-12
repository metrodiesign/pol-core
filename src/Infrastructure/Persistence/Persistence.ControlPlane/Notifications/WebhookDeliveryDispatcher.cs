using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using BuildingBlocks.Application;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Notifications.Application;
using Notifications.Domain;

namespace Persistence.ControlPlane.Notifications;

internal sealed class WebhookDeliveryDispatcher(
    IServiceScopeFactory scopeFactory,
    ILogger<WebhookDeliveryDispatcher> logger) : BackgroundService
{
    private static readonly string Owner = $"{Environment.MachineName}:{Environment.ProcessId}";
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var deliveryId = await ClaimAsync(stoppingToken);
                if (deliveryId is { } id) await DispatchAsync(id, stoppingToken);
                else await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Webhook delivery dispatcher batch failed.");
                await Task.Delay(PollInterval, stoppingToken);
            }
        }
    }

    private async Task<Guid?> ClaimAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        var clock = scope.ServiceProvider.GetRequiredService<IClock>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        WebhookDelivery? row;
        if (db.Database.IsSqlServer())
        {
            row = await db.WebhookDeliveries.FromSqlRaw(
                """
                SELECT TOP (1) *
                FROM admin.WebhookDeliveries WITH (READPAST, UPDLOCK, ROWLOCK)
                WHERE ((Status = {0} AND NextAttemptAt <= {1})
                    OR (Status = {2} AND LeaseExpiresAt < {1}))
                ORDER BY NextAttemptAt, CreatedAt, Id
                """, (int)DeliveryStatus.Pending, clock.UtcNow, (int)DeliveryStatus.Processing)
                .FirstOrDefaultAsync(cancellationToken);
        }
        else
        {
            row = await db.WebhookDeliveries.Where(x =>
                    x.Status == DeliveryStatus.Pending && x.NextAttemptAt <= clock.UtcNow
                    || x.Status == DeliveryStatus.Processing && x.LeaseExpiresAt < clock.UtcNow)
                .OrderBy(x => x.NextAttemptAt).ThenBy(x => x.CreatedAt).ThenBy(x => x.Id)
                .FirstOrDefaultAsync(cancellationToken);
        }
        if (row is null) { await transaction.CommitAsync(cancellationToken); return null; }
        row.Claim(Owner, clock.UtcNow, clock.UtcNow.Add(LeaseDuration));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return row.Id;
    }

    private async Task DispatchAsync(Guid deliveryId, CancellationToken cancellationToken)
    {
        DispatchSnapshot snapshot;
        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
            var row = await db.WebhookDeliveries.AsNoTracking().SingleAsync(x => x.Id == deliveryId, cancellationToken);
            var endpoint = await db.WebhookEndpoints.AsNoTracking().SingleAsync(x => x.Id == row.EndpointId, cancellationToken);
            var secret = await db.DeliverySecretVersions.AsNoTracking().SingleAsync(
                x => x.Id == endpoint.ActiveSecretVersionId && x.State == DeliverySecretState.Active, cancellationToken);
            snapshot = new(row.Id, row.AttemptCount, row.Payload, endpoint.Url, endpoint.Enabled, secret.ProtectedSecret);
        }

        var result = snapshot.Enabled
            ? await SendAsync(snapshot, cancellationToken)
            : new AttemptResult(false, "endpoint_disabled", Retry: false, 0);
        await FinishAsync(deliveryId, result, cancellationToken);
        if (!result.Delivered)
            logger.LogWarning("Webhook delivery {DeliveryId} failed with {FailureCode}.", deliveryId, result.FailureCode);
    }

    private async Task<AttemptResult> SendAsync(DispatchSnapshot snapshot, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        try
        {
            using var scope = scopeFactory.CreateScope();
            var resolved = await scope.ServiceProvider.GetRequiredService<ISafeDestinationValidator>()
                .ResolveAsync(snapshot.Url, cancellationToken);
            var protector = scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>()
                .CreateProtector("pol-core/delivery-secret/v1");
            var secret = protector.Unprotect(snapshot.ProtectedSecret);
            var handler = PinnedHandler(resolved.Address);
            using var client = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
            using var request = new HttpRequestMessage(HttpMethod.Post, resolved.Uri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
            request.Headers.TryAddWithoutValidation("X-POL-Delivery-Id", snapshot.Id.ToString("D"));
            request.Headers.TryAddWithoutValidation("X-POL-Timestamp", timestamp);
            request.Headers.TryAddWithoutValidation("X-POL-Signature", Sign(secret, timestamp, snapshot.Id, snapshot.Payload));
            request.Content = new StringContent(snapshot.Payload, Encoding.UTF8, "application/json");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (await ExceedsResponseLimitAsync(response, timeout.Token))
                return new AttemptResult(false, "response_too_large", Retry: false, Milliseconds(timer));
            var code = (int)response.StatusCode;
            if (code is >= 200 and < 300)
                return new AttemptResult(true, null, Retry: false, Milliseconds(timer));
            var retry = code is 408 or 429 || code >= 500;
            return new AttemptResult(false, code >= 500 ? "http_5xx" : "http_4xx", retry, Milliseconds(timer));
        }
        catch (InvalidRequestException)
        {
            return new AttemptResult(false, "dns_rejected", Retry: false, Milliseconds(timer));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new AttemptResult(false, "timeout", Retry: true, Milliseconds(timer));
        }
        catch (AuthenticationException)
        {
            return new AttemptResult(false, "tls_failed", Retry: true, Milliseconds(timer));
        }
        catch (HttpRequestException ex) when (ex.InnerException is AuthenticationException)
        {
            return new AttemptResult(false, "tls_failed", Retry: true, Milliseconds(timer));
        }
        catch (Exception ex) when (ex is HttpRequestException or SocketException or IOException)
        {
            return new AttemptResult(false, "network_error", Retry: true, Milliseconds(timer));
        }
    }

    private async Task FinishAsync(Guid deliveryId, AttemptResult result, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        var clock = scope.ServiceProvider.GetRequiredService<IClock>();
        var row = await db.WebhookDeliveries.SingleAsync(x => x.Id == deliveryId, cancellationToken);
        var retryAt = result.Retry && row.AttemptCount < WebhookDelivery.MaxAttempts
            ? clock.UtcNow.Add(RetryDelay(row.AttemptCount))
            : (DateTime?)null;
        row.Finish(result.Delivered, result.LatencyMs, result.FailureCode, clock.UtcNow, retryAt);
        await db.SaveChangesAsync(cancellationToken);
    }

    private static SocketsHttpHandler PinnedHandler(IPAddress address) => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        PooledConnectionLifetime = TimeSpan.Zero,
        ConnectCallback = async (context, cancellationToken) =>
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch { socket.Dispose(); throw; }
        },
    };

    private static string Sign(string secret, string timestamp, Guid deliveryId, string payload)
    {
        var key = Encoding.UTF8.GetBytes(secret);
        var body = Encoding.UTF8.GetBytes($"{timestamp}.{deliveryId:D}.{payload}");
        try { return $"sha256={Convert.ToHexString(HMACSHA256.HashData(key, body)).ToLowerInvariant()}"; }
        finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(body); }
    }

    private static async Task<bool> ExceedsResponseLimitAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > 64 * 1024) return true;
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[8192]; var total = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer, ct); if (read == 0) return false;
            total += read; if (total > 64 * 1024) return true;
        }
    }

    private static TimeSpan RetryDelay(int attempt) => TimeSpan.FromSeconds(
        Math.Min(300, Math.Pow(2, Math.Clamp(attempt, 1, 8))) + RandomNumberGenerator.GetInt32(0, 1000) / 1000d);
    private static int Milliseconds(Stopwatch timer) => (int)Math.Min(int.MaxValue, timer.ElapsedMilliseconds);
    private sealed record DispatchSnapshot(Guid Id, int Attempt, string Payload, string Url, bool Enabled, string ProtectedSecret);
    private sealed record AttemptResult(bool Delivered, string? FailureCode, bool Retry, int LatencyMs);
}
