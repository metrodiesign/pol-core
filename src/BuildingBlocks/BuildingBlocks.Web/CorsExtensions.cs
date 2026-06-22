using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BuildingBlocks.Web;

/// <summary>
/// CORS for the browser SPA frontend (a separate origin — these hosts are JSON APIs, not server-rendered
/// pages). Origins come from config <c>Cors:AllowedOrigins</c> (env <c>Cors__AllowedOrigins__0</c>…), so prod
/// sets the real frontend origin and dev uses localhost. Never <c>AllowAnyOrigin</c>: the API is
/// authenticated, so it allowlists explicit origins. Auth is a Bearer Authorization header (Google ID token),
/// not a cookie, so <c>AllowAnyHeader</c> covers it and credentials/cookies are deliberately NOT enabled.
/// When no origins are configured the policy allows no cross-origin request (safe default — prod must set it).
/// </summary>
public static class CorsExtensions
{
    public const string PolicyName = "pol-spa";

    public static IServiceCollection AddPolCors(this IServiceCollection services, IConfiguration configuration)
    {
        // Named policy applied via app.UseCors(PolicyName) is the global form: the CORS middleware applies it
        // to every request whose matched endpoint has no CORS metadata of its own (a minimal-API host has
        // none). A parameterless UseCors()/default policy instead requires each endpoint to opt in.
        // Origins are read INSIDE the policy builder (lazy): CorsOptions is built on the first CORS request,
        // by which time every config source is layered — eager-reading here would miss late-bound overrides.
        return services.AddCors(options =>
            options.AddPolicy(PolicyName, policy =>
            {
                var origins = configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
                if (origins.Length == 0)
                    return; // no origins configured -> no cross-origin request is allowed

                policy.WithOrigins(origins)
                      .AllowAnyHeader()
                      .AllowAnyMethod();
            }));
    }

    /// <summary>Applies the policy globally. Must run after routing and BEFORE auth so a preflight (OPTIONS)
    /// is answered without an auth challenge.</summary>
    public static IApplicationBuilder UsePolCors(this IApplicationBuilder app) => app.UseCors(PolicyName);
}
