using System.Security.Claims;
using System.Text.Json;
using Api.Admins;
using Api.Iam;
using Access.Domain;
using Accounts.Application;
using Accounts.Domain;
using Admins.Application;
using Admins.Application.Users;
using BuildingBlocks.Application;
using Iam.Domain.Permissions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using OpenIddict.Server;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using Microsoft.IdentityModel.Tokens;

namespace Api.IdentityAccess;

internal static class IdentityAccessEndpoints
{
    public static IEndpointRouteBuilder MapIdentityAccessEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/oauth/token", IssueToken)
            .AllowAnonymous()
            .WithName("IssueOAuthToken")
            .WithTags("การเข้าสู่ระบบ")
            .WithSummary("ออก OAuth token")
            .WithDescription("รองรับ authorization code (+PKCE) และ refresh token ของ workforce SPA และ client credentials ของ SYSTEM client; refresh token รับ merchant_id เพื่อออก token ใน merchant context");

        var auth = app.MapGroup("/api/v1/auth")
            .WithTags("การเข้าสู่ระบบ")
            .WithSummary("จัดการการยืนยันตัวตน")
            .WithDescription("เริ่ม OIDC login ของ agent และจบ employee session ตาม provider policy");

        auth.MapGet("/agents/login", BeginAgentLogin)
            .AllowAnonymous().WithName("BeginAgentLogin").WithTags("การเข้าสู่ระบบ");

        auth.MapGet("/agents/logout", EndAgentCiamSession)
            .AllowAnonymous().WithName("EndAgentCiamSession").WithTags("การเข้าสู่ระบบ")
            .WithSummary("จบ Microsoft CIAM session ของ Agent")
            .WithDescription("redirect ไป end_session_endpoint จาก OIDC metadata โดยใช้ post-logout URI ที่ server กำหนดเท่านั้น")
            .Produces(StatusCodes.Status302Found).ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        auth.MapPost("/logout", Logout)
            .RequireIdentityPlatformMutation()
            .RequireAuthorization("identity-platform").WithName("LogoutIdentitySession").WithTags("การเข้าสู่ระบบ")
            .WithSummary("ออกจากระบบ")
            .WithDescription("เพิกถอน OpenIddict authorization ของ token ปัจจุบัน ทำให้ access และ refresh token ของ login นั้นใช้ต่อไม่ได้ทันที")
            .Produces(StatusCodes.Status204NoContent);

        var account = app.MapGroup("/api/v1")
            .WithTags("การเข้าสู่ระบบ")
            .WithSummary("อ่านบริบทบัญชีและสิทธิ์")
            .WithDescription("คืนข้อมูล account, merchant access และ authorization context ของ caller ปัจจุบัน");
        account.MapGet("/me", GetMe)
            .RequireAuthorization("identity-platform").WithName("GetMe").WithTags("การเข้าสู่ระบบ");
        account.MapGet("/me/merchants", GetMerchants)
            .RequireAuthorization("identity-platform").WithName("GetMyMerchants").WithTags("การเข้าสู่ระบบ");
        account.MapGet("/me/access", GetAccess)
            .RequireAuthorization("identity-platform").WithName("GetMyAccess").WithTags("การเข้าสู่ระบบ");

        MapCanonicalA1(app, account);
        CanonicalAccessEndpoints.Map(app);

        return app;
    }

    private static void MapCanonicalA1(IEndpointRouteBuilder app, RouteGroupBuilder account)
    {
        app.MapGet("/.well-known/oauth-authorization-server", Discovery)
            .AllowAnonymous().WithName("GetOAuthAuthorizationServerMetadata")
            .WithTags("การเข้าสู่ระบบ").WithSummary("อ่าน OAuth authorization server metadata")
            .WithDescription("คืนตำแหน่ง OAuth endpoints และ JWKS โดยไม่คืนความลับ")
            .Produces(StatusCodes.Status200OK);

        app.MapGet("/.well-known/jwks.json", JsonWebKeySet)
            .AllowAnonymous().WithName("GetOAuthJsonWebKeySet")
            .WithTags("การเข้าสู่ระบบ").WithSummary("อ่าน public signing keys")
            .WithDescription("คืนเฉพาะ public JWK ของ OpenIddict signing credentials; ไม่คืน private material")
            .Produces(StatusCodes.Status200OK);

        app.MapGet("/oauth/authorize", Authorize)
            .AllowAnonymous().WithName("AuthorizeOAuthClient")
            .WithTags("การเข้าสู่ระบบ").WithSummary("เริ่ม OAuth authorization code flow")
            .WithDescription("ให้ OpenIddict ตรวจ registered redirect URI, state และ PKCE ก่อนออก authorization code")
            .Produces(StatusCodes.Status302Found).ProducesProblem(StatusCodes.Status400BadRequest);

        app.MapPost("/oauth/revoke", () => Results.Empty)
            .AllowAnonymous().WithName("RevokeOAuthToken")
            .WithTags("การเข้าสู่ระบบ").WithSummary("เพิกถอน OAuth token")
            .WithDescription("ให้ OpenIddict ตรวจ client ownership และ revoke token reference ใน token store")
            .Produces(StatusCodes.Status200OK).ProducesProblem(StatusCodes.Status400BadRequest);

        app.MapGet("/api/v1/auth/employees/callback", OidcCallbackFallback)
            .AllowAnonymous().WithName("EmployeeOAuthCallback")
            .WithTags("การเข้าสู่ระบบ").WithSummary("รับ workforce OIDC callback")
            .WithDescription("ปล่อยให้ OIDC handler ตรวจ state, nonce, issuer, tenant, signature และ lifetime; failure ปฏิเสธแบบ fail-closed");
        app.MapGet("/api/v1/auth/agents/callback", OidcCallbackFallback)
            .AllowAnonymous().WithName("AgentOAuthCallback")
            .WithTags("การเข้าสู่ระบบ").WithSummary("รับ agent OIDC callback")
            .WithDescription("ปล่อยให้ OIDC handler ตรวจ state, nonce, issuer, tenant, signature และ lifetime; failure ปฏิเสธแบบ fail-closed");

        account.MapGet("/me/sessions", ListMySessions)
            .RequireAuthorization("identity-platform")
            .WithName("ListMySessions").WithTags("การเข้าสู่ระบบ")
            .WithSummary("รายการ login sessions ของบัญชีตนเอง")
            .WithDescription("คืน metadata ของ OpenIddict authorization (หนึ่งรายการต่อ login) เท่านั้น ไม่คืน token และกรองด้วย AccountId จาก auth context")
            .Produces<IReadOnlyList<EmployeeSessionView>>();
        account.MapDelete("/me/sessions/{sessionId}", RevokeMySession)
            .WithMetadata(new IdempotencyMutationMarker())
            .RequireIdentityPlatformMutation()
            .RequireAuthorization("identity-platform")
            .WithName("RevokeMySession").WithTags("การเข้าสู่ระบบ")
            .WithSummary("เพิกถอน login session ที่เลือก")
            .WithDescription("ตรวจว่า authorization เป็นของ Account ปัจจุบันแล้ว revoke แบบ idempotent; token ทุกใบของ login นั้นใช้ต่อไม่ได้")
            .Produces(StatusCodes.Status204NoContent).ProducesProblem(StatusCodes.Status404NotFound);

        var system = app.MapGroup("/api/v1/system-clients")
            .RequireAuthorization("admin")
            .WithTags("ไคลเอนต์ API");
        system.MapGet("", ListSystemClients)
            .RequirePermission(Keys.UserManage).WithName("ListSystemClients")
            .WithSummary("รายการ SYSTEM clients").WithDescription("คืน metadata ของ SYSTEM clients ใน Admin merchant scope โดยไม่คืน secret");
        system.MapPost("", CreateSystemClient)
            .RequirePermission(Keys.UserManage)
            .WithMetadata(new IdempotencyMutationMarker(), new EtagResponseMarker("201"))
            .WithName("CreateSystemClient").WithSummary("สร้าง SYSTEM client")
            .WithDescription("สร้าง Account SYSTEM และ client_credentials client ที่ผูก Merchant/environment เดียว; ต้องส่ง Idempotency-Key");
        system.MapGet("/{clientId:guid}", GetSystemClient)
            .RequirePermission(Keys.UserManage).WithMetadata(new EtagResponseMarker("200"))
            .WithName("GetSystemClient").WithSummary("อ่าน SYSTEM client")
            .WithDescription("คืน client metadata, scopes และ public key policy โดยไม่คืน secret");
        system.MapPatch("/{clientId:guid}", UpdateSystemClient)
            .RequirePermission(Keys.UserManage)
            .WithMetadata(new IfMatchMutationMarker("200"), new IdempotencyMutationMarker())
            .WithName("UpdateSystemClient").WithSummary("แก้ไข SYSTEM client")
            .WithDescription("แก้ displayName/status เท่านั้น; Merchant, clientId และ grant type เปลี่ยนไม่ได้; ต้องส่ง If-Match และ Idempotency-Key");
        system.MapPut("/{clientId:guid}/access", ReplaceSystemClientAccess)
            .RequirePermission(Keys.SettingsManage)
            .WithMetadata(new IfMatchMutationMarker("200"), new IdempotencyMutationMarker())
            .WithName("ReplaceSystemClientAccess").WithSummary("แทนที่สิทธิ์ SYSTEM client")
            .WithDescription("แทนที่ scopes หลังตรวจ grant registry และ Account authorization version; ต้องส่ง If-Match และ Idempotency-Key");
        system.MapGet("/{clientId:guid}/keys", ListClientKeys)
            .RequirePermission(Keys.UserManage).WithName("ListSystemClientKeys")
            .WithSummary("รายการ public keys ของ SYSTEM client").WithDescription("คืน public key policy และ validity เท่านั้น");
        system.MapPost("/{clientId:guid}/keys", CreateClientKey)
            .RequirePermission(Keys.UserManage)
            .WithMetadata(new IdempotencyMutationMarker(), new EtagResponseMarker("201"))
            .WithName("CreateSystemClientKey").WithSummary("ลงทะเบียน SYSTEM public key")
            .WithDescription("รับเฉพาะ public JWK metadata; private material ถูกปฏิเสธ; ต้องส่ง Idempotency-Key");
        system.MapDelete("/{clientId:guid}/keys/{keyId:guid}", DeleteClientKey)
            .RequirePermission(Keys.UserManage)
            .WithMetadata(new IdempotencyMutationMarker())
            .WithName("RevokeSystemClientKey").WithSummary("เพิกถอน SYSTEM public key")
            .WithDescription("เพิกถอน key policy และทำให้ authorization version ของ client ใช้ต่อไม่ได้");
    }

    private static async Task<IResult> IssueToken(
        HttpContext http,
        IIdentityAccessQuery identities,
        IOptions<IdentityAccessOptions> options,
        CancellationToken cancellationToken)
    {
        var request = Microsoft.AspNetCore.OpenIddictServerAspNetCoreHelpers.GetOpenIddictServerRequest(http);
        if (request is null)
            return Results.NotFound();

        if (request.IsAuthorizationCodeGrantType() || request.IsRefreshTokenGrantType())
            return await IssueEmployeeTokenAsync(http, request, identities, options.Value, cancellationToken);

        if (string.Equals(request.GrantType, OpenIddictConstants.GrantTypes.ClientCredentials,
                StringComparison.Ordinal))
        {
            var resolved = string.IsNullOrWhiteSpace(request.ClientId)
                ? null
                : await identities.FindSystemClientAsync(request.ClientId, cancellationToken);
            if (resolved is null || resolved.Account.Status != AccountStatus.Active
                || resolved.Client.Status != SystemClientStatus.Active)
                return InvalidClient(http);

            var requestedScopes = request.GetScopes()
                .Where(scope => !string.IsNullOrWhiteSpace(scope))
                .Select(scope => scope.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var registeredScopes = resolved.Scopes
                .Where(scope => !string.IsNullOrWhiteSpace(scope))
                .Select(scope => scope.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToHashSet(StringComparer.Ordinal);
            var grantedScopes = requestedScopes.Length == 0
                ? registeredScopes.Order(StringComparer.Ordinal).ToArray()
                : requestedScopes;
            if (grantedScopes.Any(scope => !registeredScopes.Contains(scope)))
                return InvalidScope(http);

            var identity = new ClaimsIdentity(
                OpenIddictServerAspNetCoreDefaults.AuthenticationScheme,
                nameType: "sub",
                roleType: "role");
            identity.SetClaim(OpenIddictConstants.Claims.Subject, resolved.Account.Id.ToString("D"));
            identity.SetClaim("account_type", resolved.Account.AccountType.ToString().ToUpperInvariant());
            identity.SetClaim("authz_version", resolved.Account.AuthorizationVersion.ToString());
            identity.SetClaim("client_id", resolved.Client.ClientId);
            identity.SetClaim("merchant_id", resolved.Client.MerchantId.ToString("D"));
            identity.SetScopes(grantedScopes);
            identity.SetResources(SystemClientScopeRegistry.ApiAudience);
            identity.SetAudiences(SystemClientScopeRegistry.ApiAudience);
            identity.SetDestinations(_ => [OpenIddictConstants.Destinations.AccessToken]);
            return Results.SignIn(new ClaimsPrincipal(identity),
                authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        }

        return OAuthError(OpenIddictConstants.Errors.UnsupportedGrantType, "The grant type is not supported.");
    }

    /// <summary>Authorization code and refresh token grants of the workforce SPA. OpenIddict has already validated
    /// the code/PKCE or the reference refresh token (one-time use, authorization still valid); the principal it
    /// stored is re-checked against the account and re-issued with the CURRENT authorization version, so a
    /// refresh is where a stale token re-syncs. A refresh may also select a merchant context.</summary>
    private static async Task<IResult> IssueEmployeeTokenAsync(
        HttpContext http,
        OpenIddictRequest request,
        IIdentityAccessQuery identities,
        IdentityAccessOptions settings,
        CancellationToken cancellationToken)
    {
        var stored = (await http.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme)).Principal;
        if (stored is null || !Guid.TryParse(stored.GetClaim(OpenIddictConstants.Claims.Subject), out var accountId))
            return OAuthError(OpenIddictConstants.Errors.InvalidGrant, "The token is no longer valid.");

        var account = await identities.FindAccountAsync(accountId, cancellationToken);
        if (account is null || account.Status != AccountStatus.Active)
            return OAuthError(OpenIddictConstants.Errors.InvalidGrant, "The account is not active.");

        Guid? merchantId = Guid.TryParse(stored.GetClaim("merchant_id"), out var current) && current != Guid.Empty
            ? current
            : null;
        if (request.IsRefreshTokenGrantType() && (string?)request.GetParameter("merchant_id") is { } requested)
        {
            if (string.IsNullOrWhiteSpace(requested))
                merchantId = null;
            else if (!Guid.TryParse(requested, out var selected) || selected == Guid.Empty)
                return OAuthError(OpenIddictConstants.Errors.InvalidRequest, "merchant_id must be a non-empty GUID.");
            else
            {
                var authorization = await identities.ResolveAuthorizationAsync(
                    account.Id, selected, null, cancellationToken);
                if (authorization?.MerchantId != selected || authorization.AccountStatus != AccountStatus.Active)
                    return OAuthError(OpenIddictConstants.Errors.InvalidGrant, "The account has no active access to that merchant.");
                merchantId = selected;
            }
        }

        // Keep OpenIddict's private claims (authorization id, presenters, scopes) and replace ours.
        var identity = new ClaimsIdentity(
            stored.Claims.Where(claim => claim.Type is not ("account_type" or "authz_version" or "token_context"
                or "merchant_id" or "scope")),
            OpenIddictServerAspNetCoreDefaults.AuthenticationScheme,
            nameType: "sub",
            roleType: "role");
        ApplyEmployeeClaims(identity, account, merchantId, settings);
        return Results.SignIn(new ClaimsPrincipal(identity),
            authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    /// <summary>Claims every employee JWT carries; the same set the BFF cookie used to materialize per request.</summary>
    private static void ApplyEmployeeClaims(
        ClaimsIdentity identity, Account account, Guid? merchantId, IdentityAccessOptions settings)
    {
        identity.SetClaim(OpenIddictConstants.Claims.Subject, account.Id.ToString("D"));
        identity.SetClaim("account_type", account.AccountType.ToString().ToUpperInvariant());
        identity.SetClaim("authz_version", account.AuthorizationVersion.ToString());
        identity.SetClaim("token_context", merchantId is null ? "ACCOUNT_SELF" : "MERCHANT");
        if (merchantId is { } selected)
            identity.SetClaim("merchant_id", selected.ToString("D"));
        identity.SetResources(SystemClientScopeRegistry.ApiAudience);
        identity.SetAudiences(SystemClientScopeRegistry.ApiAudience);
        identity.SetAccessTokenLifetime(TimeSpan.FromMinutes(settings.AccessTokenMinutes));
        identity.SetRefreshTokenLifetime(TimeSpan.FromMinutes(settings.RefreshTokenMinutes));
        identity.SetDestinations(_ => [OpenIddictConstants.Destinations.AccessToken]);
    }

    private static IResult OAuthError(string error, string description) => Results.Json(
        new { error, error_description = description },
        statusCode: StatusCodes.Status400BadRequest,
        contentType: "application/json");

    private static IResult InvalidClient(HttpContext http)
    {
        http.Response.Headers["WWW-Authenticate"] = "Basic realm=\"oauth\"";
        return Results.Json(
            new { error = OpenIddictConstants.Errors.InvalidClient },
            statusCode: StatusCodes.Status401Unauthorized,
            contentType: "application/json");
    }

    private static IResult InvalidScope(HttpContext http) => Results.Json(
        new { error = OpenIddictConstants.Errors.InvalidScope },
        statusCode: StatusCodes.Status400BadRequest,
        contentType: "application/json");

    private static IResult Discovery(HttpContext http, IConfiguration configuration)
    {
        var issuer = configuration["OAuth:Issuer"]?.TrimEnd('/')
            ?? $"{http.Request.Scheme}://{http.Request.Host}";
        return Results.Ok(new
        {
            issuer,
            authorization_endpoint = $"{issuer}/oauth/authorize",
            token_endpoint = $"{issuer}/oauth/token",
            revocation_endpoint = $"{issuer}/oauth/revoke",
            end_session_endpoint = $"{issuer}/api/v1/auth/agents/logout",
            jwks_uri = $"{issuer}/.well-known/jwks.json",
            response_types_supported = new[] { "code" },
            grant_types_supported = new[] { "authorization_code", "refresh_token", "client_credentials" },
            code_challenge_methods_supported = new[] { "S256" },
            token_endpoint_auth_methods_supported = new[] { "private_key_jwt", "none" },
        });
    }

    private static IResult JsonWebKeySet(IOptions<OpenIddictServerOptions> options)
    {
        var keys = options.Value.SigningCredentials
            .Select(credentials => JsonWebKeyConverter.ConvertFromSecurityKey(credentials.Key))
            .Where(key => !string.IsNullOrWhiteSpace(key.Kty))
            .Select(key => new
            {
                kty = key.Kty,
                use = "sig",
                kid = key.Kid,
                alg = key.Alg,
                n = key.N,
                e = key.E,
                crv = key.Crv,
                x = key.X,
                y = key.Y,
            });
        return Results.Ok(new { keys });
    }

    /// <summary>Workforce SPA entry point (authorization code + PKCE). Without the login cookie the request is
    /// parked in the Entra challenge's RedirectUri; the callback signs the verified account into that cookie and
    /// returns here, where OpenIddict issues the code and the cookie is discarded.</summary>
    private static async Task<IResult> Authorize(
        HttpContext http,
        IdentityAccessProviders providers,
        IIdentityAccessQuery identities,
        IOptions<IdentityAccessOptions> options,
        CancellationToken cancellationToken)
    {
        var request = Microsoft.AspNetCore.OpenIddictServerAspNetCoreHelpers.GetOpenIddictServerRequest(http);
        if (request is null || string.IsNullOrWhiteSpace(request.RedirectUri))
            return Results.Json(new { error = OpenIddictConstants.Errors.InvalidRequest }, statusCode: 400);

        // The client_id (already validated by OpenIddict against the registered clients) selects the human realm:
        // the agent SPA challenges the "agents" (Entra External ID) provider, everything else the workforce one.
        var isAgentClient = string.Equals(request.ClientId, options.Value.AgentClientId, StringComparison.Ordinal);
        var accountSelectionRequested = isAgentClient && string.Equals(
            request.Prompt, OpenIdConnectPrompt.SelectAccount, StringComparison.Ordinal);
        if (isAgentClient
            && !string.IsNullOrWhiteSpace(request.Prompt)
            && !accountSelectionRequested)
            return OAuthError(OpenIddictConstants.Errors.InvalidRequest, "The prompt value is not supported for this client.");

        var login = await http.AuthenticateAsync(IdentityAccessWiring.LoginCookieScheme);
        var accountSelectionCompleted = login.Properties?.Items.TryGetValue(
            IdentityAccessWiring.AgentAccountSelectionItem, out var selectionStatus) == true
            && string.Equals(selectionStatus, "completed", StringComparison.Ordinal);
        if (login.Principal?.Identity?.IsAuthenticated == true
            && accountSelectionRequested
            && !accountSelectionCompleted)
        {
            await http.SignOutAsync(IdentityAccessWiring.LoginCookieScheme);
            login = AuthenticateResult.NoResult();
        }

        if (login.Principal?.Identity?.IsAuthenticated != true)
        {
            if (!providers.TryGetValue(isAgentClient ? "agents" : "employees", out var scheme))
                return Results.Problem(
                    statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: isAgentClient ? "Agent login is not configured." : "Employee login is not configured.",
                    extensions: new Dictionary<string, object?> { ["code"] = "capability_not_configured" });
            var properties = new AuthenticationProperties
            {
                RedirectUri = http.Request.GetEncodedPathAndQuery(),
            };
            properties.Items["identity.expected_issuer"] = isAgentClient ? options.Value.AgentIssuer : options.Value.WorkforceIssuer;
            properties.Items["identity.realm"] = isAgentClient ? "external" : "workforce";
            if (isAgentClient && options.Value.AgentMerchantId is { } agentMerchantId)
                properties.Items["identity.merchant_id"] = agentMerchantId.ToString("D");
            if (accountSelectionRequested)
            {
                properties.Items[IdentityAccessWiring.AgentAccountSelectionItem] = "requested";
                properties.SetParameter(OpenIdConnectParameterNames.Prompt, OpenIdConnectPrompt.SelectAccount);
            }
            return Results.Challenge(properties, [scheme]);
        }

        await http.SignOutAsync(IdentityAccessWiring.LoginCookieScheme);
        if (!Guid.TryParse(login.Principal.FindFirstValue("sub"), out var accountId))
            return Results.Json(new { error = OpenIddictConstants.Errors.LoginRequired }, statusCode: 401);
        var account = await identities.FindAccountAsync(accountId, cancellationToken);
        if (account is null || account.Status != AccountStatus.Active)
            return Results.Json(new { error = OpenIddictConstants.Errors.AccessDenied }, statusCode: 403);
        // A login cookie minted for one realm must not turn into a code for the other realm's client.
        if (isAgentClient != (account.AccountType == AccountType.Agent))
            return Results.Json(new { error = OpenIddictConstants.Errors.AccessDenied }, statusCode: 403);

        var identity = new ClaimsIdentity(
            OpenIddictServerAspNetCoreDefaults.AuthenticationScheme, nameType: "sub", roleType: "role");
        identity.SetScopes(request.GetScopes());
        ApplyEmployeeClaims(identity, account, merchantId: null, options.Value);
        return Results.SignIn(new ClaimsPrincipal(identity),
            authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    private static IResult OidcCallbackFallback(HttpContext http) => Results.Problem(
        statusCode: StatusCodes.Status401Unauthorized,
        title: "OIDC callback validation failed.",
        extensions: new Dictionary<string, object?>
        {
            ["code"] = "invalid_authentication",
            ["traceId"] = http.TraceIdentifier,
        });

    private static async Task<IResult> ListMySessions(
        HttpContext http,
        IOpenIddictAuthorizationManager authorizations,
        IOpenIddictApplicationManager applications,
        IIdentityAccessQuery identities,
        IOptions<IdentityAccessOptions> options,
        CancellationToken cancellationToken)
    {
        var accountId = GetAccountId(http.User);
        if (accountId is null)
            return Results.Unauthorized();
        var account = await identities.FindAccountAsync(accountId.Value, cancellationToken);
        var clientId = account?.AccountType == AccountType.Agent ? options.Value.AgentClientId : options.Value.WorkforceClientId;
        var application = await applications.FindByClientIdAsync(clientId, cancellationToken);
        if (application is null)
            return Results.Ok(Array.Empty<EmployeeSessionView>());
        var client = await applications.GetIdAsync(application, cancellationToken) ?? string.Empty;
        var sessions = new List<EmployeeSessionView>();
        await foreach (var authorization in authorizations.FindAsync(
            accountId.Value.ToString("D"), client, status: null, type: null, scopes: null, cancellationToken))
        {
            sessions.Add(new EmployeeSessionView(
                await authorizations.GetIdAsync(authorization, cancellationToken) ?? string.Empty,
                accountId.Value,
                (await authorizations.GetCreationDateAsync(authorization, cancellationToken))?.UtcDateTime,
                await authorizations.GetStatusAsync(authorization, cancellationToken) ?? string.Empty,
                await authorizations.HasStatusAsync(authorization, OpenIddictConstants.Statuses.Valid, cancellationToken)));
        }
        return Results.Ok(sessions.OrderByDescending(x => x.IssuedAt).ThenByDescending(x => x.SessionId, StringComparer.Ordinal).ToArray());
    }

    private static async Task<IResult> RevokeMySession(
        string sessionId,
        HttpContext http,
        IOpenIddictAuthorizationManager authorizations,
        CancellationToken cancellationToken)
    {
        var accountId = GetAccountId(http.User);
        if (accountId is null)
            return Results.Unauthorized();
        var authorization = await authorizations.FindByIdAsync(sessionId, cancellationToken);
        if (authorization is null
            || !string.Equals(await authorizations.GetSubjectAsync(authorization, cancellationToken),
                accountId.Value.ToString("D"), StringComparison.Ordinal))
            return Results.NotFound();
        await authorizations.TryRevokeAsync(authorization, cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> ListSystemClients(
        Guid? merchantId, IAdminScope scope, IIdentityAccessAdminStore store, CancellationToken cancellationToken)
    {
        if (merchantId is { } selected && !scope.Accessible.Allows(selected))
            return Results.NotFound();
        return Results.Ok(await store.ListSystemClientsAsync(merchantId, cancellationToken));
    }

    private static async Task<IResult> CreateSystemClient(
        SystemClientCreateRequest body, HttpContext http, IAdminScope scope,
        IIdentityAccessAdminStore store, CancellationToken cancellationToken)
    {
        if (!scope.Accessible.Allows(body.MerchantId))
            return Results.NotFound();
        var key = IdempotencyKeys.Require(http);
        var result = await store.CreateSystemClientIdempotentAsync(scope.Current.AdminId, key, new SystemClientAdminCreate(
            body.MerchantId, body.ClientId, body.DisplayName, body.Environment, body.Scopes ?? []), cancellationToken);
        VersionEtags.Set(http, result.Value.AccountAuthorizationVersion);
        return result.Replayed
            ? Results.Ok(result.Value)
            : Results.Created($"/api/v1/system-clients/{result.Value.SystemClientId:D}", result.Value);
    }

    private static async Task<IResult> GetSystemClient(
        Guid clientId, IAdminScope scope, IIdentityAccessAdminStore store, CancellationToken cancellationToken)
    {
        var result = await store.GetSystemClientAsync(clientId, cancellationToken);
        return result is null || !scope.Accessible.Allows(result.MerchantId)
            ? Results.NotFound()
            : Results.Ok(result);
    }

    private static async Task<IResult> UpdateSystemClient(
        Guid clientId, SystemClientPatchRequest body, HttpContext http, IAdminScope scope,
        IIdentityAccessAdminStore store, CancellationToken cancellationToken)
    {
        var current = await store.GetSystemClientAsync(clientId, cancellationToken);
        if (current is null || !scope.Accessible.Allows(current.MerchantId))
            return Results.NotFound();
        var key = IdempotencyKeys.Require(http);
        (SystemClientAdminView Value, bool Replayed) result;
        try
        {
            result = await store.UpdateSystemClientIdempotentAsync(scope.Current.AdminId, key, new SystemClientAdminUpdate(
                clientId, body.DisplayName, body.Status, VersionEtags.Require(http)), cancellationToken);
        }
        catch (ConcurrencyConflictException)
        {
            return Results.Problem(statusCode: StatusCodes.Status412PreconditionFailed,
                extensions: new Dictionary<string, object?> { ["code"] = "precondition_failed" });
        }
        VersionEtags.Set(http, result.Value.AccountAuthorizationVersion);
        return Results.Ok(result.Value);
    }

    private static async Task<IResult> ReplaceSystemClientAccess(
        Guid clientId, SystemClientAccessRequest body, HttpContext http, IAdminScope scope,
        IIdentityAccessAdminStore store, CancellationToken cancellationToken)
    {
        var current = await store.GetSystemClientAsync(clientId, cancellationToken);
        if (current is null || !scope.Accessible.Allows(current.MerchantId))
            return Results.NotFound();
        var key = IdempotencyKeys.Require(http);
        (SystemClientAdminView Value, bool Replayed) result;
        try
        {
            result = await store.ReplaceSystemClientAccessIdempotentAsync(
                scope.Current.AdminId, key, new SystemClientAccessReplace(clientId, body.Scopes ?? []), cancellationToken);
        }
        catch (ConcurrencyConflictException)
        {
            return Results.Problem(statusCode: StatusCodes.Status412PreconditionFailed,
                extensions: new Dictionary<string, object?> { ["code"] = "precondition_failed" });
        }
        VersionEtags.Set(http, result.Value.AccountAuthorizationVersion);
        return Results.Ok(result.Value);
    }

    private static async Task<IResult> ListClientKeys(
        Guid clientId, IAdminScope scope, IIdentityAccessAdminStore store, CancellationToken cancellationToken)
    {
        var current = await store.GetSystemClientAsync(clientId, cancellationToken);
        return current is null || !scope.Accessible.Allows(current.MerchantId)
            ? Results.NotFound()
            : Results.Ok(await store.ListClientKeysAsync(clientId, cancellationToken));
    }

    private static async Task<IResult> CreateClientKey(
        Guid clientId, ClientKeyCreateRequest body, HttpContext http, IAdminScope scope,
        IIdentityAccessAdminStore store, CancellationToken cancellationToken)
    {
        if (body.HasPrivateMaterial())
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest,
                extensions: new Dictionary<string, object?> { ["code"] = "private_key_not_allowed" });
        var current = await store.GetSystemClientAsync(clientId, cancellationToken);
        if (current is null || !scope.Accessible.Allows(current.MerchantId))
            return Results.NotFound();
        var key = IdempotencyKeys.Require(http);
        var result = await store.CreateClientKeyIdempotentAsync(scope.Current.AdminId, key, new ClientKeyAdminCreate(
            clientId, body.ApplicationId, body.Kid, body.Algorithm, body.ValidFrom, body.ValidUntil,
            body.AuditReference, body.Jwk.ToJsonString()), cancellationToken);
        return result.Replayed
            ? Results.Ok(result.Value)
            : Results.Created($"/api/v1/system-clients/{clientId:D}/keys/{result.Value.KeyId:D}", result.Value);
    }

    private static async Task<IResult> DeleteClientKey(
        Guid clientId, Guid keyId, HttpContext http, IAdminScope scope,
        IIdentityAccessAdminStore store, CancellationToken cancellationToken)
    {
        var current = await store.GetSystemClientAsync(clientId, cancellationToken);
        if (current is null || !scope.Accessible.Allows(current.MerchantId))
            return Results.NotFound();
        var key = IdempotencyKeys.Require(http);
        await store.DeleteClientKeyIdempotentAsync(scope.Current.AdminId, key, clientId, keyId, cancellationToken);
        return Results.NoContent();
    }

    private static IResult BeginAgentLogin(
        HttpContext http,
        IdentityAccessProviders providers,
        IOptions<IdentityAccessOptions> options,
        string? returnTo = null)
    {
        var merchantId = options.Value.AgentMerchantId;
        if (merchantId is null || merchantId == Guid.Empty)
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Agent login is not configured.",
                extensions: new Dictionary<string, object?> { ["code"] = "capability_not_configured" });

        return BeginLogin(
            http,
            providers,
            "agents",
            NormalizeReturnTo(returnTo),
            properties =>
            {
                properties.Items["identity.realm"] = "external";
                properties.Items["identity.merchant_id"] = merchantId.Value.ToString("D");
            },
            options.Value.AgentIssuer);
    }

    private static IResult BeginLogin(
        HttpContext http,
        IdentityAccessProviders providers,
        string providerKey,
        string returnTo,
        Action<AuthenticationProperties> configure,
        string expectedIssuer)
    {
        if (!providers.TryGetValue(providerKey, out var scheme))
            return Results.NotFound();

        var properties = new AuthenticationProperties { RedirectUri = returnTo };
        properties.Items["identity.expected_issuer"] = expectedIssuer;
        configure(properties);
        return Results.Challenge(properties, [scheme]);
    }

    /// <summary>Returns a browser to the Agent CIAM end-session endpoint. Both destinations are server-owned:
    /// provider metadata owns the logout endpoint and IdentityAccess configuration owns the final SPA path.</summary>
    private static async Task<IResult> EndAgentCiamSession(
        IOptionsMonitor<OpenIdConnectOptions> oidcOptions,
        IOptions<IdentityAccessOptions> identityOptions,
        CancellationToken cancellationToken)
    {
        var options = oidcOptions.Get(IdentityAccessWiring.AgentScheme);
        OpenIdConnectConfiguration configuration;
        try
        {
            configuration = options.ConfigurationManager is null
                ? options.Configuration
                    ?? throw new InvalidOperationException("Agent OIDC configuration is unavailable.")
                : await options.ConfigurationManager.GetConfigurationAsync(cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Agent end-session configuration is unavailable.",
                extensions: new Dictionary<string, object?>
                {
                    ["code"] = "end_session_configuration_unavailable",
                });
        }

        if (!Uri.TryCreate(configuration.EndSessionEndpoint, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(endpoint.UserInfo)
            || !string.IsNullOrEmpty(endpoint.Fragment))
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Agent end-session endpoint is not configured.",
                extensions: new Dictionary<string, object?> { ["code"] = "end_session_not_configured" });

        var postLogout = identityOptions.Value.AgentWebAppBaseUrl.TrimEnd('/') + "/login";
        var destination = Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(
            endpoint.ToString(), "post_logout_redirect_uri", postLogout);
        return Results.Redirect(destination);
    }

    /// <summary>Ends the login the Bearer token belongs to: revoking its OpenIddict authorization rejects every
    /// access and refresh token issued under it on the next request (authorization entry validation).</summary>
    internal static async Task<IResult> Logout(
        HttpContext http,
        IOpenIddictAuthorizationManager authorizations,
        CancellationToken cancellationToken)
    {
        var authorizationId = http.User.GetAuthorizationId();
        if (!string.IsNullOrWhiteSpace(authorizationId)
            && await authorizations.FindByIdAsync(authorizationId, cancellationToken) is { } authorization
            && string.Equals(await authorizations.GetSubjectAsync(authorization, cancellationToken),
                http.User.FindFirstValue("sub"), StringComparison.Ordinal))
            await authorizations.TryRevokeAsync(authorization, cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> GetMe(
        HttpContext http,
        IIdentityAccessQuery identities,
        CancellationToken cancellationToken)
    {
        var accountId = GetAccountId(http.User);
        if (accountId is null)
            return Results.Unauthorized();

        var account = await identities.FindAccountAsync(accountId.Value, cancellationToken);
        if (account is null || account.Status != AccountStatus.Active)
            return Results.Unauthorized();

        return Results.Ok(new
        {
            accountId = account.Id,
            // Enums go through the global JsonStringEnumConverter so /me spells them the same way /me/access does.
            accountType = account.AccountType,
            displayName = account.DisplayName,
            email = await identities.FindLoginEmailAsync(account.Id, cancellationToken),
            status = account.Status,
            authorizationVersion = account.AuthorizationVersion,
            merchantContext = GetMerchantId(http.User),
        });
    }

    private static async Task<IResult> GetMerchants(
        HttpContext http,
        IIdentityAccessQuery identities,
        CancellationToken cancellationToken)
    {
        var accountId = GetAccountId(http.User);
        if (accountId is null)
            return Results.Unauthorized();
        var account = await identities.FindAccountAsync(accountId.Value, cancellationToken);
        if (account is null || account.Status != AccountStatus.Active)
            return Results.Unauthorized();
        var accesses = await identities.ListMerchantAccessAsync(account.Id, cancellationToken);
        return Results.Ok(accesses
            .Where(x => x.Status == AccessStatus.Active)
            .Select(x => new { merchantId = x.MerchantId, dataScope = x.DataScope.ToString() })
            .ToArray());
    }

    private static async Task<IResult> GetAccess(
        HttpContext http,
        IIdentityAccessQuery identities,
        CancellationToken cancellationToken)
    {
        var accountId = GetAccountId(http.User);
        if (accountId is null)
            return Results.Unauthorized();
        var account = await identities.FindAccountAsync(accountId.Value, cancellationToken);
        if (account is null || account.Status != AccountStatus.Active)
            return Results.Unauthorized();
        var selectedMerchant = GetMerchantId(http.User);
        var snapshot = await identities.ResolveAuthorizationAsync(
            account.Id, selectedMerchant, GetClientId(http.User), cancellationToken);
        if (snapshot is null)
            return Results.Unauthorized();
        return Results.Ok(snapshot);
    }

    private static Guid? GetAccountId(ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue("sub"), out var id) && id != Guid.Empty ? id : null;

    private static Guid? GetMerchantId(ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue("merchant_id"), out var id) && id != Guid.Empty ? id : null;

    private static Guid? GetClientId(ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue("client_id"), out var id) && id != Guid.Empty ? id : null;

    private static string NormalizeReturnTo(string? returnTo) =>
        !string.IsNullOrWhiteSpace(returnTo) && returnTo.StartsWith('/') && !returnTo.StartsWith("//")
            ? returnTo
            : "/";

/// <summary>One login session of the caller (an OpenIddict authorization); never carries a token.</summary>
internal sealed record EmployeeSessionView(
    string SessionId, Guid AccountId, DateTime? IssuedAt, string Status, bool Live);
}

internal sealed record SystemClientCreateRequest(
    Guid MerchantId,
    string ClientId,
    string DisplayName,
    string Environment,
    IReadOnlyList<string>? Scopes);

internal sealed record SystemClientPatchRequest(string DisplayName, SystemClientStatus Status);

internal sealed record SystemClientAccessRequest(IReadOnlyList<string>? Scopes);

internal sealed record ClientKeyCreateRequest(
    System.Text.Json.Nodes.JsonObject Jwk,
    string ApplicationId,
    string Kid,
    string Algorithm,
    DateTime ValidFrom,
    DateTime? ValidUntil,
    string? AuditReference)
{
    public bool HasPrivateMaterial() =>
        Jwk.Any(x => x.Key is "d" or "p" or "q" or "dp" or "dq" or "qi" or "k");
}
