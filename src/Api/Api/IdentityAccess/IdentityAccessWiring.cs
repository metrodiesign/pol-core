using System.Security.Claims;
using Accounts.Application;
using Accounts.Domain;
using Api.Iam;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenIddict.Validation.AspNetCore;

namespace Api.IdentityAccess;

internal static class IdentityAccessWiring
{
    /// <summary>Short-lived cookie that carries the verified employee account from the Entra callback back to
    /// <c>/oauth/authorize</c>, where OpenIddict turns it into an authorization code. It is signed out as soon as
    /// the code is issued and is never accepted by any API route.</summary>
    public const string LoginCookieScheme = "identity-login";
    public const string LoginCookieName = "pol_login";
    public const string AgentScheme = "IdentityAgentMicrosoft";
    public const string AgentAccountSelectionItem = "identity.agent_account_selection";

    public static IServiceCollection AddIdentityAccess(
        this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var agentProvider = configuration.GetSection("IdentityAccess:Agent").Get<IdentityOidcProviderOptions>()
            ?? new IdentityOidcProviderOptions();
        var agentProviderConfigured = !string.IsNullOrWhiteSpace(agentProvider.ClientId)
            && !string.IsNullOrWhiteSpace(agentProvider.Authority);
        services.AddOptions<IdentityAccessOptions>()
            .Bind(configuration.GetSection(IdentityAccessOptions.SectionName))
            .Validate(options =>
            {
                try
                {
                    options.Validate(agentProviderConfigured);
                    return true;
                }
                catch
                {
                    return false;
                }
            }, "IdentityAccess options are invalid.")
            .ValidateOnStart();

        services.AddScoped<IAuthorizationHandler, IdentityAccessAuthorizationHandler>();
        services.AddScoped<IdentityBffLoginService>();
        services.AddHostedService<WorkforceClientRegistration>();
        services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, PlatformTokenAuthenticationHandler>(
                PlatformTokenAuthenticationHandler.SchemeName, _ => { })
            .AddCookie(LoginCookieScheme, options =>
            {
                options.Cookie.Name = LoginCookieName;
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                options.Cookie.IsEssential = true;
                options.ExpireTimeSpan = TimeSpan.FromMinutes(2);
                options.SlidingExpiration = false;
                options.Events.OnRedirectToLogin = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                };
                options.Events.OnRedirectToAccessDenied = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                };
            });

        var authentication = services.AddAuthentication();
        var providers = new IdentityAccessProviders();
        AddHumanProvider(
            authentication,
            providers,
            configuration.GetSection("IdentityAccess:Workforce").Get<IdentityOidcProviderOptions>()
                ?? new IdentityOidcProviderOptions(),
            scheme: "IdentityWorkforceMicrosoft",
            callbackPath: "/api/v1/auth/employees/callback",
            kind: IdentityLoginKind.Employee,
            services,
            environment);
        AddHumanProvider(
            authentication,
            providers,
            agentProvider,
            scheme: AgentScheme,
            // The merchant Entra app only has the legacy merchant redirect URI registered, so the agent scheme shares
            // it with the legacy merchant-user scheme (same pattern as the workforce scheme on the admin callback).
            callbackPath: AgentCallbackPath,
            kind: IdentityLoginKind.Agent,
            services,
            environment);
        services.AddSingleton(providers);

        services.AddAuthorizationBuilder()
            .AddPolicy("identity-platform", policy => policy
                .AddAuthenticationSchemes(PlatformTokenAuthenticationHandler.SchemeName)
                .RequireAuthenticatedUser()
                .AddRequirements(new IdentityAccessRequirement()));

        return services;
    }

    public static IApplicationBuilder UseIdentityAccess(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var hasBearer = context.Request.Headers.Authorization.ToString()
                .StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase);
            var hasConsoleCookie = context.Request.Cookies.ContainsKey(Api.Merchants.UserSessionCookies.SessionCookieName)
                || context.Request.Cookies.ContainsKey(Api.Merchants.UserSessionCookies.SessionCookieNameDevHttp);
            if (IdentityPermissionAuthorization.IsIdentityOrderRoute(context) && hasBearer && hasConsoleCookie)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "The request contains more than one authentication context.",
                    extensions: new Dictionary<string, object?>
                    {
                        ["code"] = "ambiguous_authentication_context",
                        ["traceId"] = context.TraceIdentifier,
                    }).ExecuteAsync(context);
                return;
            }
            await next();
        });

    /// <summary>The redirect URI registered on the merchant Entra app, relative to the API's public origin; the
    /// legacy merchant-user OIDC scheme answers on the same path.</summary>
    public const string AgentCallbackPath = "/api/v1/merchants/auth/microsoft/callback";

    private static void AddHumanProvider(
        AuthenticationBuilder authentication,
        IdentityAccessProviders providers,
        IdentityOidcProviderOptions provider,
        string scheme,
        string callbackPath,
        IdentityLoginKind kind,
        IServiceCollection services,
        IHostEnvironment environment)
    {
        if (string.IsNullOrWhiteSpace(provider.ClientId) || string.IsNullOrWhiteSpace(provider.Authority))
            return;

        provider.CallbackPath = string.IsNullOrWhiteSpace(provider.CallbackPath)
            ? callbackPath
            : provider.CallbackPath;
        providers[kind == IdentityLoginKind.Employee ? "employees" : "agents"] = scheme;
        authentication.AddOpenIdConnect(scheme, options =>
        {
            options.Authority = provider.Authority;
            options.ClientId = provider.ClientId;
            options.ClientSecret = provider.ClientSecret;
            options.CallbackPath = provider.CallbackPath;
            // The employee handler is the only one on its CallbackPath, so a callback whose state cannot be
            // unprotected is a failed login: OnRemoteFailure sends the browser to the SPA error page instead of a
            // bare 404. The agent handler shares its CallbackPath with the legacy merchant-user scheme registered
            // after it; state is data-protected per scheme, so a callback it cannot unprotect belongs to the legacy
            // scheme and is passed through instead of failed.
            options.SkipUnrecognizedRequests = kind == IdentityLoginKind.Agent;
            options.SignInScheme = LoginCookieScheme;
            options.ResponseType = "code";
            options.UsePkce = true;
            options.SaveTokens = true;
            options.GetClaimsFromUserInfoEndpoint = false;
            options.MapInboundClaims = false;
            options.RequireHttpsMetadata = !environment.IsDevelopment();
            options.Scope.Clear();
            foreach (var scope in (provider.Scopes ?? []).Where(value => !string.IsNullOrWhiteSpace(value)))
                options.Scope.Add(scope);
            options.TokenValidationParameters.ValidateIssuer = true;
            options.Events = new OpenIdConnectEvents
            {
                OnTicketReceived = async context =>
                {
                    var login = context.HttpContext.RequestServices
                        .GetRequiredService<IdentityBffLoginService>();
                    try
                    {
                        await login.CompleteAsync(context, kind);
                    }
                    catch (IdentityAccessException failure)
                    {
                        // Policy/JIT denial (tenant, issuer, audience, eligibility): a browser outcome, not a 500.
                        context.HttpContext.RequestServices.GetRequiredService<ILogger<IdentityBffLoginService>>()
                            .LogWarning("{Kind} login denied at callback: {Code}. TraceId {TraceId}.",
                                kind, failure.Code, context.HttpContext.TraceIdentifier);
                        DenyToWebApp(context.HttpContext, kind, failure.Code.Replace('_', '-'));
                        context.HandleResponse();
                    }
                },
                OnAccessDenied = context =>
                {
                    DenyToWebApp(context.HttpContext, kind, "access-denied");
                    context.HandleResponse();
                    return Task.CompletedTask;
                },
                OnRemoteFailure = context =>
                {
                    DenyToWebApp(context.HttpContext, kind, "auth-failed");
                    context.HandleResponse();
                    return Task.CompletedTask;
                },
            };
        });
    }

    /// <summary>The callback lands on the API origin, so a failed login is sent back to the SPA error page with a
    /// non-sensitive reason (same contract as the legacy admin flow: <c>/login-error?reason=...</c>).</summary>
    private static void DenyToWebApp(HttpContext http, IdentityLoginKind kind, string reason)
    {
        if (http.Response.HasStarted)
            return;
        var settings = http.RequestServices.GetRequiredService<IOptions<IdentityAccessOptions>>().Value;
        var target = IdentityBffLoginService.ToWebApp(
            "/login-error",
            kind == IdentityLoginKind.Employee ? settings.WorkforceWebAppBaseUrl : settings.AgentWebAppBaseUrl);
        http.Response.Redirect(Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(target, "reason", reason));
    }
}

internal enum IdentityLoginKind
{
    Employee,
    Agent,
}

internal sealed class IdentityBffLoginService(
    EmployeeJitService employeeJit,
    RegistrationSessionService registrationSessions,
    IIdentityAccessQuery identities,
    IOptions<IdentityAccessOptions> options)
{
    public async Task CompleteAsync(TicketReceivedContext context, IdentityLoginKind kind)
    {
        var principal = context.Principal
            ?? throw new InvalidOperationException("OIDC callback did not contain a principal.");
        var properties = context.Properties ?? new AuthenticationProperties();
        var accountSelectionRequested = properties.Items.TryGetValue(
            IdentityAccessWiring.AgentAccountSelectionItem, out var accountSelectionStatus)
            && string.Equals(accountSelectionStatus, "requested", StringComparison.Ordinal);
        var verified = FromPrincipal(principal, properties.GetTokenValue("id_token"), workforceEligible: kind == IdentityLoginKind.Employee);
        var settings = options.Value;
        if (kind == IdentityLoginKind.Employee)
        {
            var result = await employeeJit.ResolveAsync(
                verified,
                settings.WorkforceIssuer,
                settings.WorkforceTenantId,
                settings.WorkforceAudience,
                context.HttpContext.RequestAborted);
            var account = await identities.FindAccountAsync(result.AccountId, context.HttpContext.RequestAborted)
                ?? throw new InvalidOperationException("JIT account was not persisted.");
            // Hand the verified account to /oauth/authorize through the short-lived login cookie: the OIDC handler
            // signs this principal into the cookie scheme and redirects to ReturnUri (the pending authorize
            // request). Entra's tokens are dropped here; the platform issues its own JWT + refresh token.
            var identity = new ClaimsIdentity(IdentityAccessWiring.LoginCookieScheme);
            identity.AddClaim(new Claim("sub", account.Id.ToString("D")));
            context.Principal = new ClaimsPrincipal(identity);
            context.Properties = new AuthenticationProperties
            {
                IsPersistent = false,
                ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(2),
            };
            if (!IsAuthorizeRequest(context.ReturnUri))
                context.ReturnUri = "/";
            return;
        }

        // API-009: an already-approved agent logs in (login cookie -> the pending /oauth/authorize request issues
        // the code for the agent client); everyone else (no account, Pending, Rejected) gets a registration
        // session and lands on /register, where nextAction tells the SPA what to show. Suspended is a deny.
        var approved = await registrationSessions.FindApprovedAccountAsync(
            verified, settings.AgentIssuer, settings.AgentTenantId, settings.AgentAudience,
            context.HttpContext.RequestAborted);
        if (approved is not null)
        {
            if (approved.Status != AccountStatus.Active)
                throw new IdentityAccessException("agent_account_suspended", "The agent account is not active.");
            var identity = new ClaimsIdentity(IdentityAccessWiring.LoginCookieScheme);
            identity.AddClaim(new Claim("sub", approved.Id.ToString("D")));
            context.Principal = new ClaimsPrincipal(identity);
            context.Properties = new AuthenticationProperties
            {
                IsPersistent = false,
                ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(2),
            };
            if (accountSelectionRequested)
                context.Properties.Items[IdentityAccessWiring.AgentAccountSelectionItem] = "completed";
            if (!IsAuthorizeRequest(context.ReturnUri))
                context.ReturnUri = ToWebApp("/", settings.AgentWebAppBaseUrl);
            return;
        }

        var session = await registrationSessions.StartAsync(
            verified,
            ParseMerchant(properties.GetString("identity.merchant_id")),
            DateTime.UtcNow,
            TimeSpan.FromMinutes(settings.RegistrationSessionMinutes),
            settings.AgentIssuer,
            settings.AgentTenantId,
            settings.AgentAudience,
            context.HttpContext.RequestAborted);
        context.HttpContext.Response.Cookies.Append(
            "pol_registration_session",
            session.RawReference,
            new CookieOptions { HttpOnly = true, Secure = context.HttpContext.Request.IsHttps, Path = "/" });
        context.HttpContext.Response.Redirect(ToWebApp("/register", settings.AgentWebAppBaseUrl));
        context.HandleResponse();
    }

    /// <summary>The only place an employee callback may land is the pending same-origin authorize request.</summary>
    internal static bool IsAuthorizeRequest(string? returnUri) =>
        !string.IsNullOrEmpty(returnUri)
        && (returnUri.StartsWith("/oauth/authorize?", StringComparison.Ordinal)
            || returnUri.Equals("/oauth/authorize", StringComparison.Ordinal));

    /// <summary>The callback lands on the API origin: a same-origin path (already normalized at login) is made
    /// absolute against the SPA origin when one is configured, otherwise it stays relative. Anything that is not
    /// a same-origin path collapses to "/". Mirrors admin LoginService.ToSpa.</summary>
    internal static string ToWebApp(string? path, string webAppBaseUrl)
    {
        // "//host" and "/\host" are both read as protocol-relative (off-origin) by browsers.
        var target = string.IsNullOrWhiteSpace(path)
            || !path.StartsWith('/')
            || path.StartsWith("//", StringComparison.Ordinal)
            || path.StartsWith("/\\", StringComparison.Ordinal)
            ? "/"
            : path;
        return string.IsNullOrEmpty(webAppBaseUrl) ? target : webAppBaseUrl.TrimEnd('/') + target;
    }

    private static VerifiedHumanIdentity FromPrincipal(ClaimsPrincipal principal, string? idToken, bool workforceEligible)
    {
        var provider = "microsoft";
        var tenantId = principal.FindFirstValue("tid") ?? string.Empty;
        var externalUserId = principal.FindFirstValue("oid") ?? principal.FindFirstValue("sub") ?? string.Empty;
        // The token handler validates iss/aud but does not copy them into the ClaimsIdentity; fall back to the
        // already-validated id_token saved by SaveTokens=true.
        var token = string.IsNullOrEmpty(idToken) ? null : new Microsoft.IdentityModel.JsonWebTokens.JsonWebToken(idToken);
        var issuer = principal.FindFirstValue("iss") ?? token?.Issuer ?? string.Empty;
        var audience = principal.FindFirstValue("aud") ?? token?.Audiences.FirstOrDefault() ?? string.Empty;
        return new VerifiedHumanIdentity(
            ExternalIdentity.Create(provider, tenantId, externalUserId),
            issuer,
            audience,
            SignatureValidated: true,
            LifetimeValidated: true,
            StateValidated: true,
            NonceValidated: true,
            workforceEligible,
            principal.FindFirstValue("email") ?? principal.FindFirstValue(ClaimTypes.Email),
            principal.FindFirstValue("name") ?? principal.Identity?.Name);
    }

    private static Guid ParseMerchant(string? value) =>
        Guid.TryParse(value, out var merchantId) && merchantId != Guid.Empty
            ? merchantId
            : throw new IdentityAccessException("registration_merchant_required", "A trusted Merchant context is required.");
}
