using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;

namespace Api.Admins;

/// <summary>Provider slug ("google"/"microsoft") → registered scheme name for the CONFIGURED admin providers.
/// Registered in DI even when empty so the login endpoint can 404 an unknown/disabled provider instead of
/// throwing at Challenge time.</summary>
internal sealed class AdminOidcProviders : Dictionary<string, string>;

/// <summary>
/// The confidential OIDC clients for the admin BFF login (REQ-1/2), one scheme per configured provider
/// (AdminAuth:Providers): "AdminGoogle", "AdminMicrosoft". The framework handler does the Authorization Code +
/// PKCE + state + nonce + code-exchange + JWKS id_token validation; we only add the provider-specific gates
/// (Google: <c>email_verified</c> + <c>hd</c>; Microsoft: tid-consistent issuer + AllowedTenants) and, on the
/// canonical post-principal hook, establish the server session ourselves and short-circuit the framework sign-in
/// (<see cref="LoginService"/>). Schemes are ADDED here without changing the default.
/// </summary>
internal static class OidcAuthentication
{
    /// <summary>Scheme name prefix: provider "Google" registers as scheme "AdminGoogle" (REQ-1.1). The distinct
    /// per-provider names also isolate the framework's correlation/nonce Data Protection purposes automatically.</summary>
    public const string SchemePrefix = "Admin";

    /// <summary>A throwaway cookie sign-in scheme (shared by every admin provider — it is never actually written):
    /// the OIDC handler needs a resolvable SignInScheme, but we call HandleResponse before sign-in.</summary>
    public const string SignInScheme = "oidc-noop";

    public static IServiceCollection AddAdminOidcAuthentication(
        this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var auth = configuration.GetSection(AdminAuthOptions.SectionName).Get<AdminAuthOptions>() ?? new AdminAuthOptions();

        services.AddScoped<ICallbackResolver, CallbackResolver>();
        services.AddScoped<LoginService>();

        var providers = new AdminOidcProviders();
        services.AddSingleton(providers);

        AuthenticationBuilder? builder = null;
        foreach (var (name, oidc) in auth.Providers)
        {
            // The OIDC scheme is a per-request handler: AuthenticationMiddleware initializes — and VALIDATES — it on
            // EVERY request to detect the callback, and OpenIdConnectOptions.Validate() requires a non-empty ClientId.
            // A blank ClientId would therefore throw on every request and take the WHOLE API down (health, webhooks,
            // merchant routes), not just admin login. Outside Development the boot guard already requires at least one
            // configured provider; a blank one (tests, an unconfigured dev box) just skips its scheme so the rest of
            // the API stays up — a login attempt for it then 404s at the login endpoint.
            if (string.IsNullOrWhiteSpace(oidc.ClientId))
                continue;

            // Parameterless AddAuthentication() does NOT set a default scheme, so the explicit default Program.cs
            // established (MerchantUserSessionAuthenticationHandler.SchemeName) is preserved.
            builder ??= services.AddAuthentication().AddCookie(SignInScheme);

            var scheme = SchemePrefix + name;
            providers[name.ToLowerInvariant()] = scheme;
            builder.AddOpenIdConnect(scheme, options => Configure(options, name, oidc, environment));
        }

        return services;
    }

    private static void Configure(
        OpenIdConnectOptions options, string name, OidcProviderOptions oidc, IHostEnvironment environment)
    {
        var isMicrosoft = MicrosoftOidc.Is(name);

        options.Authority = oidc.Authority;
        options.ClientId = oidc.ClientId;
        options.ClientSecret = oidc.ClientSecret;
        options.CallbackPath = oidc.CallbackPath;
        options.SignInScheme = SignInScheme;

        options.ResponseType = "code";          // Authorization Code (REQ-1.1)
        options.UsePkce = true;                  // S256 code_challenge (REQ-1.1)
        options.SaveTokens = false;              // we never call the provider's APIs (REQ-1.5)
        options.GetClaimsFromUserInfoEndpoint = false;
        options.MapInboundClaims = false;        // keep raw claim names (sub/oid/email/hd/tid/email_verified)
        options.RequireHttpsMetadata = !environment.IsDevelopment();

        options.Scope.Clear();                   // default is {openid, profile}; keep the request minimal
        options.Scope.Add("openid");
        options.Scope.Add("email");
        if (isMicrosoft)
            options.Scope.Add("profile");        // Entra puts oid/tid behind profile; openid alone yields only the pairwise sub

        options.TokenValidationParameters.ValidateIssuer = true;
        if (isMicrosoft)
            // The Entra v2 issuer is per-tenant (a template in multi-tenant metadata) — validate it against the
            // token's own tid claim + the AllowedTenants gate (MicrosoftOidc).
            options.TokenValidationParameters.IssuerValidator = (issuer, token, _) =>
                MicrosoftOidc.ValidateIssuer(issuer, token, oidc.AllowedTenants);
        else
            options.TokenValidationParameters.ValidIssuers = ["https://accounts.google.com", "accounts.google.com"];
        // aud is validated against ClientId by the handler; nonce + signature + lifetime too.

        options.Events = new OpenIdConnectEvents
        {
            // REQ-2.3: the provider-specific gates the JWKS/iss/aud/nonce checks don't cover. Google: verified-email +
            // hosted-domain. Microsoft: require tid (Entra emits NO email_verified — that gate would reject every
            // Entra login); the issuer validator above already checked tid consistency + AllowedTenants.
            OnTokenValidated = context =>
            {
                var principal = context.Principal;
                if (isMicrosoft)
                {
                    if (principal?.FindFirst("tid")?.Value is null or "")
                        context.Fail("tid-required");
                }
                else if (principal?.FindFirst("email_verified")?.Value is not "true")
                    context.Fail("email_verified-required");
                else if (!string.IsNullOrEmpty(oidc.HostedDomain) && principal.FindFirst("hd")?.Value != oidc.HostedDomain)
                    context.Fail("hd-not-allowed");
                return Task.CompletedTask;
            },

            // Canonical post-principal hook: resolve the admin, establish the server session + cookies,
            // and short-circuit the framework sign-in (REQ-2.5/3.1). Subject = Google sub / Entra oid.
            OnTicketReceived = async context =>
            {
                var login = context.HttpContext.RequestServices.GetRequiredService<LoginService>();
                var principal = context.Principal;
                await login.EstablishSessionAsync(
                    context.HttpContext,
                    isMicrosoft ? MicrosoftOidc.Subject(principal) : principal?.FindFirst("sub")?.Value,
                    isMicrosoft ? MicrosoftOidc.Email(principal) : principal?.FindFirst("email")?.Value,
                    // Google passed the email_verified gate above; Entra email/preferred_username are unverified,
                    // mutable claims -> display-only, never invite-binding (see CallbackResolver).
                    emailVerified: !isMicrosoft,
                    context.Properties?.RedirectUri,
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
                await login.DenyAsync(context.HttpContext, MapFailureReason(context.Failure), null, context.HttpContext.RequestAborted);
                context.HandleResponse();
            },
        };
    }

    private static string MapFailureReason(Exception? failure) => failure?.Message switch
    {
        "email_verified-required" => "email-unverified",
        "hd-not-allowed" => "hd-mismatch",
        "tid-required" => "tenant-missing",
        _ => "auth-failed",
    };
}
