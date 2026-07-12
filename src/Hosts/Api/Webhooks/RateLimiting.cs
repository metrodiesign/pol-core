using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;

namespace Api.Webhooks;

/// <summary>
/// Flood protection for the unauthenticated webhook surface, using the built-in rate limiter (no package).
/// Lives in this host because Api is the only host with a webhook. A sliding window smooths a
/// PSP's bursty/retry delivery (a fixed window would 429 a burst straddling a window edge). The partition
/// is the CALLER (source IP), not the route's <c>pspConnectionId</c>: that id is a client-supplied,
/// GUID-format-only value, so partitioning on it would hand an attacker a fresh budget per random GUID —
/// the limit would never trip and the partition table would grow unbounded. Source IP is the flood
/// dimension, so a rotating-GUID flood from one host shares ONE bounded budget and is rejected in
/// middleware BEFORE the merchant-resolve DB lookup. <c>QueueLimit = 0</c> rejects an over-limit request with
/// 429 immediately rather than holding the connection open — a held connection makes PSPs retry harder.
/// Behind a reverse proxy, configure ForwardedHeaders so this is the real client IP; otherwise all webhook
/// traffic shares the proxy's single bounded budget — still safe, just a global cap instead of per-source.
/// </summary>
internal static class RateLimiting
{
    public const string PolicyName = "psp-webhook";

    public static IServiceCollection AddWebhookRateLimiter(this IServiceCollection services) =>
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            options.AddPolicy(PolicyName, httpContext =>
            {
                var key = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

                return RateLimitPartition.GetSlidingWindowLimiter(key, _ => new SlidingWindowRateLimiterOptions
                {
                    PermitLimit = 60,
                    Window = TimeSpan.FromSeconds(10),
                    SegmentsPerWindow = 5,
                    QueueLimit = 0,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    AutoReplenishment = true,
                });
            });

            options.OnRejected = (context, _) =>
            {
                // Always give a well-behaved PSP an explicit back-off instead of letting it hot-loop retries.
                // Use the limiter's own estimate when it provides one, else one window segment (2s).
                var retryAfterSeconds = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter)
                    ? (int)retryAfter.TotalSeconds
                    : 2;
                context.HttpContext.Response.Headers.RetryAfter =
                    retryAfterSeconds.ToString(CultureInfo.InvariantCulture);
                return ValueTask.CompletedTask;
            };
        });
}
