using Microsoft.AspNetCore.Authentication.OpenIdConnect;

namespace Api;

/// <summary>
/// The confidential Google OIDC client for the merchant-user BFF login (REQ-8/9/14). The framework handler does the
/// Authorization Code + PKCE + state + nonce + code-exchange + JWKS id_token validation; we only hook
/// <c>OnTokenValidated</c> (email_verified + hd, REQ-9.2), <c>OnTicketReceived</c> (the 4-way lifecycle branch via
/// <see cref="MerchantUserLoginService"/>, REQ-9.4), and <c>OnRemoteFailure</c>/<c>OnAccessDenied</c> (deny → error
/// page, REQ-9.5). Fully isolated from the Admin <c>Google</c> client: a distinct scheme + sign-in scheme + callback
/// path (REQ-14.4). Schemes are ADDED here without changing the default (the merchant-user session scheme is the
/// default — the Bearer path is retired, REQ-6).
/// </summary>
// ponytail: DUPLICATE-shaped of AdminOidcAuthentication (distinct scheme/callback + 4-way hook) — deliberate.
internal static class MerchantUserOidcAuthentication
{
    /// <summary>The merchant-user OIDC scheme — distinct from Admin's <c>Google</c> (REQ-14.4). The distinct name
    /// also isolates the framework's correlation/nonce Data Protection purposes from the Admin client automatically.</summary>
    public const string Scheme = "MerchantUserGoogle";

    /// <summary>A throwaway cookie sign-in scheme (distinct from Admin's <c>oidc-noop</c>, REQ-14.4): the OIDC handler
    /// needs a resolvable SignInScheme, but we call HandleResponse before sign-in, so it is never actually written.</summary>
    public const string SignInScheme = "merchant-user-oidc-noop";

    public static IServiceCollection AddMerchantUserOidcAuthentication(
        this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var oidc = configuration.GetSection(MerchantUserOidcOptions.SectionName).Get<MerchantUserOidcOptions>() ?? new MerchantUserOidcOptions();

        services.AddScoped<IMerchantUserCallbackResolver, MerchantUserCallbackResolver>();
        services.AddScoped<MerchantUserLoginService>();

        // Blank ClientId -> skip the scheme (REQ-14.2). The OIDC scheme is a per-request handler that AuthN middleware
        // VALIDATES on every request, and OpenIdConnectOptions.Validate() requires a non-empty ClientId — a blank one
        // would throw on every request and take the WHOLE API down. The merchant-user FE is a later slice, so that
        // login may be intentionally unconfigured; skip the scheme so the rest of the API stays up. A login attempt
        // then fails loudly at the challenge rather than silently breaking every request. Mirrors Admin.
        if (string.IsNullOrWhiteSpace(oidc.ClientId))
            return services;

        // Parameterless AddAuthentication() keeps the existing default scheme intact.
        services.AddAuthentication()
            .AddCookie(SignInScheme)
            .AddOpenIdConnect(Scheme, options =>
            {
                options.Authority = oidc.Authority;
                options.ClientId = oidc.ClientId;
                options.ClientSecret = oidc.ClientSecret;
                options.CallbackPath = oidc.CallbackPath;
                options.SignInScheme = SignInScheme;

                options.ResponseType = "code";          // Authorization Code (REQ-8.1)
                options.UsePkce = true;                  // S256 code_challenge (REQ-8.1)
                options.SaveTokens = false;              // we never call Google APIs (REQ-8.4)
                options.GetClaimsFromUserInfoEndpoint = false;
                options.MapInboundClaims = false;        // keep raw claim names (sub/email/hd/email_verified)
                options.RequireHttpsMetadata = !environment.IsDevelopment();

                options.Scope.Clear();                   // only openid+email, no offline access (REQ-8.4)
                options.Scope.Add("openid");
                options.Scope.Add("email");

                options.TokenValidationParameters.ValidateIssuer = true;
                options.TokenValidationParameters.ValidIssuers = ["https://accounts.google.com", "accounts.google.com"];
                // aud is validated against ClientId by the handler; nonce + signature + lifetime too (REQ-9.2).

                options.Events = new OpenIdConnectEvents
                {
                    // REQ-9.2: the verified-email + hosted-domain gates the JWKS/iss/aud/nonce checks don't cover.
                    OnTokenValidated = context =>
                    {
                        var principal = context.Principal;
                        if (principal?.FindFirst("email_verified")?.Value is not "true")
                            context.Fail("email_verified-required");
                        else if (!string.IsNullOrEmpty(oidc.HostedDomain) && principal.FindFirst("hd")?.Value != oidc.HostedDomain)
                            context.Fail("hd-not-allowed");
                        return Task.CompletedTask;
                    },

                    // Canonical post-principal hook: resolve the merchant user and run the 4-way state branch, then
                    // short-circuit the framework sign-in (REQ-9.4). Identity (sub/email/hd) comes ONLY from the
                    // verified id_token (REQ-9.3).
                    OnTicketReceived = async context =>
                    {
                        var login = context.HttpContext.RequestServices.GetRequiredService<MerchantUserLoginService>();
                        var principal = context.Principal;
                        await login.HandleCallbackAsync(
                            context.HttpContext,
                            principal?.FindFirst("sub")?.Value,
                            principal?.FindFirst("email")?.Value,
                            principal?.FindFirst("hd")?.Value,
                            context.Properties?.RedirectUri,
                            context.HttpContext.RequestAborted);
                        context.HandleResponse();
                    },

                    // OAuth error=access_denied at Google (REQ-9.5).
                    OnAccessDenied = async context =>
                    {
                        var login = context.HttpContext.RequestServices.GetRequiredService<MerchantUserLoginService>();
                        await login.DenyAsync(context.HttpContext, "access-denied", null, context.HttpContext.RequestAborted);
                        context.HandleResponse();
                    },

                    // State mismatch / code-exchange fail / email_verified|hd Fail / OAuth error (REQ-9.5).
                    OnRemoteFailure = async context =>
                    {
                        var login = context.HttpContext.RequestServices.GetRequiredService<MerchantUserLoginService>();
                        await login.DenyAsync(context.HttpContext, MapFailureReason(context.Failure), null, context.HttpContext.RequestAborted);
                        context.HandleResponse();
                    },
                };
            });

        return services;
    }

    private static string MapFailureReason(Exception? failure) => failure?.Message switch
    {
        "email_verified-required" => "email-unverified",
        "hd-not-allowed" => "hd-mismatch",
        _ => "auth-failed",
    };
}
