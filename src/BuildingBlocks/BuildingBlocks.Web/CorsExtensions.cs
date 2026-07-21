using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace BuildingBlocks.Web;

/// <summary>
/// CORS for the two browser SPA frontends served by the one API (REQ-10.5). They have DIFFERENT origins but the
/// SAME credential posture now: T5 collapsed the old Bearer "tenant" scheme into the merchant-user session
/// cookie, which the WHOLE funnel authenticates with (not just <c>/merchants/users/*</c> — every endpoint that
/// used to gate on policy <c>tenant</c>: products/carts/checkouts/orders/payments/reports), so there is no more
/// uncredentialed surface left to keep a separate default for.
/// <list type="bullet">
/// <item>the <b>default</b> policy is the merchant-user SPA — cookie (credentialed) XHR, so it sets
/// <c>AllowCredentials</c>, which the spec forbids pairing with a wildcard origin: the origins are pinned
/// explicitly.</item>
/// <item><see cref="AdminPolicyName"/> is the admin SPA — also cookie (credentialed) XHR, its own pinned origin
/// set. Applied to <c>/api/v1/admins/*</c> PLUS the two admin-provisioned merchant endpoints that moved out of
/// that group (hierarchical-naming D9): <c>/api/v1/merchants</c> and <c>/api/v1/merchants/{code}</c> — but NOT
/// <c>/api/v1/merchants/users/*</c>, which is the merchant-user plane and stays on the default policy.</item>
/// </list>
/// Origins come from config (<c>Cors:AllowedOrigins</c> merchant-user, <c>Cors:AdminOrigins</c> admin); never
/// <c>AllowAnyOrigin</c>. When a list is empty the policy allows no cross-origin request (safe default — prod
/// must set it). Splitting the two keeps enabling admin cookies from changing the merchant-user posture
/// (REQ-10.5/4.5).
/// </summary>
public static class CorsExtensions
{
    public const string AdminPolicyName = "pol-admin-spa";

    public static IServiceCollection AddPolCors(this IServiceCollection services, IConfiguration configuration)
    {
        // Origins are read INSIDE each policy builder (lazy): CorsOptions is built on the first CORS request, by
        // which time every config source is layered — eager-reading here would miss late-bound overrides.
        services.AddCors(options =>
        {
            options.AddDefaultPolicy(policy =>
            {
                var origins = configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
                if (origins.Length == 0)
                    return; // no origins configured -> no cross-origin request is allowed
                policy.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod().AllowCredentials(); // merchant-user: cookie XHR
            });

            options.AddPolicy(AdminPolicyName, policy =>
            {
                var origins = configuration.GetSection("Cors:AdminOrigins").Get<string[]>() ?? [];
                if (origins.Length == 0)
                    return; // no admin origin configured -> no cross-origin admin XHR
                policy.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod().AllowCredentials(); // admin: cookie XHR
            });
        });

        // Select the policy by path: the admin plane (see PolCorsPolicyProvider) -> credentialed admin policy,
        // everything else (the whole merchant-user funnel, incl. /api/v1/merchants/users/*) -> the credentialed
        // default. A provider (not per-endpoint RequireCors) so policy selection does not depend on endpoint
        // metadata being resolved before the CORS middleware runs.
        services.Replace(ServiceDescriptor.Transient<ICorsPolicyProvider, PolCorsPolicyProvider>());
        return services;
    }

    /// <summary>Applies CORS before auth so a browser preflight (OPTIONS) is answered without an auth
    /// challenge. The policy per request is chosen by <see cref="PolCorsPolicyProvider"/>.</summary>
    public static IApplicationBuilder UsePolCors(this IApplicationBuilder app) => app.UseCors();
}

/// <summary>Chooses the CORS policy by request path: the credentialed admin policy for the admin plane
/// (<see cref="IsAdminPlane"/>), the credentialed merchant-user default everywhere else (REQ-10.5).</summary>
public sealed class PolCorsPolicyProvider : ICorsPolicyProvider
{
    private readonly CorsOptions _options;

    public PolCorsPolicyProvider(IOptions<CorsOptions> options) => _options = options.Value;

    public Task<CorsPolicy?> GetPolicyAsync(HttpContext context, string? policyName)
    {
        var name = IsAdminPlane(context.Request.Path) ? CorsExtensions.AdminPolicyName : _options.DefaultPolicyName;
        return Task.FromResult(_options.GetPolicy(name));
    }

    // admin plane = /api/v1/admins/** + /api/v1/merchants and /api/v1/merchants/{code} (D9) — but NOT
    // /api/v1/merchants/users/** or /api/v1/merchants/auth/**, which are the merchant-user plane (REQ-8.2; auth
    // carries the provider-scoped login/callback/logout). Also the 4 standalone reference
    // master-data areas (2026-07-20) — they authenticate on the same AdminSession cookie despite living outside
    // /admins. Backed by a fail-closed guard (AdminCorsGuardTests) that enumerates every "admin"-policy endpoint
    // and asserts it resolves here.
    private static bool IsAdminPlane(PathString path) =>
        path.StartsWithSegments("/api/v1/admins")
        || (path.StartsWithSegments("/api/v1/merchants", out var rest)
            && !rest.StartsWithSegments("/users") && !rest.StartsWithSegments("/auth"))
        || path.StartsWithSegments("/api/v1/positions")
        || path.StartsWithSegments("/api/v1/offices")
        || path.StartsWithSegments("/api/v1/levels")
        || path.StartsWithSegments("/api/v1/divisions");
}
