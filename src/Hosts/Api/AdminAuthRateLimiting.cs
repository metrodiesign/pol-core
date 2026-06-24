using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace Api;

/// <summary>
/// Flood protection for the admin auth endpoints (<c>GET /admin/auth/login</c> + the OIDC callback), partitioned
/// by source IP (REQ-1.6). Mirrors <see cref="WebhookRateLimiting"/>: a sliding window so a burst straddling a
/// window edge is smoothed, immediate rejection (no queued connections). Bounds login-spam / callback-probe
/// abuse from one source without affecting legitimate, infrequent admin logins.
/// </summary>
internal static class AdminAuthRateLimiting
{
    public const string PolicyName = "admin-auth";

    public static IServiceCollection AddAdminAuthRateLimiter(this IServiceCollection services) =>
        // A second AddRateLimiter call only ADDS this policy (the middleware + 429 status are configured once by
        // the webhook limiter); both policies coexist on the shared RateLimiterOptions.
        services.AddRateLimiter(options =>
            options.AddPolicy(PolicyName, httpContext =>
            {
                var key = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                return RateLimitPartition.GetSlidingWindowLimiter(key, _ => new SlidingWindowRateLimiterOptions
                {
                    PermitLimit = 20,
                    Window = TimeSpan.FromSeconds(60),
                    SegmentsPerWindow = 6,
                    QueueLimit = 0,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    AutoReplenishment = true,
                });
            }));
}
