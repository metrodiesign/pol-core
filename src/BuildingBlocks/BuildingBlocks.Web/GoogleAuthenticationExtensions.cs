using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;

namespace BuildingBlocks.Web;

/// <summary>
/// Single source of truth for Google ID-token validation for the API. The API serves more than one browser
/// SPA, each with its own Google OAuth client, so a valid token's audience may be ANY of the configured
/// client ids — <c>Google:ClientIds</c> (array) is the source of truth, with single <c>Google:ClientId</c>
/// honoured for back-compat. Setting <c>Authority</c> makes the JwtBearer handler fetch Google's OIDC
/// metadata + JWKS and validate the RS256 signature against Google's rotating keys at runtime — no client
/// secret, no embedded keys. Issuer, audience, lifetime, a verified email, and (when configured) the hosted
/// domain are all enforced.
/// </summary>
public static class GoogleAuthenticationExtensions
{
    public static IServiceCollection AddGoogleIdTokenAuthentication(
        this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var clientIds = configuration.GetSection("Google:ClientIds").Get<string[]>() ?? [];
        if (clientIds.Length == 0 && configuration["Google:ClientId"] is { } single && !string.IsNullOrWhiteSpace(single))
            clientIds = [single]; // back-compat: a single Google:ClientId still works

        var hostedDomain = configuration["Google:HostedDomain"];

        // Fail fast OUTSIDE Development: never boot a real host that would "validate" tokens against an
        // empty/placeholder audience. Development may boot on the committed placeholder (no real tokens);
        // a developer sets Google__ClientIds via user-secrets only when exercising the live SSO flow.
        var isUnset = clientIds.Length == 0 ||
            clientIds.All(id => string.IsNullOrWhiteSpace(id) || id.StartsWith("REPLACE_WITH_", StringComparison.Ordinal));
        if (isUnset && !environment.IsDevelopment())
        {
            throw new InvalidOperationException(
                "Google:ClientIds is not configured. Set it via the Google__ClientIds__0/__1 environment variables or user-secrets.");
        }

        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.Authority = "https://accounts.google.com";
                options.RequireHttpsMetadata = true;   // OIDC discovery + JWKS must be fetched over HTTPS
                options.MapInboundClaims = false;       // keep raw OIDC claim names (sub/email/hd/email_verified)
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
                        }

                        return Task.CompletedTask;
                    },
                };
            });
        services.AddAuthorization();
        return services;
    }
}
