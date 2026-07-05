using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace BuildingBlocks.Web;

/// <summary>
/// CORS for the two browser SPA frontends served by the one API (REQ-10.5). They have DIFFERENT credential
/// postures, so they get DIFFERENT policies, selected by request path via <see cref="PolCorsPolicyProvider"/>:
/// <list type="bullet">
/// <item>the <b>default</b> policy is the tenant SPA — Bearer (Authorization header) auth, so
/// <c>AllowAnyHeader</c> covers it and credentials/cookies are deliberately NOT enabled.</item>
/// <item><see cref="AdminPolicyName"/> is the admin SPA — cookie (credentialed) XHR, so it sets
/// <c>AllowCredentials</c>, which the spec forbids pairing with a wildcard origin: the origins are pinned
/// explicitly. Applied ONLY to <c>/api/v1/admins/*</c>.</item>
/// </list>
/// Origins come from config (<c>Cors:AllowedOrigins</c> tenant, <c>Cors:AdminOrigins</c> admin); never
/// <c>AllowAnyOrigin</c>. When a list is empty the policy allows no cross-origin request (safe default — prod
/// must set it). Splitting the old single shared policy keeps enabling admin cookies from changing the tenant
/// posture (REQ-10.5/4.5).
/// </summary>
public static class CorsExtensions
{
    public const string AdminPolicyName = "pol-admin-spa";
    public const string ProducerPolicyName = "pol-producer-spa";

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
                policy.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod(); // tenant: NO credentials
            });

            options.AddPolicy(AdminPolicyName, policy =>
            {
                var origins = configuration.GetSection("Cors:AdminOrigins").Get<string[]>() ?? [];
                if (origins.Length == 0)
                    return; // no admin origin configured -> no cross-origin admin XHR
                policy.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod().AllowCredentials(); // admin: cookie XHR
            });

            // The producer SPA — also cookie (credentialed) XHR (producer-google-sso REQ-14.5), so its own
            // AllowCredentials policy pinned to Cors:ProducerOrigins. Applied ONLY to /api/v1/producers/*. Adding it leaves
            // the tenant (credential-less) and admin policies untouched.
            options.AddPolicy(ProducerPolicyName, policy =>
            {
                var origins = configuration.GetSection("Cors:ProducerOrigins").Get<string[]>() ?? [];
                if (origins.Length == 0)
                    return; // no producer origin configured -> no cross-origin producer XHR
                policy.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod().AllowCredentials();
            });
        });

        // Select the policy by path: /api/v1/admins/* -> credentialed admin policy, everything else -> tenant default.
        // A provider (not per-endpoint RequireCors) so policy selection does not depend on endpoint metadata
        // being resolved before the CORS middleware runs.
        services.Replace(ServiceDescriptor.Transient<ICorsPolicyProvider, PolCorsPolicyProvider>());
        return services;
    }

    /// <summary>Applies CORS before auth so a browser preflight (OPTIONS) is answered without an auth
    /// challenge. The policy per request is chosen by <see cref="PolCorsPolicyProvider"/>.</summary>
    public static IApplicationBuilder UsePolCors(this IApplicationBuilder app) => app.UseCors();
}

/// <summary>Chooses the CORS policy by request path: the credentialed admin policy for <c>/api/v1/admins/*</c>, the
/// credentialed producer policy for <c>/api/v1/producers/*</c> (REQ-14.5), the tenant default everywhere else (REQ-10.5).</summary>
public sealed class PolCorsPolicyProvider : ICorsPolicyProvider
{
    private readonly CorsOptions _options;

    public PolCorsPolicyProvider(IOptions<CorsOptions> options) => _options = options.Value;

    public Task<CorsPolicy?> GetPolicyAsync(HttpContext context, string? policyName)
    {
        var name = context.Request.Path.StartsWithSegments("/api/v1/admins") ? CorsExtensions.AdminPolicyName
            : context.Request.Path.StartsWithSegments("/api/v1/producers") ? CorsExtensions.ProducerPolicyName
            : _options.DefaultPolicyName;
        return Task.FromResult(_options.GetPolicy(name));
    }
}
