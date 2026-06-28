using System.Threading.RateLimiting;

namespace Api;

/// <summary>
/// Per-source-IP rate limit for the anonymous producer auth surface — the ticket-gated register endpoint
/// (REQ-13.4) and the login redirect (REQ-8.4, Task 5). The single-use signed ticket is the register endpoint's
/// capability barrier (no session CSRF on a pre-session route); this limiter blunts brute-force/abuse on top.
/// A second AddRateLimiter call only ADDS this policy onto the shared RateLimiterOptions (the middleware + 429
/// status are configured once elsewhere).
/// </summary>
// ponytail: DUPLICATE-shaped of AdminAuthRateLimiting (distinct policy name + partition) — deliberate.
internal static class ProducerAuthRateLimiting
{
    public const string PolicyName = "producer-auth";

    public static IServiceCollection AddProducerAuthRateLimiter(this IServiceCollection services) =>
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
