using Merchants.Domain.Users;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;

namespace Api.Merchants;

/// <summary>Provider slug ("microsoft") → registered scheme name for the CONFIGURED merchant-user provider.
/// Registered in DI even when empty so the login endpoint can 404 an unknown/disabled provider
/// instead of throwing at Challenge time.</summary>
internal sealed class UserOidcProviders : Dictionary<string, string>;

/// <summary>
/// The confidential Microsoft OIDC client for the merchant-user BFF login (REQ-8/9/14), registered from
/// MerchantAuth:Providers as the scheme "MerchantUserMicrosoft". The framework handler does the
/// Authorization Code + PKCE + state + nonce + code-exchange + JWKS id_token validation; we only hook
/// <c>OnTokenValidated</c> (provider-specific gates, REQ-9.2), <c>OnTicketReceived</c> (the 4-way lifecycle branch via
/// <see cref="UserLoginService"/>, REQ-9.4), and <c>OnRemoteFailure</c>/<c>OnAccessDenied</c> (deny → error
/// page, REQ-9.5). Fully isolated from the Admin clients: distinct schemes + sign-in scheme + callback paths
/// (REQ-14.4). Schemes are ADDED here without changing the default (the merchant-user session scheme is the
/// default — the Bearer path is retired, REQ-6).
/// </summary>
// ponytail: DUPLICATE-shaped of AdminOidcAuthentication (distinct scheme/callback + 4-way hook) — deliberate.
internal static class UserOidcAuthentication
{
    /// <summary>Scheme name prefix: provider "Microsoft" registers as scheme "MerchantUserMicrosoft" — distinct
    /// from Admin's "AdminMicrosoft" (REQ-14.4). The distinct names also isolate the framework's correlation/nonce
    /// Data Protection purposes from the Admin clients automatically.</summary>
    public const string SchemePrefix = "MerchantUser";

    /// <summary>A throwaway cookie sign-in scheme (distinct from Admin's <c>oidc-noop</c>, REQ-14.4; shared by every
    /// merchant-user provider): the OIDC handler needs a resolvable SignInScheme, but we call HandleResponse before
    /// sign-in, so it is never actually written.</summary>
    public const string SignInScheme = "merchant-user-oidc-noop";

    public static IServiceCollection AddMerchantUserOidcAuthentication(
        this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var auth = configuration.GetSection(UserOidcOptions.SectionName).Get<UserOidcOptions>() ?? new UserOidcOptions();

        services.AddScoped<IUserCallbackResolver, UserCallbackResolver>();
        services.AddScoped<UserLoginService>();

        var providers = new UserOidcProviders();
        services.AddSingleton(providers);

        AuthenticationBuilder? builder = null;
        foreach (var (name, oidc) in auth.Providers)
        {
            // Merchant-user authentication is deliberately Microsoft-only (mirrors Admin). Any other configured
            // provider is a deployment error the boot guard rejects outside Development; skip its scheme here.
            if (!MicrosoftOidc.Is(name))
                continue;

            // Blank ClientId -> skip the scheme (REQ-14.2). The OIDC scheme is a per-request handler that AuthN
            // middleware VALIDATES on every request, and OpenIdConnectOptions.Validate() requires a non-empty
            // ClientId — a blank one would throw on every request and take the WHOLE API down. A provider may be
            // intentionally unconfigured; skip its scheme so the rest of the API stays up — a login attempt for it
            // then 404s at the login endpoint. Mirrors Admin.
            if (string.IsNullOrWhiteSpace(oidc.ClientId))
                continue;

            // Parameterless AddAuthentication() keeps the existing default scheme intact.
            builder ??= services.AddAuthentication().AddCookie(SignInScheme);

            var scheme = SchemePrefix + name;
            providers[name.ToLowerInvariant()] = scheme;
            builder.AddOpenIdConnect(scheme, options => Configure(options, oidc, environment));
        }

        return services;
    }

    private static void Configure(
        OpenIdConnectOptions options, OidcProviderOptions oidc, IHostEnvironment environment)
    {
        options.Authority = oidc.Authority;
        options.ClientId = oidc.ClientId;
        options.ClientSecret = oidc.ClientSecret;
        options.CallbackPath = oidc.CallbackPath;
        options.SignInScheme = SignInScheme;

        options.ResponseType = "code";          // Authorization Code (REQ-8.1)
        options.UsePkce = true;                  // S256 code_challenge (REQ-8.1)
        options.SaveTokens = false;              // we never call the provider's APIs (REQ-8.4)
        options.GetClaimsFromUserInfoEndpoint = false;
        options.MapInboundClaims = false;        // keep raw claim names (sub/oid/email/hd/tid/email_verified)
        options.RequireHttpsMetadata = !environment.IsDevelopment();

        options.Scope.Clear();                   // minimal request, no offline access (REQ-8.4)
        options.Scope.Add("openid");
        options.Scope.Add("email");
        options.Scope.Add("profile");            // Entra puts oid/tid behind profile; openid alone yields only the pairwise sub

        options.TokenValidationParameters.ValidateIssuer = true;
        // The library default skew (5 min) is generous for short-lived id_tokens; servers run NTP — 2 min covers real drift.
        options.TokenValidationParameters.ClockSkew = TimeSpan.FromMinutes(2);
        // Microsoft issuer validation is the FRAMEWORK DEFAULT: iss is compared to the tenant-pinned Authority's
        // discovery metadata issuer (multi-tenant Authorities are rejected at boot) — no custom IssuerValidator.
        // aud is validated against ClientId by the handler; nonce + signature + lifetime too (REQ-9.2).

        options.Events = new OpenIdConnectEvents
        {
            // REQ-9.2: the gate the JWKS/iss/aud/nonce checks don't cover — the OPTIONAL AllowedTenants tid
            // allowlist, active only when non-empty (tenant isolation already comes from issuer == metadata issuer).
            OnTokenValidated = context =>
            {
                if (MicrosoftOidc.TenantGate(context.Principal, oidc.AllowedTenants) is { } reason)
                    context.Fail(reason);
                return Task.CompletedTask;
            },

            // Canonical post-principal hook: resolve the merchant user and run the 4-way state branch, then
            // short-circuit the framework sign-in (REQ-9.4). Identity comes ONLY from the verified id_token
            // (REQ-9.3): subject = Entra oid; the provider slug rides along so a registration ticket records
            // the provider.
            OnTicketReceived = async context =>
            {
                var login = context.HttpContext.RequestServices.GetRequiredService<UserLoginService>();
                var principal = context.Principal;
                await login.HandleCallbackAsync(
                    context.HttpContext,
                    MicrosoftOidc.Subject(principal),
                    MicrosoftOidc.Email(principal),
                    hostedDomain: null,
                    ExternalLogin.Microsoft,
                    context.Properties?.RedirectUri,
                    context.HttpContext.RequestAborted,
                    context.Properties?.Items.TryGetValue("merchant_invitation_id", out var invitationId) == true
                        && Guid.TryParse(invitationId, out var parsedInvitationId) ? parsedInvitationId : null);
                context.HandleResponse();
            },

            // OAuth error=access_denied at the provider (REQ-9.5).
            OnAccessDenied = async context =>
            {
                var login = context.HttpContext.RequestServices.GetRequiredService<UserLoginService>();
                await login.DenyAsync(context.HttpContext, "access-denied", null, context.HttpContext.RequestAborted);
                context.HandleResponse();
            },

            // State mismatch / code-exchange fail / gate Fail / OAuth error (REQ-9.5).
            OnRemoteFailure = async context =>
            {
                var login = context.HttpContext.RequestServices.GetRequiredService<UserLoginService>();
                await login.DenyAsync(context.HttpContext, MapFailureReason(context.Failure), null, context.HttpContext.RequestAborted);
                context.HandleResponse();
            },
        };
    }

    private static string MapFailureReason(Exception? failure) => failure?.Message switch
    {
        "tid-required" => "tenant-missing",
        "tenant-not-allowed" => "tenant-not-allowed",
        _ => "auth-failed",
    };
}
