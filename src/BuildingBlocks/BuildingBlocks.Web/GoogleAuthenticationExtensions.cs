using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;

namespace BuildingBlocks.Web;

/// <summary>
/// Single source of truth for Google ID-token validation for the API. The API serves more than one browser
/// SPA, each with its own Google OAuth client AND its own role. <c>Google:Audiences</c> maps each role
/// (e.g. "tenant", "admin") to its OAuth client id: every mapped client id is an accepted token audience,
/// and the validated audience is turned into a "role" claim so endpoints can authorize per SPA — a tenant-SPA
/// token must not reach an admin route, and vice versa — even though both authenticate on the one scheme.
/// One named authorization policy is registered per role, so an endpoint gates with RequireAuthorization("tenant").
/// Setting <c>Authority</c> makes the JwtBearer handler fetch Google's OIDC metadata + JWKS and validate the
/// RS256 signature at runtime — no client secret, no embedded keys. Issuer, audience, lifetime, a verified
/// email, and (when configured) the hosted domain are all enforced.
/// </summary>
public static class GoogleAuthenticationExtensions
{
    public const string RoleClaimType = "role";

    public static IServiceCollection AddGoogleIdTokenAuthentication(
        this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var audiences = configuration.GetSection("Google:Audiences").Get<Dictionary<string, string>>() ?? [];
        var clientIds = audiences.Values.Where(id => !string.IsNullOrWhiteSpace(id)).ToArray();
        var hostedDomain = configuration["Google:HostedDomain"];

        // Fail fast OUTSIDE Development: never boot a real host that would "validate" tokens against an
        // empty/placeholder audience. Development may boot on the committed placeholder (no real tokens);
        // a developer sets Google__Audiences__* via user-secrets only when exercising the live SSO flow.
        var isUnset = clientIds.Length == 0 ||
            clientIds.All(id => id.StartsWith("REPLACE_WITH_", StringComparison.Ordinal));
        if (isUnset && !environment.IsDevelopment())
        {
            throw new InvalidOperationException(
                "Google:Audiences is not configured. Map each SPA role to its client id via " +
                "Google__Audiences__tenant / Google__Audiences__admin (environment variables or user-secrets).");
        }

        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.Authority = "https://accounts.google.com";
                options.RequireHttpsMetadata = true;   // OIDC discovery + JWKS must be fetched over HTTPS
                options.MapInboundClaims = false;       // keep raw OIDC claim names (sub/email/hd/aud/email_verified)
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuers = ["https://accounts.google.com", "accounts.google.com"],
                    ValidateAudience = true,
                    ValidAudiences = clientIds,
                    ValidateLifetime = true,
                    RequireExpirationTime = true,
                };
                options.Events = new JwtBearerEvents
                {
                    OnTokenValidated = context =>
                    {
                        var principal = context.Principal;
                        if (principal?.FindFirst("email_verified")?.Value is not "true")
                        {
                            context.Fail("The 'email_verified' claim is required.");
                            return Task.CompletedTask;
                        }

                        if (!string.IsNullOrEmpty(hostedDomain) &&
                            principal.FindFirst("hd")?.Value != hostedDomain)
                        {
                            context.Fail("The token's hosted domain ('hd') is not allowed.");
                            return Task.CompletedTask;
                        }

                        // The validated audience identifies which SPA issued the token -> its role claim,
                        // which the per-role authorization policies below gate on.
                        if (RoleForAudience(audiences, principal.FindFirst("aud")?.Value) is { } role)
                            ((ClaimsIdentity)principal.Identity!).AddClaim(new Claim(RoleClaimType, role));

                        return Task.CompletedTask;
                    },
                };
            });

        // One authorization policy per role, named after the role: RequireAuthorization("tenant") admits only
        // an authenticated token whose validated audience mapped to the "tenant" role (anonymous -> 401,
        // wrong role -> 403).
        var authz = services.AddAuthorizationBuilder();
        foreach (var role in audiences.Keys)
            authz.AddPolicy(role, policy => policy.RequireAuthenticatedUser().RequireClaim(RoleClaimType, role));

        return services;
    }

    /// <summary>The role whose configured client id equals <paramref name="audience"/>, or null when none.</summary>
    internal static string? RoleForAudience(IReadOnlyDictionary<string, string> audiences, string? audience) =>
        audience is null ? null : audiences.FirstOrDefault(kv => kv.Value == audience).Key;
}
