using System.Security.Claims;
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
/// (Google: <c>email_verified</c> + <c>hd</c>; Microsoft: pinned UUID <c>tid</c> + UUID <c>oid</c> + AllowedTenants),
/// then, on the canonical post-principal hook, establish the server session and short-circuit the framework sign-in
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

    internal const string ReturnToPropertyKey = ".admin.returnTo";

    internal static AuthenticationProperties CreateLoginProperties(string returnTo)
    {
        var properties = new AuthenticationProperties { RedirectUri = returnTo };
        properties.Items[ReturnToPropertyKey] = returnTo;
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
            builder.AddOpenIdConnect(scheme, options => Configure(options, name, oidc, environment, microsoftTenant));
        }

        return services;
    }

    private static void Configure(
        OpenIdConnectOptions options, string name, OidcProviderOptions oidc, IHostEnvironment environment,
        AdminMicrosoftTenantSnapshot microsoftTenant)
    {
        var isMicrosoft = MicrosoftOidc.Is(name);
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
        options.MapInboundClaims = false;        // keep raw claim names (sub/oid/email/hd/tid/email_verified)
        options.RequireHttpsMetadata = !environment.IsDevelopment();

        options.Scope.Clear();                   // default is {openid, profile}; keep the request minimal
        options.Scope.Add("openid");
        options.Scope.Add("email");
        if (isMicrosoft)
            options.Scope.Add("profile");        // Entra puts oid/tid behind profile; openid alone yields only the pairwise sub

        options.TokenValidationParameters.ValidateIssuer = true;
        // The library default skew (5 min) is generous for short-lived id_tokens; servers run NTP — 2 min covers real drift.
        options.TokenValidationParameters.ClockSkew = TimeSpan.FromMinutes(2);
        // Microsoft issuer validation is the FRAMEWORK DEFAULT: iss is compared to the tenant-pinned Authority's
        // discovery metadata issuer (multi-tenant Authorities are rejected at boot) — no custom IssuerValidator.
        if (!isMicrosoft)
            options.TokenValidationParameters.ValidIssuers = ["https://accounts.google.com", "accounts.google.com"];
        // aud is validated against ClientId by the handler; nonce + signature + lifetime too.

        options.Events = new OpenIdConnectEvents
        {
            // The provider-specific gates the JWKS/iss/aud/nonce checks don't cover. Google: verified-email +
            // hosted-domain. Admin Microsoft additionally requires canonical UUID tid/oid and the Authority tenant;
            // AllowedTenants remains an optional extra restriction. Entra emits no email_verified.
            OnTokenValidated = context =>
            {
                var principal = context.Principal;
                if (isMicrosoft)
                {
                    if (ValidateMicrosoftIdentity(principal, microsoftTenant.TenantId, oidc.AllowedTenants) is { } reason)
                        context.Fail(reason);
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
                    providerSlug,
                    isMicrosoft ? CanonicalMicrosoftSubject(principal) : principal?.FindFirst("sub")?.Value,
                    isMicrosoft ? MicrosoftOidc.Email(principal) : principal?.FindFirst("email")?.Value,
                    // Google passed the email_verified gate above; Entra email/preferred_username are unverified,
                    // mutable claims -> display-only, never invite-binding (see CallbackResolver).
                    emailVerified: !isMicrosoft,
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
                await login.DenyAsync(context.HttpContext, MapFailureReason(context.Failure), null, context.HttpContext.RequestAborted);
                context.HandleResponse();
            },
        };
    }

    private static string? ValidateMicrosoftIdentity(
        ClaimsPrincipal? principal, Guid? configuredTenant, string[] allowedTenants)
    {
        if (!TryCanonicalizeUuidClaim(principal, "tid", out var tid))
            return "tid-required";
        if (configuredTenant is null || tid != configuredTenant.Value)
            return "tenant-not-allowed";
        if (!TryCanonicalizeUuidClaim(principal, "oid", out _))
            return "oid-required";
        return MicrosoftOidc.TenantGate(principal, allowedTenants);
    }

    private static bool TryCanonicalizeUuidClaim(
        ClaimsPrincipal? principal, string type, out Guid value)
    {
        value = Guid.Empty;
        var claims = principal?.FindAll(type).ToArray();
        if (claims is not { Length: 1 }
            || !Guid.TryParse(claims[0].Value, out value)
            || value == Guid.Empty
            || claims[0].Subject is not { } identity)
        {
            return false;
        }

        var canonical = value.ToString("D").ToLowerInvariant();
        if (claims[0].Value == canonical)
            return true;

        var claim = claims[0];
        if (!identity.TryRemoveClaim(claim))
            return false;
        identity.AddClaim(new Claim(
            claim.Type, canonical, claim.ValueType, claim.Issuer, claim.OriginalIssuer, identity));
        return true;
    }

    private static string? CanonicalMicrosoftSubject(ClaimsPrincipal? principal) =>
        Guid.TryParse(MicrosoftOidc.Subject(principal), out var oid) && oid != Guid.Empty
            ? oid.ToString("D").ToLowerInvariant()
            : null;

    private static string MapFailureReason(Exception? failure) => failure?.Message switch
    {
        "email_verified-required" => "email-unverified",
        "hd-not-allowed" => "hd-mismatch",
        "tid-required" => "tenant-missing",
        "tenant-not-allowed" => "tenant-not-allowed",
        _ => "auth-failed",
    };
}
