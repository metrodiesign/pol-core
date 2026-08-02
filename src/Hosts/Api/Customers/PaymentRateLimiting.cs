using System.Threading.RateLimiting;

namespace Api.Customers;

/// <summary>
/// Per-source-IP rate limit for the anonymous customer payment surface — <c>POST /orders/{token}/pay</c>
/// and <c>POST /orders/{token}/payment-status</c> (REQ-8.3). Far tighter than the webhook limiter because
/// every call here costs real work off-box: a vault reveal plus a PSP charge or inquiry. The partition is
/// the CALLER (source IP), never the route's token — a client-supplied value would hand an attacker a
/// fresh budget per random token, so the limit would never trip and the partition table would grow
/// unbounded. A sliding window keeps a customer's legitimate poll-after-redirect from being 429'd by a
/// window edge. A second AddRateLimiter call only ADDS this policy onto the shared RateLimiterOptions (the
/// middleware + 429 status are configured once elsewhere).
/// </summary>
// ponytail: DUPLICATE-shaped of the two auth limiters (distinct policy name + budget) — deliberate.
internal static class PaymentRateLimiting
{
    public const string PolicyName = "customer-payment";

    public static IServiceCollection AddCustomerPaymentRateLimiter(this IServiceCollection services) =>
        services.AddRateLimiter(options =>
            options.AddPolicy(PolicyName, httpContext =>
            {
                var key = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                return RateLimitPartition.GetSlidingWindowLimiter(key, _ => new SlidingWindowRateLimiterOptions
                {
                    PermitLimit = 10,
                    Window = TimeSpan.FromSeconds(60),
                    SegmentsPerWindow = 6,
                    QueueLimit = 0,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    AutoReplenishment = true,
                });
            }));
}
