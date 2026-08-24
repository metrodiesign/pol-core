using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;

namespace Api.Admins;

/// <summary>Provider slug ("microsoft") → registered scheme name for the configured Admin provider.
/// Registered in DI even when empty so the login endpoint can 404 an unknown/disabled provider instead of
/// throwing at Challenge time.</summary>
internal sealed class AdminOidcProviders : Dictionary<string, string>;

/// <summary>
/// The confidential Microsoft OIDC client for the Admin BFF login. The framework handler does Authorization Code +
/// PKCE + state + nonce + code-exchange + JWKS id_token validation; the Admin callback adds the fixed workforce
/// policy gate, then establishes the server session and short-circuits framework sign-in (<see cref="LoginService"/>).
/// The scheme is ADDED without changing the default.
/// </summary>
internal static class OidcAuthentication
{
    /// <summary>Scheme name prefix: Microsoft registers as scheme "AdminMicrosoft". The distinct scheme name
    /// isolates the framework's correlation/nonce Data Protection purpose.</summary>
    public const string SchemePrefix = "Admin";

    /// <summary>A throwaway cookie sign-in scheme (shared by every admin provider — it is never actually written):
    /// the OIDC handler needs a resolvable SignInScheme, but we call HandleResponse before sign-in.</summary>
    public const string SignInScheme = "oidc-noop";

    internal const string ReturnToPropertyKey = ".admin.returnTo";

    internal static AuthenticationProperties CreateLoginProperties(string returnTo, string? provider = null)
    {
        var properties = new AuthenticationProperties { RedirectUri = returnTo };
        properties.Items[ReturnToPropertyKey] = returnTo;
        if (MicrosoftOidc.Is(provider ?? string.Empty))
            properties.Parameters["prompt"] = "select_account";
        return properties;
    }

    internal static string? GetReturnTo(AuthenticationProperties? properties) =>
        properties?.Items.TryGetValue(ReturnToPropertyKey, out var returnTo) == true
        && !string.IsNullOrEmpty(returnTo)
            ? returnTo
            : properties?.RedirectUri;

    public static IServiceCollection AddAdminOidcAuthentication(
        this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var auth = configuration.GetSection(AdminAuthOptions.SectionName).Get<AdminAuthOptions>() ?? new AdminAuthOptions();
        var microsoftTenant = AdminMicrosoftTenantSnapshot.Resolve(auth);
        services.AddSingleton(microsoftTenant);

        services.AddScoped<ICallbackResolver, CallbackResolver>();
        services.AddScoped<LoginService>();

        var providers = new AdminOidcProviders();
        services.AddSingleton(providers);

        AuthenticationBuilder? builder = null;
        foreach (var (name, oidc) in auth.Providers)
        {
            // Admin authentication is deliberately Microsoft-only. MerchantAuth keeps its own provider map.
            if (!MicrosoftOidc.Is(name))
                continue;

            // The OIDC scheme is a per-request handler: AuthenticationMiddleware initializes — and VALIDATES — it on
            // EVERY request to detect the callback, and OpenIdConnectOptions.Validate() requires a non-empty ClientId.
            // A blank ClientId would therefore throw on every request and take the WHOLE API down (health, webhooks,
            // merchant routes), not just admin login. The Production boot guard already requires the Microsoft
            // provider; a blank one (tests, an unconfigured dev box) just skips its scheme so the rest of
            // the API stays up — a login attempt for it then 404s at the login endpoint.
            if (string.IsNullOrWhiteSpace(oidc.ClientId))
                continue;

            // Parameterless AddAuthentication() does NOT set a default scheme, so the explicit default Program.cs
            // established (MerchantUserSessionAuthenticationHandler.SchemeName) is preserved.
            builder ??= services.AddAuthentication().AddCookie(SignInScheme);

            var scheme = SchemePrefix + name;
            providers[name.ToLowerInvariant()] = scheme;
            builder.AddOpenIdConnect(scheme, options => Configure(options, name, oidc, environment, microsoftTenant));
        }

        return services;
    }

    private static void Configure(
        OpenIdConnectOptions options, string name, OidcProviderOptions oidc, IHostEnvironment environment,
        AdminMicrosoftTenantSnapshot microsoftTenant)
    {
        var providerSlug = name.ToLowerInvariant();

        options.Authority = oidc.Authority;
        options.ClientId = oidc.ClientId;
        options.ClientSecret = oidc.ClientSecret;
        options.CallbackPath = oidc.CallbackPath;
        options.SignInScheme = SignInScheme;

        options.ResponseType = "code";          // Authorization Code (REQ-1.1)
        options.UsePkce = true;                  // S256 code_challenge (REQ-1.1)
        options.SaveTokens = false;              // we never call the provider's APIs (REQ-1.5)
        options.GetClaimsFromUserInfoEndpoint = false;
        options.MapInboundClaims = false;        // keep raw claim names
        options.RequireHttpsMetadata = !environment.IsDevelopment();

        options.Scope.Clear();                   // default is {openid, profile}; keep the request minimal
        options.Scope.Add("openid");
        options.Scope.Add("email");
        options.Scope.Add("profile");            // required for tenant and preferred username claims

        options.TokenValidationParameters.ValidateIssuer = true;
        // The library default skew (5 min) is generous for short-lived id_tokens; servers run NTP — 2 min covers real drift.
        options.TokenValidationParameters.ClockSkew = TimeSpan.FromMinutes(2);
        // Microsoft issuer validation is the FRAMEWORK DEFAULT: iss is compared to the tenant-pinned Authority's
        // discovery metadata issuer (multi-tenant Authorities are rejected at boot) — no custom IssuerValidator.
        // aud is validated against ClientId by the handler; nonce + signature + lifetime too.

        options.Events = new OpenIdConnectEvents
        {
            // The workforce gate runs after signature/issuer/audience/nonce/lifetime validation. It stores one typed
            // result in request state so the ticket hook never reparses mutable claims.
            OnTokenValidated = context =>
            {
                if (!MicrosoftWorkforceClaimsValidator.TryValidate(
                        context.Principal, microsoftTenant.TenantId, out var claims))
                {
                    MicrosoftOidcFailureClassifier.MarkPolicyFailure(context.HttpContext);
                    context.Fail(new MicrosoftWorkforcePolicyException());
                }
                else
                    context.HttpContext.Items[MicrosoftWorkforceClaimsValidator.ContextItemKey] = claims;
                return Task.CompletedTask;
            },

            // Canonical post-principal hook: resolve the admin, establish the server session + cookies,
            // and short-circuit framework sign-in (REQ-2.5/3.1).
            OnTicketReceived = async context =>
            {
                var login = context.HttpContext.RequestServices.GetRequiredService<LoginService>();
                if (context.HttpContext.Items[MicrosoftWorkforceClaimsValidator.ContextItemKey]
                    is not MicrosoftWorkforceClaims claims)
                {
                    await login.DenyAsync(
                        context.HttpContext, "auth-failed", null, context.HttpContext.RequestAborted);
                    context.HandleResponse();
                    return;
                }

                await login.EstablishSessionAsync(
                    context.HttpContext,
                    providerSlug,
                    claims.Identity.Subject,
                    GetReturnTo(context.Properties),
                    context.HttpContext.RequestAborted);
                context.HandleResponse();
            },

            // OAuth error=access_denied at the provider (REQ-2.8).
            OnAccessDenied = async context =>
            {
                var login = context.HttpContext.RequestServices.GetRequiredService<LoginService>();
                await login.DenyAsync(context.HttpContext, "access-denied", null, context.HttpContext.RequestAborted);
                context.HandleResponse();
            },

            // State mismatch / code-exchange fail / gate Fail / OAuth error (REQ-2.1/2.7/2.8/12.4).
            OnRemoteFailure = async context =>
            {
                var login = context.HttpContext.RequestServices.GetRequiredService<LoginService>();
                await login.DenyAsync(
                    context.HttpContext,
                    MicrosoftOidcFailureClassifier.BrowserReason(context.HttpContext, context.Failure),
                    null,
                    context.HttpContext.RequestAborted);
                context.HandleResponse();
            },
        };
    }
}
