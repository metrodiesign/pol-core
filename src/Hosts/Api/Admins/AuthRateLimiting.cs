using Admins.Application;
using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace Api.Admins;

/// <summary>
/// Flood protection for admin auth and rare Super identity mutations. Source-IP limits run before authentication
/// so forged session cookies cannot force unbounded database lookups; the mutation endpoint adds a second,
/// post-authentication limit partitioned by internal Admin ID. Sliding windows never queue connections.
/// </summary>
internal static class AuthRateLimiting
{
    public const string PolicyName = "admin-auth";
    private const string IdentityMutationIpPolicyName = "admin-identity-mutation-ip";

    public static IServiceCollection AddAdminAuthRateLimiter(this IServiceCollection services)
    {
        services.AddSingleton(_ => PartitionedRateLimiter.Create<Guid, Guid>(adminId =>
            RateLimitPartition.GetSlidingWindowLimiter(adminId, _ => Window())));

        // A second AddRateLimiter call only ADDS this policy (the middleware + 429 status are configured once by
        // the webhook limiter); both policies coexist on the shared RateLimiterOptions.
        return services.AddRateLimiter(options =>
        {
            options.AddPolicy(PolicyName, httpContext =>
            {
                var key = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                return RateLimitPartition.GetSlidingWindowLimiter(key, _ => Window());
            });
            options.AddPolicy(IdentityMutationIpPolicyName, httpContext =>
            {
                var key = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                return RateLimitPartition.GetSlidingWindowLimiter(key, _ => Window());
            });
        });
    }

    public static RouteHandlerBuilder RequireAdminIdentityMutationRateLimit(this RouteHandlerBuilder builder)
    {
        builder.RequireRateLimiting(IdentityMutationIpPolicyName);
        return builder.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            var adminId = http.RequestServices.GetRequiredService<IAdminScope>().Current.AdminId;
            var limiter = http.RequestServices.GetRequiredService<PartitionedRateLimiter<Guid>>();
            using var lease = limiter.AttemptAcquire(adminId, permitCount: 1);
            if (lease.IsAcquired)
                return await next(context);

            var retryAfterSeconds = lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter)
                ? Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds))
                : 10;
            http.Response.Headers.RetryAfter = retryAfterSeconds.ToString(CultureInfo.InvariantCulture);
            return Results.Problem(
                statusCode: StatusCodes.Status429TooManyRequests,
                title: "Too many requests.",
                extensions: new Dictionary<string, object?> { ["traceId"] = http.TraceIdentifier });
        });
    }

    private static SlidingWindowRateLimiterOptions Window() => new()
    {
        PermitLimit = 20,
        Window = TimeSpan.FromSeconds(60),
        SegmentsPerWindow = 6,
        QueueLimit = 0,
        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        AutoReplenishment = true,
    };
}
