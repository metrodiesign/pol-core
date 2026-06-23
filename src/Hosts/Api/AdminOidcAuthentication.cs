using Microsoft.AspNetCore.Authentication.OpenIdConnect;

namespace Api;

/// <summary>
/// The confidential Google OIDC client for the admin BFF login (REQ-1/2). The framework handler does the
/// Authorization Code + PKCE + state + nonce + code-exchange + JWKS id_token validation; we only assert
/// <c>email_verified</c> + <c>hd</c> (REQ-2.3) and, on the canonical post-principal hook, establish the
/// server session ourselves and short-circuit the framework sign-in (<see cref="AdminLoginService"/>). Schemes
/// are ADDED here without changing the default (JwtBearer stays the default for tenant routes).
/// </summary>
internal static class AdminOidcAuthentication
{
    /// <summary>The OIDC scheme the admin login challenges (REQ-1.1).</summary>
    public const string Scheme = "Google";

    /// <summary>A throwaway cookie sign-in scheme: the OIDC handler needs a resolvable SignInScheme, but we call
    /// HandleResponse before sign-in, so it is never actually written.</summary>
    public const string SignInScheme = "oidc-noop";

    public static IServiceCollection AddAdminOidcAuthentication(
        this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var oidc = configuration.GetSection(AdminOidcOptions.SectionName).Get<AdminOidcOptions>() ?? new AdminOidcOptions();
        var hostedDomain = configuration["Google:HostedDomain"];

        // Parameterless AddAuthentication() does NOT set a default scheme, so the JwtBearer default that
        // AddGoogleIdTokenAuthentication established (tenant routes) is preserved.
        services.AddAuthentication()
            .AddCookie(SignInScheme)
            .AddOpenIdConnect(Scheme, options =>
            {
                options.Authority = oidc.Authority;
                options.ClientId = oidc.ClientId;
                options.ClientSecret = oidc.ClientSecret;
                options.CallbackPath = oidc.CallbackPath;
                options.SignInScheme = SignInScheme;

                options.ResponseType = "code";          // Authorization Code (REQ-1.1)
                options.UsePkce = true;                  // S256 code_challenge (REQ-1.1)
                options.SaveTokens = false;              // we never call Google APIs (REQ-1.5)
                options.GetClaimsFromUserInfoEndpoint = false;
                options.MapInboundClaims = false;        // keep raw claim names (sub/email/hd/email_verified)
                options.RequireHttpsMetadata = !environment.IsDevelopment();

                options.Scope.Clear();                   // default is {openid, profile}; we want only openid+email
                options.Scope.Add("openid");
                options.Scope.Add("email");

                options.TokenValidationParameters.ValidateIssuer = true;
                options.TokenValidationParameters.ValidIssuers = ["https://accounts.google.com", "accounts.google.com"];
                // aud is validated against ClientId by the handler; nonce + signature + lifetime too.

                options.Events = new OpenIdConnectEvents
                {
                    // REQ-2.3: the verified-email + hosted-domain gates the JWKS/iss/aud/nonce checks don't cover.
                    OnTokenValidated = context =>
                    {
                        var principal = context.Principal;
                        if (principal?.FindFirst("email_verified")?.Value is not "true")
                            context.Fail("email_verified-required");
                        else if (!string.IsNullOrEmpty(hostedDomain) && principal.FindFirst("hd")?.Value != hostedDomain)
                            context.Fail("hd-not-allowed");
                        return Task.CompletedTask;
                    },

                    // Canonical post-principal hook: resolve the admin, establish the server session + cookies,
                    // and short-circuit the framework sign-in (REQ-2.5/3.1).
                    OnTicketReceived = async context =>
                    {
                        var login = context.HttpContext.RequestServices.GetRequiredService<AdminLoginService>();
                        var principal = context.Principal;
                        await login.EstablishSessionAsync(
                            context.HttpContext,
                            principal?.FindFirst("sub")?.Value,
                            principal?.FindFirst("email")?.Value,
                            context.Properties?.RedirectUri,
                            context.HttpContext.RequestAborted);
                        context.HandleResponse();
                    },

                    // OAuth error=access_denied at Google (REQ-2.8).
                    OnAccessDenied = async context =>
                    {
                        var login = context.HttpContext.RequestServices.GetRequiredService<AdminLoginService>();
                        await login.DenyAsync(context.HttpContext, "access-denied", null, context.HttpContext.RequestAborted);
                        context.HandleResponse();
                    },

                    // State mismatch / code-exchange fail / email_verified|hd Fail / OAuth error (REQ-2.1/2.7/2.8/12.4).
                    OnRemoteFailure = async context =>
                    {
                        var login = context.HttpContext.RequestServices.GetRequiredService<AdminLoginService>();
                        await login.DenyAsync(context.HttpContext, MapFailureReason(context.Failure), null, context.HttpContext.RequestAborted);
                        context.HandleResponse();
                    },
                };
            });

        services.AddScoped<IAdminCallbackResolver, AdminCallbackResolver>();
        services.AddScoped<AdminLoginService>();
        return services;
    }

    private static string MapFailureReason(Exception? failure) => failure?.Message switch
    {
        "email_verified-required" => "email-unverified",
        "hd-not-allowed" => "hd-mismatch",
        _ => "auth-failed",
    };
}
