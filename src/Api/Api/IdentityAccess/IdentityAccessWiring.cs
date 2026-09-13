using System.Security.Claims;
using Accounts.Application;
using Accounts.Domain;
using Api.Iam;
using Microsoft.AspNetCore.Authentication;
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
    public static IServiceCollection AddIdentityAccess(
        this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        services.AddOptions<IdentityAccessOptions>()
            .Bind(configuration.GetSection(IdentityAccessOptions.SectionName))
            .Validate(options =>
            {
                try
                {
                    options.Validate();
                    return true;
                }
                catch
                {
                    return false;
                }
            }, "IdentityAccess options are invalid.")
            .ValidateOnStart();

        services.AddScoped<BffSessionManager>();
        services.AddScoped<OpenIddictRefreshTokenRotator>();
        services.AddScoped<IAuthorizationHandler, IdentityAccessAuthorizationHandler>();
        services.AddScoped<IdentityBffLoginService>();
        services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, BffSessionAuthenticationHandler>(
                BffSessionAuthenticationHandler.SchemeName, _ => { })
            .AddCookie("identity-oidc-noop");

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
            configuration.GetSection("IdentityAccess:Agent").Get<IdentityOidcProviderOptions>()
                ?? new IdentityOidcProviderOptions(),
            scheme: "IdentityAgentMicrosoft",
            callbackPath: "/api/v1/auth/agents/callback",
            kind: IdentityLoginKind.Agent,
            services,
            environment);
        services.AddSingleton(providers);

        services.AddAuthorizationBuilder()
            .AddPolicy("identity-bff", policy => policy
                .AddAuthenticationSchemes(BffSessionAuthenticationHandler.SchemeName)
                .RequireAuthenticatedUser()
                .AddRequirements(new IdentityAccessRequirement()))
            .AddPolicy("identity-platform", policy => policy
                .AddAuthenticationSchemes(
                    BffSessionAuthenticationHandler.SchemeName,
                    OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)
                .RequireAuthenticatedUser()
                .AddRequirements(new IdentityAccessRequirement()));

        return services;
    }

    public static IApplicationBuilder UseIdentityAccess(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var hasBearer = context.Request.Headers.Authorization.ToString()
                .StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase);
            var hasBffCookie = context.Request.Cookies.ContainsKey(BffSessionManager.SessionCookieName)
                || context.Request.Cookies.ContainsKey(BffSessionManager.SessionCookieNameDevHttp);
            var hasConsoleCookie = context.Request.Cookies.ContainsKey(Api.Admins.SessionCookies.SessionCookieName)
                || context.Request.Cookies.ContainsKey(Api.Admins.SessionCookies.SessionCookieNameDevHttp)
                || context.Request.Cookies.ContainsKey(Api.Merchants.UserSessionCookies.SessionCookieName)
                || context.Request.Cookies.ContainsKey(Api.Merchants.UserSessionCookies.SessionCookieNameDevHttp);
            var ambiguousOrderContext = IdentityPermissionAuthorization.IsIdentityOrderRoute(context)
                && ((hasBearer && (hasBffCookie || hasConsoleCookie))
                    || (hasBffCookie && hasConsoleCookie));
            if (ambiguousOrderContext)
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
            // The CallbackPath may be shared with the legacy admin OIDC scheme (the only redirect URI registered on
            // the workforce app). State is data-protected per scheme, so a callback whose state this handler cannot
            // unprotect is not ours: pass it through instead of failing. Load-bearing order: AddIdentityAccess runs
            // before AddAdminOidcAuthentication in Program.cs, so this handler sees the shared callback first and the
            // admin handler (last, no skip) keeps its own failure path.
            options.SkipUnrecognizedRequests = true;
            options.SignInScheme = "identity-oidc-noop";
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
                    await login.CompleteAsync(context, kind);
                    context.HandleResponse();
                },
                OnRemoteFailure = context =>
                {
                    if (!context.Response.HasStarted)
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    context.HandleResponse();
                    return Task.CompletedTask;
                },
            };
        });
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
    BffSessionManager bff,
    OpenIddictRefreshTokenRotator refreshTokens,
    IOptions<IdentityAccessOptions> options)
{
    public async Task CompleteAsync(TicketReceivedContext context, IdentityLoginKind kind)
    {
        var principal = context.Principal
            ?? throw new InvalidOperationException("OIDC callback did not contain a principal.");
        var properties = context.Properties ?? new AuthenticationProperties();
        var verified = FromPrincipal(principal, properties.GetTokenValue("id_token"), workforceEligible: kind == IdentityLoginKind.Employee);
        var settings = options.Value;
        // RemoteAuthenticationHandler moves Properties.RedirectUri into ReturnUri (and nulls it) before raising
        // TicketReceived, so the login-time returnTo is only available here.
        var returnTo = context.ReturnUri ?? properties.RedirectUri;
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
            // The ticket carries platform tokens, not Entra's: refresh rotates and logout revokes this OpenIddict
            // reference token (design API-011). Entra's access/refresh tokens are not needed after the callback.
            var refreshToken = await refreshTokens.IssueAsync(
                account.Id,
                DateTimeOffset.UtcNow.AddMinutes(settings.BffSessionMinutes),
                context.HttpContext.RequestAborted);
            var issue = await bff.CreateAsync(
                account,
                clientId: null,
                accessToken: null,
                refreshToken,
                merchantId: null,
                returnTo ?? "/",
                context.HttpContext.RequestAborted);
            bff.WriteCookies(context.HttpContext, issue.SessionToken, issue.CsrfToken);
            context.HttpContext.Response.Redirect(ToWebApp(returnTo, settings.WorkforceWebAppBaseUrl));
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
    }

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
