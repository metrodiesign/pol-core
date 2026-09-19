using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;

namespace Api.Accounts;

internal static class AgentRegistrationRateLimiting
{
    public const string PolicyName = "agent-registration";

    public static IServiceCollection AddAgentRegistrationRateLimiter(this IServiceCollection services) =>
        services.AddRateLimiter(options => options.AddPolicy(PolicyName, http =>
        {
            var cookie = http.Request.Cookies[RegistrationSessionCookie.Name];
            var key = string.IsNullOrEmpty(cookie)
                ? $"ip:{http.Connection.RemoteIpAddress?.ToString() ?? "unknown"}"
                : $"session:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(cookie)))}";
            return RateLimitPartition.GetSlidingWindowLimiter(key, _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = 20,
                Window = TimeSpan.FromMinutes(1),
                SegmentsPerWindow = 6,
                QueueLimit = 0,
                AutoReplenishment = true,
            });
        }));
}
