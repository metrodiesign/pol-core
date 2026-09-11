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
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
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
            .WithDescription("รองรับ authorization code, refresh token และ client credentials ตาม OAuth error contract");

        var auth = app.MapGroup("/api/v1/auth")
            .WithTags("การเข้าสู่ระบบ")
            .WithSummary("จัดการการยืนยันตัวตน")
            .WithDescription("เริ่ม OIDC login และจัดการ BFF session ตาม provider policy");

        auth.MapGet("/employees/login", BeginEmployeeLogin)
            .AllowAnonymous().WithName("BeginEmployeeLogin").WithTags("การเข้าสู่ระบบ");
        auth.MapGet("/agents/login", BeginAgentLogin)
            .AllowAnonymous().WithName("BeginAgentLogin").WithTags("การเข้าสู่ระบบ");

        auth.MapGet("/session", GetSession)
            .RequireAuthorization("identity-platform").WithName("GetIdentitySession").WithTags("การเข้าสู่ระบบ");
        auth.MapPost("/session/refresh", RefreshSession)
            .WithMetadata(new BffCsrfProtected())
            .AddEndpointFilter<BffCsrfFilter>()
            .RequireAuthorization("identity-bff").WithName("RefreshIdentitySession").WithTags("การเข้าสู่ระบบ");
        auth.MapPost("/merchant-context", SelectMerchantContext)
            .WithMetadata(new BffCsrfProtected())
            .AddEndpointFilter<BffCsrfFilter>()
            .RequireAuthorization("identity-bff").WithName("SelectIdentityMerchantContext").WithTags("การเข้าสู่ระบบ");
        auth.MapPost("/logout", Logout)
            .WithMetadata(new BffCsrfProtected())
            .AddEndpointFilter<BffCsrfFilter>()
            .RequireAuthorization("identity-bff").WithName("LogoutIdentitySession").WithTags("การเข้าสู่ระบบ");

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
            .WithSummary("รายการ BFF sessions ของบัญชีตนเอง")
            .WithDescription("คืน metadata ของ session เท่านั้น ไม่คืน ticket หรือ token และกรองด้วย AccountId จาก auth context")
            .Produces<IReadOnlyList<BffSessionAdminView>>();
        account.MapDelete("/me/sessions/{sessionId:guid}", RevokeMySession)
            .WithMetadata(new BffCsrfProtected(), new IdempotencyMutationMarker())
            .AddEndpointFilter<BffCsrfFilter>()
            .RequireAuthorization("identity-bff")
            .WithName("RevokeMySession").WithTags("การเข้าสู่ระบบ")
            .WithSummary("เพิกถอน BFF session ที่เลือก")
            .WithDescription("ตรวจว่า session เป็นของ Account ปัจจุบันแล้ว revoke แบบ idempotent")
            .Produces(StatusCodes.Status204NoContent).ProducesProblem(StatusCodes.Status404NotFound);

        var system = app.MapGroup("/api/v1/system-clients")
            .RequireAuthorization("admin")
            .WithTags("ไคลเอนต์ API");
        system.MapGet("", ListSystemClients)
            .RequirePermission(Keys.UserManage).WithName("ListSystemClients")
            .WithSummary("รายการ SYSTEM clients").WithDescription("คืน metadata ของ SYSTEM clients ใน Admin merchant scope โดยไม่คืน secret");
        system.MapPost("", CreateSystemClient)
            .RequireCsrf().RequirePermission(Keys.UserManage)
            .WithMetadata(new IdempotencyMutationMarker(), new EtagResponseMarker("201"))
            .WithName("CreateSystemClient").WithSummary("สร้าง SYSTEM client")
            .WithDescription("สร้าง Account SYSTEM และ client_credentials client ที่ผูก Merchant/environment เดียว; ต้องส่ง Idempotency-Key");
        system.MapGet("/{clientId:guid}", GetSystemClient)
            .RequirePermission(Keys.UserManage).WithMetadata(new EtagResponseMarker("200"))
            .WithName("GetSystemClient").WithSummary("อ่าน SYSTEM client")
            .WithDescription("คืน client metadata, scopes และ public key policy โดยไม่คืน secret");
        system.MapPatch("/{clientId:guid}", UpdateSystemClient)
            .RequireCsrf().RequirePermission(Keys.UserManage)
            .WithMetadata(new IfMatchMutationMarker("200"), new IdempotencyMutationMarker())
            .WithName("UpdateSystemClient").WithSummary("แก้ไข SYSTEM client")
            .WithDescription("แก้ displayName/status เท่านั้น; Merchant, clientId และ grant type เปลี่ยนไม่ได้; ต้องส่ง If-Match และ Idempotency-Key");
        system.MapPut("/{clientId:guid}/access", ReplaceSystemClientAccess)
            .RequireCsrf().RequirePermission(Keys.SettingsManage)
            .WithMetadata(new IfMatchMutationMarker("200"), new IdempotencyMutationMarker())
            .WithName("ReplaceSystemClientAccess").WithSummary("แทนที่สิทธิ์ SYSTEM client")
            .WithDescription("แทนที่ scopes หลังตรวจ grant registry และ Account authorization version; ต้องส่ง If-Match และ Idempotency-Key");
        system.MapGet("/{clientId:guid}/keys", ListClientKeys)
            .RequirePermission(Keys.UserManage).WithName("ListSystemClientKeys")
            .WithSummary("รายการ public keys ของ SYSTEM client").WithDescription("คืน public key policy และ validity เท่านั้น");
        system.MapPost("/{clientId:guid}/keys", CreateClientKey)
            .RequireCsrf().RequirePermission(Keys.UserManage)
            .WithMetadata(new IdempotencyMutationMarker(), new EtagResponseMarker("201"))
            .WithName("CreateSystemClientKey").WithSummary("ลงทะเบียน SYSTEM public key")
            .WithDescription("รับเฉพาะ public JWK metadata; private material ถูกปฏิเสธ; ต้องส่ง Idempotency-Key");
        system.MapDelete("/{clientId:guid}/keys/{keyId:guid}", DeleteClientKey)
            .RequireCsrf().RequirePermission(Keys.UserManage)
            .WithMetadata(new IdempotencyMutationMarker())
            .WithName("RevokeSystemClientKey").WithSummary("เพิกถอน SYSTEM public key")
            .WithDescription("เพิกถอน key policy และทำให้ authorization version ของ client ใช้ต่อไม่ได้");
    }

    private static async Task<IResult> IssueToken(
        HttpContext http,
        IIdentityAccessQuery identities,
        CancellationToken cancellationToken)
    {
        var request = Microsoft.AspNetCore.OpenIddictServerAspNetCoreHelpers.GetOpenIddictServerRequest(http);
        if (request is null)
            return Results.NotFound();

        if (string.Equals(request.GrantType, OpenIddictConstants.GrantTypes.ClientCredentials,
                StringComparison.Ordinal))
        {
            var resolved = string.IsNullOrWhiteSpace(request.ClientId)
                ? null
                : await identities.FindSystemClientAsync(request.ClientId, cancellationToken);
            if (resolved is null || resolved.Account.Status != AccountStatus.Active
                || resolved.Client.Status != SystemClientStatus.Active)
                return InvalidClient(http);

            var identity = new ClaimsIdentity(
                OpenIddictServerAspNetCoreDefaults.AuthenticationScheme,
                nameType: "sub",
                roleType: "role");
            identity.SetClaim(OpenIddictConstants.Claims.Subject, resolved.Account.Id.ToString("D"));
            identity.SetClaim("account_type", resolved.Account.AccountType.ToString().ToUpperInvariant());
            identity.SetClaim("authz_version", resolved.Account.AuthorizationVersion.ToString());
            identity.SetClaim("client_id", resolved.Client.ClientId);
            identity.SetClaim("merchant_id", resolved.Client.MerchantId.ToString("D"));
            identity.SetDestinations(_ => [OpenIddictConstants.Destinations.AccessToken]);
            return Results.SignIn(new ClaimsPrincipal(identity),
                authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        }

        return Results.SignIn(new ClaimsPrincipal(new ClaimsIdentity(
            OpenIddictServerAspNetCoreDefaults.AuthenticationScheme)),
            authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    private static IResult InvalidClient(HttpContext http)
    {
        http.Response.Headers["WWW-Authenticate"] = "Basic realm=\"oauth\"";
        return Results.Json(
            new { error = OpenIddictConstants.Errors.InvalidClient },
            statusCode: StatusCodes.Status401Unauthorized,
            contentType: "application/json");
    }

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
            jwks_uri = $"{issuer}/.well-known/jwks.json",
            response_types_supported = new[] { "code" },
            grant_types_supported = new[] { "authorization_code", "refresh_token", "client_credentials" },
            code_challenge_methods_supported = new[] { "S256" },
            token_endpoint_auth_methods_supported = new[] { "private_key_jwt" },
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

    private static IResult Authorize(HttpContext http)
    {
        var request = Microsoft.AspNetCore.OpenIddictServerAspNetCoreHelpers.GetOpenIddictServerRequest(http);
        if (request is null || string.IsNullOrWhiteSpace(request.RedirectUri))
            return Results.Json(new { error = OpenIddictConstants.Errors.InvalidRequest }, statusCode: 400);
        if (http.User.Identity?.IsAuthenticated != true)
            return Results.Challenge(authenticationSchemes: ["IdentityWorkforceMicrosoft"]);

        var subject = http.User.FindFirstValue(OpenIddictConstants.Claims.Subject)
            ?? http.User.FindFirstValue("sub");
        if (string.IsNullOrWhiteSpace(subject))
            return Results.Json(new { error = OpenIddictConstants.Errors.LoginRequired }, statusCode: 401);
        var identity = new ClaimsIdentity(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        identity.SetClaim(OpenIddictConstants.Claims.Subject, subject);
        identity.SetClaim(OpenIddictConstants.Claims.Name, http.User.Identity?.Name ?? subject);
        identity.SetScopes(request.GetScopes());
        identity.SetResources(request.GetResources());
        identity.SetDestinations(_ => [OpenIddictConstants.Destinations.AccessToken]);
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
        HttpContext http, IIdentityAccessAdminStore store, CancellationToken cancellationToken)
    {
        var accountId = GetAccountId(http.User);
        return accountId is null
            ? Results.Unauthorized()
            : Results.Ok(await store.ListBffSessionsAsync(accountId.Value, cancellationToken));
    }

    private static async Task<IResult> RevokeMySession(
        Guid sessionId, HttpContext http, IIdentityAccessAdminStore store, CancellationToken cancellationToken)
    {
        var accountId = GetAccountId(http.User);
        if (accountId is null)
            return Results.Unauthorized();
        await store.RevokeBffSessionAsync(accountId.Value, sessionId, cancellationToken);
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
            body.AuditReference), cancellationToken);
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

    private static IResult BeginEmployeeLogin(
        HttpContext http,
        IdentityAccessProviders providers,
        IOptions<IdentityAccessOptions> options,
        string? returnTo = null)
    {
        return BeginLogin(
            http,
            providers,
            "employees",
            NormalizeReturnTo(returnTo),
            properties => properties.Items["identity.realm"] = "workforce",
            options.Value.WorkforceIssuer);
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

    private static IResult GetSession(HttpContext http, BffSessionManager manager)
    {
        var token = manager.ReadSessionToken(http);
        if (string.IsNullOrEmpty(token))
            return Results.Unauthorized();

        var session = http.Features.Get<BffSessionContext>();
        if (session is null)
            return Results.Unauthorized();

        return Results.Ok(new
        {
            accountId = session.Ticket.AccountId,
            clientId = session.Ticket.ClientId,
            merchantId = session.ProtectedTicket.MerchantId,
            issuedAt = session.Ticket.IssuedAt,
            expiresAt = session.Ticket.ExpiresAt,
        });
    }

    internal static async Task<IResult> RefreshSession(
        HttpContext http,
        BffSessionManager manager,
        IBffSessionStore sessions,
        IIdentityAccessQuery identities,
        CancellationToken cancellationToken,
        OpenIddictRefreshTokenRotator? rotator = null)
    {
        var current = await FindCurrentSessionAsync(http, manager, sessions, cancellationToken);
        if (current is null)
            return Results.Unauthorized();

        var account = await identities.FindAccountAsync(current.Ticket.AccountId, cancellationToken);
        if (account is null || account.Status != AccountStatus.Active)
            return Results.Unauthorized();
        if (string.IsNullOrWhiteSpace(current.ProtectedTicket.RefreshToken))
            return Results.Problem(
                statusCode: StatusCodes.Status401Unauthorized,
                title: "The BFF session cannot be refreshed.",
                extensions: new Dictionary<string, object?> { ["code"] = "refresh_unavailable" });

        var refreshToken = current.ProtectedTicket.RefreshToken;
        if (rotator is not null)
        {
            refreshToken = await rotator.RotateAsync(refreshToken, cancellationToken);
            if (refreshToken is null)
                return Results.Unauthorized();
        }

        var replacement = await manager.RotateAsync(
            current.Ticket,
            account,
            current.ProtectedTicket.AccessToken,
            refreshToken,
            current.ProtectedTicket.MerchantId,
            current.ProtectedTicket.ReturnTo,
            cancellationToken);
        manager.WriteCookies(http, replacement.SessionToken, replacement.CsrfToken);
        return Results.Ok(new { expiresAt = replacement.Ticket.ExpiresAt });
    }

    internal static async Task<IResult> SelectMerchantContext(
        HttpContext http,
        MerchantContextRequest request,
        BffSessionManager manager,
        IBffSessionStore sessions,
        IIdentityAccessQuery identities,
        CancellationToken cancellationToken)
    {
        if (request.MerchantId == Guid.Empty)
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["merchantId"] = ["A non-empty MerchantId is required."],
            });

        var current = await FindCurrentSessionAsync(http, manager, sessions, cancellationToken);
        if (current is null)
            return Results.Unauthorized();

        var account = await identities.FindAccountAsync(current.Ticket.AccountId, cancellationToken);
        var authorization = account is null
            ? null
            : await identities.ResolveAuthorizationAsync(
                account.Id, request.MerchantId, null, cancellationToken);
        if (account is null || account.Status != AccountStatus.Active || authorization?.MerchantId != request.MerchantId)
            return Results.Forbid();

        var replacement = await manager.RotateAsync(
            current.Ticket,
            account,
            current.ProtectedTicket.AccessToken,
            current.ProtectedTicket.RefreshToken,
            request.MerchantId,
            current.ProtectedTicket.ReturnTo,
            cancellationToken);
        manager.WriteCookies(http, replacement.SessionToken, replacement.CsrfToken);
        return Results.Ok(new { merchantId = request.MerchantId, expiresAt = replacement.Ticket.ExpiresAt });
    }

    internal static async Task<IResult> Logout(
        HttpContext http,
        BffSessionManager manager,
        IBffSessionStore sessions,
        CancellationToken cancellationToken,
        IOpenIddictTokenManager? tokenManager = null)
    {
        var current = await FindCurrentSessionAsync(http, manager, sessions, cancellationToken);
        if (current is not null)
        {
            if (tokenManager is not null && !string.IsNullOrWhiteSpace(current.ProtectedTicket.RefreshToken))
            {
                var token = await tokenManager.FindByReferenceIdAsync(
                    current.ProtectedTicket.RefreshToken, cancellationToken);
                if (token is not null)
                    await tokenManager.TryRevokeAsync(token, cancellationToken);
            }
            await sessions.RevokeAsync(current.Ticket, DateTime.UtcNow, cancellationToken);
        }
        manager.ClearCookies(http);
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
            accountType = account.AccountType.ToString().ToUpperInvariant(),
            displayName = account.DisplayName,
            status = account.Status.ToString().ToUpperInvariant(),
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

    private static async Task<BffSessionContext?> FindCurrentSessionAsync(
        HttpContext http,
        BffSessionManager manager,
        IBffSessionStore sessions,
        CancellationToken cancellationToken)
    {
        var raw = manager.ReadSessionToken(http);
        if (string.IsNullOrEmpty(raw))
            return null;
        var ticket = await sessions.FindByHashAsync(BffSessionManager.Hash(raw), cancellationToken);
        var protectedTicket = ticket is null ? null : manager.Unprotect(ticket);
        return ticket is null || protectedTicket is null
            ? null
            : new BffSessionContext(ticket, protectedTicket);
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

internal sealed record MerchantContextRequest(Guid MerchantId);
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
