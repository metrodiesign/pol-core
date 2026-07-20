using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.OpenApi;
using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure;
using BuildingBlocks.Infrastructure.Observability;
using BuildingBlocks.Infrastructure.Persistence;
using BuildingBlocks.Infrastructure.Vault;
using BuildingBlocks.Web;
using Admins.Application;
using Admins.Application.Roles;
using Admins.Application.Users;
using Admins.Domain.Roles;
using Admins.Domain.Users;
using Admins.Infrastructure;
using Carts.Application;
using Carts.Infrastructure;
using Checkouts.Application;
using Checkouts.Infrastructure;
using Mediator;
using Divisions.Application;
using Levels.Application;
using Offices.Application;
using Positions.Application;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpLogging;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Orders.Application;
using Orders.Infrastructure;
using Payments.Application.CreateSession;
using Payments.Application.HandlePspWebhook;
using Payments.Application.StartRedirect;
using Payments.Domain.Psp;
using Payments.Infrastructure;
using Payments.Infrastructure.Psp;
using Merchants.Application;
using Merchants.Domain;
using Merchants.Infrastructure;
using Products.Application;
using Products.Infrastructure;
using Merchants.Application.GetMerchant;
using Merchants.Application.ProvisionMerchant;
// L6 (hierarchical-naming): Admins.*.Users/.Roles and Merchants.*.Users/.Roles now share bare names (User,
// Session, IRoleRepository, ...). This host file is composition-root-neutral and needs both planes, so every
// colliding name is imported by explicit alias rather than a blanket `using` — a module never aliases its own
// types (L6), but this file is neither module's own. Role/permission CRUD + catalog types (rf2) are UNIFIED in
// Iam.Application/Iam.Domain — both consoles now reference the SAME Role/RoleStatus/Keys/RoleListItem/
// CreateRoleCommand/etc, so no per-side alias is needed for those anymore.
using PhotoValidation = Merchants.Application.Users.PhotoValidation;
using ApproveCommand = Merchants.Application.Users.ApproveCommand;
using RejectCommand = Merchants.Application.Users.RejectCommand;
using SubmitRegistrationCommand = Merchants.Application.Users.SubmitRegistrationCommand;
using IMerchantSessionStore = Merchants.Application.Users.ISessionStore;
using IMerchantAuthAuditWriter = Merchants.Application.Users.IAuthAuditWriter;
using IMerchantRoleRepository = Merchants.Application.Users.Roles.IRoleRepository;
using MerchantSetRolesCommand = Merchants.Application.Users.SetRolesCommand;
using MerchantAuthAudit = Merchants.Domain.Users.AuthAudit;
using MerchantAuthEventType = Merchants.Domain.Users.AuthEventType;
using Iam.Application.Roles;
using Iam.Domain.Permissions;
// Microsoft.OpenApi also declares a `Scope` type — alias to disambiguate the two call sites that need
// Iam's Platform/Merchant enum (GetPermissionCatalogQuery).
using Scope = Iam.Domain.Permissions.Scope;
using Iam.Domain.Roles;
using Api;
using Api.Admins;
using Api.Iam;
using Api.Merchants;
using Api.Persistence;
using Api.Webhooks;
using Persistence.ControlPlane;
using Persistence.MerchantRuntime;
using Persistence.MerchantUsers;
using Persistence.Provisioning;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Scalar.AspNetCore;
using SharedKernel;

var builder = WebApplication.CreateBuilder(args);

builder.AddJsonConsoleLogging();

// Fail fast on captive scope/lifetime mistakes (MerchantGuardBehavior + IActorContext are Scoped, PLAN #7).
if (builder.Environment.IsDevelopment())
{
    builder.Host.UseDefaultServiceProvider(o =>
    {
        o.ValidateScopes = true;
        o.ValidateOnBuild = true;
    });
}

// Mediator: source-gen discovers every handler across referenced module assemblies. Scoped so
// handlers can depend on the Scoped DbContext/repositories.
builder.Services.AddMediator(options => options.ServiceLifetime = ServiceLifetime.Scoped);
builder.Services.AddScoped(typeof(IPipelineBehavior<,>), typeof(MerchantGuardBehavior<,>));

builder.Services.AddBuildingBlocksInfrastructure();
builder.Services.AddSecurityTelemetry(builder.Configuration, applicationName: "Api");

// Single pol_app principal for every runtime cluster (task 8, "1 principal" — RLS is gone, so there is no
// separate pol_admin bypass role to key a second connection against). Each Persistence.* registration
// extension below builds its own DbContext + adapters over this ONE connection string; the write floor (an
// app-layer IWriteAuthorizer, not a DB role) is what still stops a merchant request from writing another
// merchant's rows (WriteAuthorizers.cs).
var appConnString = builder.Configuration.GetConnectionString("App")
    ?? throw new InvalidOperationException("ConnectionStrings:App is required. Set ConnectionStrings__App.");
// REQ-13.3: every host connects as the SAME pol_app login (task 8, 1 principal) — Application Name is the
// one thing that still lets sys.dm_exec_sessions/sys.dm_exec_requests attribute activity to Api vs Worker.
appConnString = new SqlConnectionStringBuilder(appConnString) { ApplicationName = "Api" }.ConnectionString;

// Module entity configurations are discovered from these assemblies at model-build time.
builder.Services.AddSingleton(new ModuleAssemblies(HostModuleAssemblies.All));

builder.Services.Configure<VaultOptions>(builder.Configuration.GetSection(VaultOptions.SectionName));
// Non-secret PSP endpoint/environment config for the real 2C2P + Omise adapters (UseSandbox defaults true).
builder.Services.Configure<PspOptions>(builder.Configuration.GetSection(PspOptions.SectionName));

builder.Services.AddProductsModule();
builder.Services.AddCartModule();
builder.Services.AddCheckoutModule();
builder.Services.AddOrdersModule();
builder.Services.AddPaymentsModule();
builder.Services.AddMerchantsModule();

// The committed appsettings.json ships an App string with a BLANK password (the real secret is injected
// at runtime). Outside Development, fail fast if that injection did not happen — otherwise the host boots
// and only the first request discovers the missing credential. Development may use integrated auth.
if (!builder.Environment.IsDevelopment())
{
    ProvisioningGuards.RequireInjectedCredential(appConnString, "App");
    // The admin BFF login is a confidential OIDC client: the client id + secret must be injected outside
    // Development (the committed config ships blanks/placeholders). A blank/placeholder secret means the
    // runtime secret was never injected — fail fast at boot rather than on the first login (REQ-8.1/8.2).
    ProvisioningGuards.RequireConfidentialClientId(builder.Configuration["Google:Oidc:ClientId"]);
    ProvisioningGuards.RequireConfidentialClientSecret(builder.Configuration["Google:Oidc:ClientSecret"]);
    // The merchant-user OIDC client is a SECOND confidential client. Unlike Admin, a blank MerchantUser ClientId is allowed
    // outside Development — the merchant-user FE is a later slice, so merchant-user login may be intentionally disabled (the
    // scheme is then skipped, REQ-14.2). But IF a ClientId is configured, its secret MUST be injected too, and the
    // id itself must not be a committed placeholder (REQ-14.1/14.2).
    var merchantUserClientId = builder.Configuration["MerchantUser:Oidc:ClientId"];
    if (!string.IsNullOrWhiteSpace(merchantUserClientId))
    {
        ProvisioningGuards.RequireMerchantUserClientId(merchantUserClientId);
        ProvisioningGuards.RequireMerchantUserClientSecret(builder.Configuration["MerchantUser:Oidc:ClientSecret"]);
    }
}

// The 3 runtime clusters + the Provisioning UoW (task 8.5.7), all on the single pol_app connection. The
// write-floor authorizer differs per capability (WriteAuthorizers.cs): an ordinary admin-console request
// (ControlPlaneDbContext), an ordinary merchant request (MerchantUserDbContext/MerchantRuntimeDbContext), or
// the ONE cross-context provisioning writer — a single instance, since ProvisioningCoordinator constructs
// its own context instances per attempt rather than resolving them from this container.
builder.Services.AddControlPlanePersistence(
    appConnString, sp => new ControlPlaneAdminWriteAuthorizer(sp.GetRequiredService<IAdminScope>()));
builder.Services.AddMerchantUserPersistence(
    appConnString, sp => new MerchantRequestWriteAuthorizer(sp.GetRequiredService<IActorContext>()));
builder.Services.AddMerchantRuntimePersistence(
    appConnString, sp => new MerchantRequestWriteAuthorizer(sp.GetRequiredService<IActorContext>()));

// Keyring comes from the DI singleton (options-bound, validated once) — an inline eager
// VaultKeyringFactory.Build here reads builder.Configuration BEFORE deferred test/host config
// sources are applied, which is exactly the CI-only "Vault is not configured" boot crash.
builder.Services.AddProvisioning(appConnString, new ProvisioningSuperWriteAuthorizer());

// Admin identity (control plane: PlatformUsers/assignments/audit) + Merchants identity (data plane:
// MerchantUsers + control-plane ExternalLogins/RegistrationTickets/Profiles/RegistrationAudits) — every
// repository/session-store/audit seam is already bound by AddControlPlanePersistence/AddMerchantUserPersistence
// above; these calls wire only the host-only pieces (scope, session cookies, cross-context compositions).
builder.Services.AddAdminModule();
builder.Services.AddAdminIdentity();

builder.Services.Configure<UserRegistrationOptions>(
    builder.Configuration.GetSection(UserRegistrationOptions.SectionName));
builder.Services.AddMerchantsIdentity();

// Central IAM audit bridge + assignment counter (rf2) — IRoleStore itself comes from
// AddControlPlanePersistence above. Needs IAdminScope/IAuditWriter (AddAdminIdentity) and the two
// count-reader ports (AddControlPlanePersistence/AddMerchantUserPersistence) already bound.
builder.Services.AddIamRoleManagement();

// MerchantUser BFF: a SECOND confidential Google OIDC client (Authorization Code + PKCE) for the server-side merchant-user
// login, fully isolated from the Admin one — distinct "MerchantUserGoogle" scheme + callback + cookie names (REQ-8/9/14).
// Adds the scheme WITHOUT changing the default (JwtBearer stays for merchant); a blank ClientId skips the scheme so a
// half-configured env (the merchant-user FE is a later slice) does not fault the whole host (REQ-14.2). The merchant-user
// session lifetime + cookie posture come from MerchantUser:Session.
builder.Services.Configure<UserOidcOptions>(builder.Configuration.GetSection(UserOidcOptions.SectionName));
builder.Services.Configure<UserSessionOptions>(builder.Configuration.GetSection(UserSessionOptions.SectionName));
builder.Services.AddMerchantUserOidcAuthentication(builder.Configuration, builder.Environment);

// MerchantUser BFF session scheme: authenticate merchant-user requests via the __Host-mch_session cookie and register the
// SINGLE-SCHEME "merchant-user" policy (MerchantUserSession only, T11 — the Bearer fallback is retired). Background
// sweep prunes expired sessions so the control-plane session table does not grow unbounded (REQ-10.4).
builder.Services.AddMerchantUserSessionScheme();
builder.Services.AddHostedService<UserSessionPruneService>();

// Data Protection key ring for the admin OIDC handler (correlation/state/nonce cookies), persisted to the
// control-plane DataProtectionKeys table via the keyed pol_admin context (REQ-8, Tech #5). Lazy — no SQL at boot.
builder.Services.AddAdminDataProtection();

// Admin BFF session lifetime + cookie posture (REQ-3/5/7).
builder.Services.Configure<AdminSessionOptions>(builder.Configuration.GetSection(AdminSessionOptions.SectionName));

// Merchant identity from the authenticated principal (never from the URL — PLAN #4).
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IActorContext, HttpActorContext>();

// Google id-token Bearer is retired for the funnel (T11 — rf1 big-bang, no legacy audience). The
// MerchantUserSession cookie scheme is the explicit default (each protected group still pins its own scheme via
// its policy — the default only matters for UseAuthentication's principal-population pass).
builder.Services.AddAuthentication(UserSessionAuthenticationHandler.SchemeName);

// Admin BFF: confidential Google OIDC client (Authorization Code + PKCE) for the server-side admin login.
// Adds the "Google" OIDC + "oidc-noop" sign-in schemes WITHOUT changing the default set above.
builder.Services.Configure<OidcOptions>(builder.Configuration.GetSection(OidcOptions.SectionName));
builder.Services.AddAdminOidcAuthentication(builder.Configuration, builder.Environment);

// Admin BFF session scheme: authenticate every /api/v1/admins/* request via the __Host-adm_session cookie and
// REDEFINE the "admin" authorization policy to pin it — retiring the Bearer "admin" audience (REQ-4/5/9/10).
builder.Services.AddPlatformUserSessionScheme();

// Background sweep: delete sessions past their absolute expiry so the store does not grow unbounded (REQ-11.5).
builder.Services.AddHostedService<SessionPruneService>();

// CORS for the separate browser SPA frontends (both allowlisted origins from Cors:AllowedOrigins).
builder.Services.AddPolCors(builder.Configuration);

// OpenAPI document so the SPA teams have a machine-readable contract (served in Development only). The
// document also declares the two auth schemes (merchant-user session cookie + admin session cookie) and tags
// each operation with the scheme its authorization policy requires, so other teams can authenticate straight
// from the Scalar reference UI.
builder.Services.AddOpenApi(options =>
{
    options.AddSchemaTransformer((schema, context, _) =>
    {
        // PspCode has a custom JsonConverter the schema generator can't introspect, so it would emit an
        // empty schema. Describe the real wire shape: the stable string codes from the PspCodes mapping.
        if (context.JsonTypeInfo.Type == typeof(Code))
        {
            schema.Type = JsonSchemaType.String;
            schema.Enum = Enum.GetValues<Code>().Select(p => (JsonNode)JsonValue.Create(p.ToCode())).ToList();
        }
        return Task.CompletedTask;
    });

    // Operation-level: SFS endpoints read page/limit/filters/sort/search from the raw query string, so ASP.NET
    // emits no parameters for them. Declare them wherever the SfsQueryParamsMarker is present (REQ-13).
    options.AddOperationTransformer((operation, context, _) =>
    {
        if (context.Description.ActionDescriptor.EndpointMetadata.OfType<SfsQueryParamsMarker>().Any())
            SfsOpenApi.AddQueryParameters(operation);
        return Task.CompletedTask;
    });

    // Document-level: title/description + the security schemes other teams pick from Scalar's auth dropdown,
    // plus the per-operation security requirement each route's authorization policy implies.
    options.AddDocumentTransformer((document, context, _) =>
    {
        document.Info.Title = "pol-core API";
        document.Info.Version = "v1";
        document.Info.Description =
            "Captive payment orchestration (redirect-only, PCI SAQ A). Merchant storefront surface + Admin BFF + MerchantUser BFF.";

        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes["AdminSession"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey,
            In = ParameterLocation.Cookie,
            // Scalar/OpenAPI serve in Development only, where the default host is dev HTTP and the handler
            // writes the non-__Host cookie. Document that name, not the prod one, so admins testing in /scalar
            // see the cookie they actually have.
            Name = SessionCookies.SessionCookieNameDevHttp,
            Description = "Admin BFF session cookie issued by the OIDC login flow (GET /api/v1/admins/auth/login). "
                + "Set automatically in the browser. Production (HTTPS) uses the `__Host-adm_session` name.",
        };
        document.Components.SecuritySchemes["MerchantUserSession"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey,
            In = ParameterLocation.Cookie,
            Name = UserSessionCookies.SessionCookieNameDevHttp,
            Description = "Merchant-user BFF session cookie issued by the OIDC login flow (GET /api/v1/merchants/users/auth/login). "
                + "Set automatically in the browser. Production (HTTPS) uses the `__Host-mch_session` name (T11 — "
                + "single-scheme, the legacy Bearer fallback is retired).",
        };

        // Per-operation: attach the scheme each route's authorization policy requires so Scalar shows the right
        // auth on the right endpoint (merchant-user -> MerchantUserSession, admin -> AdminSession). The host
        // document is passed so the requirement serialises as a $ref into components.securitySchemes. Anonymous
        // routes (order summary link, admin login, webhook) carry no requirement.
        var schemeByRoute = new Dictionary<(string Path, string Method), string>();
        foreach (var d in context.ApplicationServices
                     .GetRequiredService<IApiDescriptionGroupCollectionProvider>()
                     .ApiDescriptionGroups.Items.SelectMany(g => g.Items))
        {
            var schemeId = SecuritySchemeForEndpoint(d.ActionDescriptor.EndpointMetadata);
            if (schemeId is not null && d.RelativePath is not null && d.HttpMethod is not null)
            {
                // RelativePath keeps route constraints ("{cartId:guid}"); the OpenAPI path strips them
                // ("{cartId}"). Normalise so the two keys match.
                var path = RouteConstraintRegex().Replace("/" + d.RelativePath.TrimStart('/'), "{$1}");
                schemeByRoute[(path, d.HttpMethod.ToUpperInvariant())] = schemeId;
            }
        }
        foreach (var (pathKey, pathItem) in document.Paths)
        {
            if (pathItem.Operations is null)
                continue;
            foreach (var (method, operation) in pathItem.Operations)
                if (schemeByRoute.TryGetValue((pathKey, method.Method.ToUpperInvariant()), out var schemeId))
                {
                    operation.Security ??= [];
                    operation.Security.Add(new OpenApiSecurityRequirement
                    {
                        [new OpenApiSecuritySchemeReference(schemeId, document)] = [],
                    });
                }
        }
        return Task.CompletedTask;
    });
});

// merchant-user routes gate on the "merchant-user" policy (session cookie, T11 single-scheme); admin routes on
// "admin" (session cookie). AllowAnonymous endpoints and the unauthenticated webhook get no security requirement
// in the doc. Assumption: the only IAuthorizeData on an endpoint is the named policy from .RequireAuthorization(...).
// RequirePlatformUserTier/RequirePermission use endpoint filters + WithMetadata (Api.Iam.PermissionAuthorization),
// NOT IAuthorizeData, so exactly one non-empty policy is present and LastOrDefault is unambiguous. The
// policy->scheme mapping itself lives in AuthPolicyScheme (rf2) — shared with the boot parity guard below so the
// two can never drift apart.
static string? SecuritySchemeForEndpoint(IEnumerable<object> metadata)
{
    if (metadata.OfType<IAllowAnonymous>().Any())
        return null;
    var policy = metadata.OfType<IAuthorizeData>()
        .Select(a => a.Policy)
        .LastOrDefault(p => !string.IsNullOrEmpty(p));
    return AuthPolicyScheme.For(policy)?.SchemeId;
}

// PspCode crosses the wire as its stable code ("2c2p"/"omise") via the domain's PspCodes mapping —
// not as an int or the C# member name. An unknown code fails body binding -> 400.
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new PspCodeJsonConverter());
    o.SerializerOptions.Converters.Add(new MoneyJsonConverter());
});

// Cross-cutting HTTP hardening: RFC7807 errors, split liveness/readiness probes, webhook flood protection.
builder.Services.AddProblemDetailsHandling();
builder.Services.AddReadinessHealthChecks(appConnString);
builder.Services.AddWebhookRateLimiter();
builder.Services.AddAdminAuthRateLimiter();
builder.Services.AddMerchantUserAuthRateLimiter();

// Dev-only HTTP req/res logging incl. response headers (esp. Location on a 302 — see where the OIDC callback /
// register redirect actually sends the browser). Logs headers + bodies would leak PII/photo bytes, so this stays
// Development-only and headers-only (no body). Location is not in the default response-header allowlist, so add it.
if (builder.Environment.IsDevelopment())
    builder.Services.AddHttpLogging(o =>
    {
        o.LoggingFields = HttpLoggingFields.RequestPropertiesAndHeaders | HttpLoggingFields.ResponsePropertiesAndHeaders;
        o.ResponseHeaders.Add("Location");
        o.CombineLogs = true; // one merged log entry per request instead of separate start/finish lines
    });

var app = builder.Build();

// Dev convenience: auto-apply pending EF migrations at boot so a freshly merged migration can't leave the
// local DB desynced from the code (the symptom is a runtime "Invalid object name" -> resolve-failed login).
// The runtime pol_app/pol_admin logins have no DDL rights, so this runs on the privileged Migrator
// connection from the gitignored appsettings.Development.json. Absent -> skip with a warning, never crash a
// healthy boot. Prod migrates out-of-band as sa via docker/migrate-entrypoint.sh, NOT here.
if (app.Environment.IsDevelopment()
    && app.Configuration.GetConnectionString("Migrator") is { Length: > 0 } migratorConn)
{
    var options = new DbContextOptionsBuilder<PolDbContext>().UseSqlServer(migratorConn).Options;
    using var migrateDb = new PolDbContext(options, app.Services.GetRequiredService<ModuleAssemblies>());
    await migrateDb.Database.MigrateAsync();
    app.Logger.LogInformation("Applied pending EF migrations (Development, Migrator connection).");
}
else if (app.Environment.IsDevelopment())
{
    app.Logger.LogWarning("ConnectionStrings:Migrator not set — skipping Development auto-migrate.");
}

// Fail-fast: build the vault keyring now so a missing/short/invalid master key crash-loops the host at
// boot instead of surfacing only on the first reveal. ValidateOnBuild does NOT run factory-registered
// singletons, so this explicit resolve is what delivers the boot-time custody guarantee.
_ = app.Services.GetRequiredService<VaultKeyring>();

// Outside Development, the OIDC correlation cookies must ride a persisted, shared key ring — never the
// framework's default ephemeral one (REQ-8.2). Assert it now so a misconfigured key store crash-loops at boot.
if (!app.Environment.IsDevelopment())
    AdminDataProtection.RequirePersistentDataProtection(app.Services);

// REQ-5.4 fail-closed signal: outside Development, an empty admin allowlist means Super-admin bootstrap is
// disabled — the first-admin self-provision will be denied. Existing admins are unaffected (the allowlist is
// a bootstrap-only gate, REQ-5.7), so this warns rather than crash-loops a healthy host.
if (!app.Environment.IsDevelopment()
    && (app.Configuration.GetSection("AdminAllowlist:Subjects").Get<string[]>() ?? []).Length == 0)
{
    app.Logger.LogWarning(
        "AdminAllowlist:Subjects is empty — Super-admin bootstrap is disabled (first-admin self-provision will " +
        "be denied, fail-closed). Set AdminAllowlist__Subjects__0 to bootstrap the first Super admin.");
}

// Forwarded headers FIRST so every downstream middleware (auth, and the OIDC redirect_uri builder) sees the
// browser-facing host/scheme, not this process's. The admin SPA dev server proxies /api/v1/admins/* here, so the OIDC
// redirect_uri must be the SPA origin (e.g. localhost:5200) to match the registered Google redirect URI; the
// same applies to a TLS-terminating reverse proxy in prod (scheme must read https). Default trust = loopback
// only, which covers the localhost dev proxy. A containerized prod proxy connects from the (non-loopback)
// docker/private network, and .NET only honours forwarded headers from a TRUSTED peer — otherwise it silently
// ignores X-Forwarded-* and the redirect_uri keeps this process's internal host (Google then rejects login with
// redirect_uri_mismatch). Trust the real proxy ADDITIVELY from config so the localhost dev proxy keeps working:
// ForwardedHeaders:KnownNetworks = CIDRs (e.g. the docker subnet "172.18.0.0/16"), KnownProxies = single IPs.
// Both empty (the default) = loopback only.
var forwardedHeaders = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedHost | ForwardedHeaders.XForwardedProto,
};
foreach (var cidr in app.Configuration.GetSection("ForwardedHeaders:KnownNetworks").Get<string[]>() ?? [])
    if (!string.IsNullOrWhiteSpace(cidr)) // an unset `${VAR:-}` env expands to a blank entry — skip, don't Parse("")
        forwardedHeaders.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(cidr.Trim()));
foreach (var proxy in app.Configuration.GetSection("ForwardedHeaders:KnownProxies").Get<string[]>() ?? [])
    if (!string.IsNullOrWhiteSpace(proxy))
        forwardedHeaders.KnownProxies.Add(System.Net.IPAddress.Parse(proxy.Trim()));
app.UseForwardedHeaders(forwardedHeaders);

// Dev-only: log each request + its response headers (Location on 302, etc.). After UseForwardedHeaders so the
// logged Host is the browser-facing one; early so it wraps the endpoints and captures the final response.
if (app.Environment.IsDevelopment())
    app.UseHttpLogging();

// Order matters: correlation id OUTERMOST so the logging scope is still active when the exception handler
// logs a failure (the scope is popped as the exception unwinds, so it must wrap UseExceptionHandler); the
// exception handler then wraps auth + the endpoints; rate limiter before the mapped endpoints run.
app.UseCorrelationId();
app.UseExceptionHandler();
// Render framework-generated bare status codes (401/403, unmatched-route 404) as RFC7807 ProblemDetails
// too, so every error shares one body shape. AddProblemDetails() above supplies the writer.
app.UseStatusCodePages();

// CORS before auth so a browser preflight (OPTIONS) is answered without an auth challenge. The per-request
// policy (admin-credentialed on /api/v1/admins/*, merchant default elsewhere) is chosen by PolCorsPolicyProvider.
app.UsePolCors();

app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

// Merchant-user resolution is no longer a middleware: MerchantUserSessionAuthenticationHandler authenticates the
// __Host-mch_session cookie during authorization (the single-scheme "merchant-user" policy pins that scheme, T11 —
// the Bearer fallback is retired), re-resolves the MerchantUser READ-ONLY by id, and binds IMerchantUserScope + the
// ambient `merchant_id` claim per request.

// Admin resolution is no longer a middleware: PlatformUserSessionAuthenticationHandler authenticates the
// __Host-adm_session cookie during authorization (the "admin" policy pins that scheme), re-resolves the admin
// READ-ONLY by id, and binds IAdminScope per request (REQ-9). First-login bootstrap/bind happens at the OIDC
// callback (AdminCallbackResolver), not per request.

// Liveness (process only) + readiness (DB + vault), anonymous, minimal body — no topology leak.
app.MapPolHealthChecks();

// The API contract for the SPA teams. Development only — the prod contract is not published publicly.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    // Scalar reference UI over /openapi/v1.json — anonymous like the health checks. Other teams browse the
    // grouped endpoints and try them with the right auth straight from /scalar. Dev-only, same as MapOpenApi.
    // No preferred scheme: Scalar auto-selects each operation's own security (Bearer for merchant routes,
    // Session for admin routes) instead of defaulting every endpoint to one.
    app.MapScalarApiReference(options => options.WithTitle("pol-core API"));
}

// --- /api/v1 route scheme (api-route-scheme REQ-1/2) ---
// One versioned root group. Every endpoint's path is /api/v1/{area}/... — the version segment is FIRST and the
// second segment is the domain AREA (plural noun), never the audience (audience stays enforced per endpoint via
// RequireAuthorization, REQ-3.2). Handlers/policies/contracts are unchanged from the pre-migration flat routes;
// only the base path moves. Data-plane endpoints map their area path DIRECTLY on this group (an explicit
// "/products", "/carts/..." pattern) rather than via a nested MapGroup with an empty-string root pattern — the
// latter renders a trailing-slash canonical path ("/api/v1/products/"), which the clean-path intent forbids
// (REQ-1.4). admins/merchants-users DO use a MapGroup, because it binds their endpoint FILTERS once for the whole
// surface; the admins-root create and the two admin-provisioned /merchants endpoints (moved out of /admins, D9)
// carry the area path + their filters per-endpoint instead. Infra (health, openapi, scalar) is mapped ABOVE this
// and stays OUTSIDE /api/v1 (REQ-4).
var api = app.MapGroup("/api/v1");

// Webhook = source of truth. Routed by the trusted PSP connection id (NOT merchant/PSP parsed from the
// URL before the signature is verified — security rules). The raw body + signature header are handed
// to the handler, which verifies -> claims idempotency -> fetches-to-confirm -> transitions -> enqueues
// PaymentPaid, all inside one transaction.
api.MapPost("/webhooks/{pspConnectionId:guid}", async (
    Guid pspConnectionId,
    HttpRequest request,
    IWebhookMerchantResolver merchantResolver,
    IActorScope actorScope,
    IMediator mediator,
    CancellationToken ct) =>
{
    using var reader = new StreamReader(request.Body);
    var rawPayload = await reader.ReadToEndAsync(ct);
    var signature = request.Headers["X-Signature"].ToString();

    // Resolve the merchant from the trusted connection id BEFORE any merchant-scoped work, then bind it so
    // every query in the handler runs under the right RLS SESSION_CONTEXT. Unknown id -> 404 (no leak).
    var merchantId = await merchantResolver.ResolveMerchantAsync(pspConnectionId, ct);
    if (merchantId is null)
        return Results.Problem(statusCode: StatusCodes.Status404NotFound);

    using var actorBinding = actorScope.Begin(merchantId.Value);

    var result = await mediator.Send(new HandlePspWebhookCommand(pspConnectionId, rawPayload, signature), ct);
    return result.Outcome == WebhookOutcome.Rejected
        ? Results.Problem(statusCode: StatusCodes.Status401Unauthorized)
        : Results.Ok(new WebhookResponse(result.Outcome.ToString()));
}).RequireRateLimiting(RateLimiting.PolicyName)
    .WithTags("Webhooks")
    .WithName("HandlePspWebhook")
    .WithSummary("PSP webhook callback")
    .WithDescription("Verify the PSP signature, claim idempotency, confirm the payment and emit PaymentPaid. Routed by the trusted connection id; unknown id -> 404, bad signature -> 401.")
    .Produces<WebhookResponse>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status429TooManyRequests);

// T11 (rf1 big-bang): the merchant-Bearer fallback is retired, so every write endpoint gates on the single-scheme
// "merchant-user" policy + its permission unconditionally — the former MerchantUser:EnforcePermissionsOnWrites
// toggle (a transitional un-gated Bearer state) no longer has a Bearer path to fall back to, so it is deleted.

// Merchant-facing convenience endpoints (merchant comes from the authenticated principal via IActorContext).
var createProduct = api.MapPost("/products", async (
    CreateProductRequest body,
    IActorContext actor,
    IMediator mediator,
    CancellationToken ct) =>
{
    var id = await mediator.Send(
        new CreateProductCommand(actor.MerchantId, body.Name, body.Price), ct);
    return TypedResults.Ok(new CreateProductResponse(id));
});
createProduct.RequireAuthorization("merchant-user").RequirePermission(Keys.ProductCreate)
    .WithTags("Products")
    .WithName("CreateProduct")
    .WithSummary("Create a product")
    .WithDescription("Create a catalog product for the authenticated merchant. Requires the merchant-user policy + product.create.")
    .Produces<CreateProductResponse>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status403Forbidden);

// GET /products — the merchant-scoped SFS exemplar. Merchant comes from the principal (IActorContext), NEVER the
// client; the query is IMerchantScoped so the merchant guard + RLS floor confine every row to the bound merchant, and
// SFS can only narrow within it (no whitelist exposes merchantId). productFilters is the optional typed surface.
api.MapGet("/products", async (HttpContext http, IActorContext actor, IMediator mediator, CancellationToken ct) =>
{
    var p = SfsQueryParser.Parse(http.Request.Query);
    var result = await mediator.Send(new ListProductsQuery
    {
        MerchantId = actor.MerchantId,
        Page = p.Page, Limit = p.Limit, Filters = p.Filters, Sort = p.Sort, Search = p.Search,
        ProductFilters = ProductFilterDto.Parse(http.Request.Query["productFilters"]),
    }, ct);
    return Results.Ok(result);
})
    .RequireAuthorization("merchant-user")
    .WithMetadata(new SfsQueryParamsMarker())
    .WithTags("Products")
    .WithName("ListProducts")
    .WithSummary("List products")
    .WithDescription("Paged catalog for the authenticated merchant. Supports SFS (page, limit, filters, sort, search) plus a typed productFilters object.")
    .Produces<PagedResult<ProductListItem>>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status401Unauthorized);

// Cart — open, add/merge lines, review, adjust, clear. Merchant comes from the principal; the commands are
// IMerchantScoped so RLS + the merchant guard confine every cart to the bound merchant.
api.MapPost("/carts", async (IActorContext actor, IMediator mediator, CancellationToken ct) =>
{
    var id = await mediator.Send(new CreateCartCommand(actor.MerchantId), ct);
    return TypedResults.Ok(new CreateCartResponse(id));
}).RequireAuthorization("merchant-user")
    .WithTags("Cart")
    .WithName("CreateCart")
    .WithSummary("Open a cart")
    .WithDescription("Open a new empty cart for the authenticated merchant.")
    .Produces<CreateCartResponse>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status401Unauthorized);

api.MapPost("/carts/{cartId:guid}/items", async (
    Guid cartId, AddItemToCartRequest body, IActorContext actor, IMediator mediator, CancellationToken ct) =>
{
    // The unit price is the catalog's, NEVER the client's: look the product up first and price the line
    // from it (the cart is "selected plans + quote", reference 2.4). Unknown/inactive product -> 400.
    var product = await mediator.Send(new GetProductByIdQuery(actor.MerchantId, body.ProductId), ct);
    if (product is null || !product.IsActive)
        return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Unknown or inactive product.");

    var result = await mediator.Send(new AddItemToCartCommand(
        cartId, actor.MerchantId, body.ProductId, body.Quantity, product.Price), ct);
    return Results.Ok(result);
}).RequireAuthorization("merchant-user")
    .WithTags("Cart")
    .WithName("AddCartItem")
    .WithSummary("Add an item to a cart")
    .WithDescription("Add a catalog product line to the cart, priced from the catalog. Unknown or inactive product -> 400.")
    .Produces<AddItemResult>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status401Unauthorized);

api.MapGet("/carts/{cartId:guid}", async (
    Guid cartId, IActorContext actor, IMediator mediator, CancellationToken ct) =>
{
    var view = await mediator.Send(new GetCartQuery(cartId, actor.MerchantId), ct);
    return view is null ? Results.NotFound() : Results.Ok(view);
}).RequireAuthorization("merchant-user")
    .WithTags("Cart")
    .WithName("GetCart")
    .WithSummary("Review a cart")
    .WithDescription("Return the cart with its lines and quoted subtotal. Unknown cart -> 404.")
    .Produces<CartView>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status401Unauthorized);

api.MapDelete("/carts/{cartId:guid}/items/{productId:guid}", async (
    Guid cartId, Guid productId, IActorContext actor, IMediator mediator, CancellationToken ct) =>
{
    var view = await mediator.Send(new RemoveItemFromCartCommand(cartId, actor.MerchantId, productId), ct);
    return TypedResults.Ok(view);
}).RequireAuthorization("merchant-user")
    .WithTags("Cart")
    .WithName("RemoveCartItem")
    .WithSummary("Remove a cart line")
    .WithDescription("Remove a product line from the cart and return the updated cart.")
    .Produces<CartView>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status401Unauthorized);

api.MapPut("/carts/{cartId:guid}/items/{productId:guid}", async (
    Guid cartId, Guid productId, SetCartItemQuantityRequest body, IActorContext actor, IMediator mediator, CancellationToken ct) =>
{
    var view = await mediator.Send(new SetCartItemQuantityCommand(cartId, actor.MerchantId, productId, body.Quantity), ct);
    return TypedResults.Ok(view);
}).RequireAuthorization("merchant-user")
    .WithTags("Cart")
    .WithName("SetCartItemQuantity")
    .WithSummary("Set a cart line quantity")
    .WithDescription("Adjust the quantity of a product line and return the updated cart.")
    .Produces<CartView>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status401Unauthorized);

api.MapPost("/carts/{cartId:guid}/clear", async (
    Guid cartId, IActorContext actor, IMediator mediator, CancellationToken ct) =>
{
    var view = await mediator.Send(new ClearCartCommand(cartId, actor.MerchantId), ct);
    return TypedResults.Ok(view);
}).RequireAuthorization("merchant-user")
    .WithTags("Cart")
    .WithName("ClearCart")
    .WithSummary("Clear a cart")
    .WithDescription("Remove every line from the cart and return the emptied cart.")
    .Produces<CartView>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status401Unauthorized);

// Checkout. Start prices the checkout from the CART's subtotal (never a client-supplied amount), captures
// an optional notification recipient, then Confirm emits CheckoutConfirmed -> Orders opens the order.
api.MapPost("/checkouts", async (
    StartCheckoutRequest body, IActorContext actor, IMediator mediator, CancellationToken ct) =>
{
    var cart = await mediator.Send(new GetCartQuery(body.CartId, actor.MerchantId), ct);
    if (cart is null)
        return Results.NotFound();
    if (cart.Subtotal is not { } subtotal)
        return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Cannot check out an empty cart.");

    var result = await mediator.Send(
        new StartCheckoutCommand(actor.MerchantId, body.CartId, subtotal, body.Recipient), ct);
    return Results.Ok(result);
}).RequireAuthorization("merchant-user")
    .WithTags("Checkout")
    .WithName("StartCheckout")
    .WithSummary("Start checkout")
    .WithDescription("Price a checkout from the cart subtotal (never a client amount). Unknown cart -> 404, empty cart -> 400.")
    .Produces<StartCheckoutResult>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status401Unauthorized);

api.MapPost("/checkouts/{checkoutSessionId:guid}/confirm", async (
    Guid checkoutSessionId, IActorContext actor, IMediator mediator, CancellationToken ct) =>
{
    var result = await mediator.Send(new ConfirmCheckoutCommand(checkoutSessionId, actor.MerchantId), ct);
    return Results.Ok(result);
}).RequireAuthorization("merchant-user")
    .WithTags("Checkout")
    .WithName("ConfirmCheckout")
    .WithSummary("Confirm checkout")
    .WithDescription("Confirm the checkout session, emitting CheckoutConfirmed so Orders opens the order.")
    .Produces<ConfirmCheckoutResult>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status401Unauthorized);

var createPaymentSession = api.MapPost("/payments/sessions", async (
    CreatePaymentSessionRequest body,
    IActorContext actor,
    IMediator mediator,
    CancellationToken ct) =>
{
    var result = await mediator.Send(new CreateSessionCommand(
        body.OrderId, actor.MerchantId, body.Amount, body.Method, body.Psp), ct);
    return TypedResults.Ok(new CreatePaymentSessionResponse(result.PaymentSessionId));
});
createPaymentSession.RequireAuthorization("merchant-user").RequirePermission(Keys.PaymentCreate)
    .WithTags("Payments")
    .WithName("CreatePaymentSession")
    .WithSummary("Create a payment session")
    .WithDescription("Open a payment session for an order against the chosen method/PSP. Requires the merchant-user policy + payment.create.")
    .Produces<CreatePaymentSessionResponse>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status403Forbidden);

// Claims-then-charges redirect (PLAN #11). Merchant scoping is automatic: the command is IMerchantScoped, so
// MerchantGuardBehavior + RLS resolve the session for the authenticated merchant only. Errors flow through the
// shared ProblemDetails handler (not found -> 404, illegal state / concurrent claim -> 409).
var startRedirect = api.MapPost("/payments/sessions/{paymentSessionId:guid}/redirect", async (
    Guid paymentSessionId,
    IMediator mediator,
    CancellationToken ct) =>
{
    var result = await mediator.Send(new StartRedirectCommand(paymentSessionId), ct);
    return TypedResults.Ok(new StartRedirectResponse(result.RedirectUrl));
});
startRedirect.RequireAuthorization("merchant-user").RequirePermission(Keys.PaymentRedirect)
    .WithTags("Payments")
    .WithName("StartPaymentRedirect")
    .WithSummary("Start the PSP redirect")
    .WithDescription("Claim then charge: return the PSP redirect URL for the payment session. Requires the merchant-user policy + payment.redirect. Not found -> 404, illegal/concurrent state -> 409.")
    .Produces<StartRedirectResponse>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status409Conflict)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status403Forbidden);

// Order summary link. The customer opens it anonymously — the opaque token IS the capability, resolved on
// a bypass proc (no merchant binding). Unknown token -> 404; expired -> 410. A merchant-user can resend (rotates
// the token + extends the TTL), which is merchant-scoped.
api.MapGet("/orders/{token}/summary", async (
    string token, IOrderSummaryReader reader, IClock clock, CancellationToken ct) =>
{
    var summary = await reader.GetByTokenAsync(token, ct);
    if (summary is null)
        return Results.NotFound();
    if (clock.UtcNow >= summary.ExpiresAt)
        return Results.Problem(statusCode: StatusCodes.Status410Gone, title: "This link has expired.");

    return Results.Ok(new OrderSummaryResponse(
        summary.OrderId, summary.Amount, summary.Status, summary.PaymentSessionId));
}).AllowAnonymous()
    .WithTags("Orders")
    .WithName("GetOrderSummary")
    .WithSummary("Order summary by link")
    .WithDescription("Public capability link: the opaque token resolves the order summary anonymously. Unknown token -> 404, expired -> 410.")
    .Produces<OrderSummaryResponse>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status410Gone);

api.MapPost("/orders/{orderId:guid}/summary/resend", async (
    Guid orderId, IActorContext actor, IMediator mediator, CancellationToken ct) =>
{
    var result = await mediator.Send(new ResendOrderSummaryCommand(orderId, actor.MerchantId), ct);
    return Results.Ok(result);
}).RequireAuthorization("merchant-user")
    .WithTags("Orders")
    .WithName("ResendOrderSummary")
    .WithSummary("Resend the order summary link")
    .WithDescription("Rotate the order summary token and extend its TTL, returning the fresh link.")
    .Produces<ResendOrderSummaryResult>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status401Unauthorized);

// Reconciliation report: the bound merchant's orders grouped by status + currency (count + total).
api.MapGet("/reports/reconciliation", async (IActorContext actor, IMediator mediator, CancellationToken ct) =>
{
    var view = await mediator.Send(new GetReconciliationSummaryQuery(actor.MerchantId), ct);
    return TypedResults.Ok(view);
}).RequireAuthorization("merchant-user")
    .WithTags("Orders")
    .WithName("GetReconciliationReport")
    .WithSummary("Reconciliation report")
    .WithDescription("The bound merchant's orders grouped by status and currency (count + total).")
    .Produces<ReconciliationView>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status401Unauthorized);

// --- Admin BFF (/api/v1/admins route group, REQ-1/7/10) ---
// One group binds the CSRF double-submit filter ONCE for the whole admin surface (the credentialed admin CORS
// policy is applied to /api/v1/admins/* by PolCorsPolicyProvider). Per-endpoint authorization stays explicit: login
// is anonymous; every other route gates on the Session "admin" policy. The CSRF filter exempts safe methods,
// so the login/callback GETs pass untouched.
var admin = api.MapGroup("/admins").AddEndpointFilter<CsrfFilter>();

// Top-level browser navigation (AllowAnonymous, rate-limited): validate the post-login returnTo against the
// allowlist, then hand off to the OIDC handler, which builds the Authorization Code + PKCE + state + nonce
// redirect to Google. The callback (Google:Oidc:CallbackPath) is handled by the OIDC middleware itself, which
// establishes the session via OnTicketReceived — there is no mapped callback endpoint.
admin.MapGet("/auth/login", (HttpContext http, IOptions<AdminSessionOptions> session) =>
{
    var returnTo = ReturnUrlPolicy.Resolve(
        http.Request.Query["returnTo"].ToString(), session.Value.ReturnUrlAllowlist, session.Value.DefaultReturnPath);
    return Results.Challenge(
        new AuthenticationProperties { RedirectUri = returnTo },
        [OidcAuthentication.Scheme]);
})
.AllowAnonymous()
.RequireRateLimiting(AuthRateLimiting.PolicyName)
    .WithTags("Admin Auth")
    .WithName("AdminLogin")
    .WithSummary("Begin admin login")
    .WithDescription("Validate returnTo against the allowlist, then redirect to Google (OIDC Authorization Code + PKCE). The callback establishes the session cookie.")
    .Produces(StatusCodes.Status302Found)
    .ProducesProblem(StatusCodes.Status429TooManyRequests);

// Logout = revoke the CURRENT session family (this device only); other devices stay signed in (REQ-6.1). The
// presented cookie identifies the family. CSRF protection for these POSTs is added with the other /admin
// mutations in Task 5's double-submit filter (REQ-7).
admin.MapPost("/auth/logout", async (
    HttpContext http, ISessionStore sessions, SessionCookies cookies,
    IAuthAuditWriter audit, IClock clock, CancellationToken ct) =>
{
    var token = cookies.ReadSessionToken(http);
    if (token is not null)
    {
        var session = await sessions.FindByTokenHashAsync(SessionTokens.Hash(token), ct);
        if (session is not null)
        {
            await sessions.RevokeFamilyAsync(session.FamilyId, ct);
            audit.Append(AuthAudit.For(AuthEventType.Logout, http.TraceIdentifier, clock.UtcNow, session.PlatformUserId));
            await audit.SaveChangesAsync(ct);
        }
    }
    cookies.Clear(http);
    return Results.NoContent();
}).RequireAuthorization("admin")
    .WithTags("Admin Auth")
    .WithName("AdminLogout")
    .WithSummary("Log out this device")
    .WithDescription("Revoke the current session family (this device only) and clear the cookie.")
    .Produces(StatusCodes.Status204NoContent)
    .ProducesProblem(StatusCodes.Status401Unauthorized);

// Logout-all = revoke EVERY session of this admin across all devices (REQ-6.2).
admin.MapPost("/auth/logout-all", async (
    HttpContext http, IAdminScope scope, ISessionStore sessions, SessionCookies cookies,
    IAuthAuditWriter audit, IClock clock, CancellationToken ct) =>
{
    var adminId = scope.Current.AdminId;
    await sessions.RevokeAllForAdminAsync(adminId, ct);
    audit.Append(AuthAudit.For(AuthEventType.LogoutAll, http.TraceIdentifier, clock.UtcNow, adminId));
    await audit.SaveChangesAsync(ct);
    cookies.Clear(http);
    return Results.NoContent();
}).RequireAuthorization("admin")
    .WithTags("Admin Auth")
    .WithName("AdminLogoutAll")
    .WithSummary("Log out all devices")
    .WithDescription("Revoke every session of this admin across all devices and clear the cookie.")
    .Produces(StatusCodes.Status204NoContent)
    .ProducesProblem(StatusCodes.Status401Unauthorized);

// --- Admin-provisioned merchants (/api/v1/merchants, D9) ---
// Moved out of the /admins group (hierarchical-naming task 8, design §5): mapped DIRECTLY on `api`, like the
// admins-root create above it, so each endpoint re-attaches its own controls explicitly instead of inheriting
// them from group membership — CsrfFilter, the "admin" policy, and (POST only) the Super tier. The admin CORS
// policy is re-attached via the path table in CorsExtensions.cs, not here (CORS selection stays path-based).
// Admin provisioning (reference 2.4). Cross-merchant, so NOT IMerchantScoped — runs under pol_admin via the
// keyed admin scope. AdminSubject (sub claim) + correlation id (TraceIdentifier) are taken server-side,
// never from the body. Duplicate code -> ConflictException -> 409; bad input -> ArgumentException -> 400.
api.MapPost("/merchants", async (
    ProvisionMerchantRequest body,
    HttpContext http,
    IMediator mediator,
    IAdminScope adminScope,
    IUserRepository adminUsers,
    CancellationToken ct) =>
{
    // The documented 2.4 body wraps merchant fields under "merchant"; non-secret PSP config rides alongside
    // "psp"/"secrets" and is captured verbatim via JsonExtensionData (reference 2.4 — config stored as-is).
    var t = body.Merchant ?? throw new ArgumentException("The 'merchant' object is required.");

    // The caller's CURRENT AuthorizationVersion, read fresh right before dispatch (task 8.5.4) — this IS the
    // "pinned at the request boundary" snapshot the provisioning UoW re-verifies in-transaction under lock.
    var caller = await adminUsers.GetByIdAsync(adminScope.Current.AdminId, ct)
        ?? throw new InvalidOperationException("The authenticated admin no longer exists.");

    var command = new ProvisionMerchantCommand(
        new MerchantSpec(t.Code, t.DisplayName, t.LegalEntityId, t.Country, t.Currency,
            t.EnabledChannels ?? [], ToElement(t.Metadata)),
        [.. (body.PspConnections ?? []).Select(p =>
        {
            // A secret-looking field captured as readable config (a typo putting it beside, not inside,
            // "secrets") would persist + echo plaintext outside the vault -> reject it (400).
            ProvisioningGuards.RejectSecretsInConfig(p.Config);
            return new PspConnectionSpec(
                p.Psp, p.EnabledMethods ?? [], p.MerchantId,
                p.Secrets ?? new Dictionary<string, string>(), ToElement(p.Config));
        })],
        http.User.FindFirst("sub")?.Value ?? "unknown",
        http.TraceIdentifier,
        adminScope.Current.AdminId,
        caller.AuthorizationVersion);

    var result = await mediator.Send(command, ct);
    return Results.Created($"/api/v1/merchants/{t.Code}", result);

    // Re-pack the captured overflow fields into a single JSON element for verbatim storage.
    static JsonElement? ToElement(IDictionary<string, JsonElement>? extra) =>
        extra is null || extra.Count == 0 ? null : JsonSerializer.SerializeToElement(extra);
})
    .AddEndpointFilter<CsrfFilter>() // re-attached explicitly — no longer inherited from the /admins group (REQ-7.1)
    .RequireAuthorization("admin").RequirePlatformUserTier(Tier.Super) // provisioning is Super-only (REQ-8.4)
    .WithTags("Admin Merchants")
    .WithName("ProvisionMerchant")
    .WithSummary("Provision a merchant")
    .WithDescription("Super-only. Create a merchant with its PSP connections (secrets vaulted, config stored verbatim). Duplicate code -> 409, bad input -> 400.")
    .Produces<ProvisionMerchantResult>(StatusCodes.Status201Created)
    .ProducesProblem(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status409Conflict)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status403Forbidden);

// Cross-merchant read routed through the IAdminQuery seam: a Scoped admin sees only its assigned merchants, a
// Super is unrestricted (REQ-8.5 / 7.1). Out-of-scope or unknown -> 404 (no existence leak). {code} stays
// unconstrained (REQ-6.5) — adding a route constraint here would itself be a behavior change.
api.MapGet("/merchants/{code}", async (
    string code,
    IAdminQuery adminQuery,
    CancellationToken ct) =>
{
    var view = await adminQuery.GetMerchantByCodeAsync(code, ct);
    return view is null
        ? Results.Problem(statusCode: StatusCodes.Status404NotFound)
        : Results.Ok(view);
}).AddEndpointFilter<CsrfFilter>().RequireAuthorization("admin") // GET is CSRF-exempt by design; attached for REQ-7.1
    .WithTags("Admin Merchants")
    .WithName("GetMerchant")
    .WithSummary("Read a merchant by code")
    .WithDescription("Scoped admins see only assigned merchants; Super is unrestricted. Out-of-scope or unknown -> 404 (no existence leak).")
    .Produces<MerchantView>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status401Unauthorized);

// --- MerchantUser BFF login + registration (merchant-user-google-sso REQ-8/9/14) ---
// The merchant-users area has TWO group refs at the same /api/v1/merchants/users prefix (REQ-3.7): this UNFILTERED ref carries
// the anonymous pre-session entry (login + register), exactly as they were mapped top-level before the migration;
// the filtered console group below (CSRF + bound-merchant-user) carries the authenticated surface. The area-prefix move
// must NOT move any endpoint between tiers.
var merchantUsersAnon = api.MapGroup("/merchants/users");

// Top-level browser navigation (AllowAnonymous, rate-limited): validate the post-login returnTo against the merchant-user
// allowlist, then hand off to the "MerchantUserGoogle" OIDC handler, which builds the Authorization Code + PKCE + state
// + nonce redirect to Google. The callback (MerchantUser:Oidc:CallbackPath) is handled by the OIDC middleware itself,
// which runs the 4-way state branch via OnTicketReceived -> MerchantUserLoginService (session cookie for an Active
// merchant-user, a signed registration/correction ticket + redirect to /register otherwise) — there is no mapped callback.
merchantUsersAnon.MapGet("/auth/login", (HttpContext http, IOptions<UserSessionOptions> session) =>
{
    var returnTo = ReturnUrlPolicy.Resolve(
        http.Request.Query["returnTo"].ToString(), session.Value.ReturnUrlAllowlist, session.Value.DefaultReturnPath);
    return Results.Challenge(
        new AuthenticationProperties { RedirectUri = returnTo },
        [UserOidcAuthentication.Scheme]);
})
.AllowAnonymous()
.RequireRateLimiting(UserAuthRateLimiting.PolicyName)
    .WithTags("MerchantUser Auth")
    .WithName("MerchantUserLogin")
    .WithSummary("Begin merchant-user login")
    .WithDescription("Validate returnTo against the allowlist, then redirect to Google (OIDC Authorization Code + PKCE). The callback establishes a session cookie for an Active merchant-user, or redirects an applicant to /register with a signed ticket.")
    .Produces(StatusCodes.Status302Found)
    .ProducesProblem(StatusCodes.Status429TooManyRequests);

// --- MerchantUser self-service registration (merchant-user-google-sso REQ-3/4/5/7/13/20) ---
// Anonymous + ticket-gated: the signed, time-limited (stateless) ticket IS the capability barrier, so no session CSRF on this
// pre-session route (REQ-13.4); rate-limited per IP instead. Multipart (form + optional photo): the request body
// is bounded BEFORE buffering (REQ-7.4/N3), the photo is validated by content-type + magic bytes (REQ-7.3), then
// the write runs in ONE pol_admin transaction that also enqueues the registration event (REQ-4.1/20). Identity is
// taken only from the verified ticket, never the form (REQ-4.2). Replays/duplicates -> 409 (no 500); a Correction
// ticket resubmits a Rejected user (REQ-5).
merchantUsersAnon.MapPost("/register", async (
    HttpRequest request,
    UserRegistrationTickets tickets,
    IOptions<UserRegistrationOptions> registrationOptions,
    IMediator mediator,
    HttpContext http,
    CancellationToken ct) =>
{
    var opts = registrationOptions.Value;

    // Bound the body BEFORE reading the multipart so an oversized upload is aborted mid-read, never buffered whole
    // then measured (DoS guard, N3). Photo cap + headroom for the text fields.
    var sizeFeature = http.Features.Get<IHttpMaxRequestBodySizeFeature>();
    if (sizeFeature is { IsReadOnly: false })
        sizeFeature.MaxRequestBodySize = opts.PhotoMaxBytes + 64 * 1024;

    if (!request.HasFormContentType)
        return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "multipart/form-data is required.");

    IFormCollection form;
    try
    {
        form = await request.ReadFormAsync(ct);
    }
    catch (BadHttpRequestException)
    {
        return Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "The upload exceeds the size limit.");
    }

    if (!tickets.TryUnprotect(form["ticket"].ToString(), out var ticket))
        return Results.Problem(statusCode: StatusCodes.Status400BadRequest,
            title: "The registration ticket is missing, invalid, or expired.");

    // Optional photo: validate type + magic bytes + size BEFORE it is stored (REQ-7.3/7.4); store nothing on reject.
    byte[]? photoBytes = null;
    string? photoContentType = null;
    var file = form.Files["photo"];
    if (file is { Length: > 0 })
    {
        if (file.Length > opts.PhotoMaxBytes)
            return Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "The photo exceeds the size limit.");
        var buffer = new byte[file.Length];
        await using (var stream = file.OpenReadStream())
            await stream.ReadExactlyAsync(buffer, ct);
        var validation = PhotoValidation.Validate(
            file.ContentType, buffer.AsSpan(0, Math.Min(16, buffer.Length)), buffer.Length, opts.PhotoMaxBytes);
        if (!validation.IsValid)
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: validation.Error);
        photoBytes = buffer;
        photoContentType = validation.ContentType;
    }

    var formModel = UserRegistrationForm.From(form);
    if (string.IsNullOrWhiteSpace(formModel.FirstName) || string.IsNullOrWhiteSpace(formModel.LastName))
        return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "firstName and lastName are required.");

    var result = await mediator.Send(new SubmitRegistrationCommand(
        ticket.Subject, ticket.Email, ticket.HostedDomain, ticket.Purpose,
        formModel, photoBytes, photoContentType, http.TraceIdentifier), ct);

    return Results.Created($"/api/v1/merchants/users/{result.MerchantUserId}",
        new UserRegisterResponse(result.MerchantUserId, result.Status.ToString()));
})
    .AllowAnonymous()
    .DisableAntiforgery()
    .RequireRateLimiting(UserAuthRateLimiting.PolicyName)
    .WithTags("MerchantUser Auth")
    .WithName("MerchantUserRegister")
    .WithSummary("Submit a merchant-user registration")
    .WithDescription("Anonymous, ticket-gated multipart submission (form + optional photo). Creates a PendingApproval MerchantUser and enqueues a registration event. Invalid/expired ticket -> 400; duplicate/replay (unique Subject index) -> 409; oversize -> 413.")
    .Accepts<IFormFile>("multipart/form-data")
    .Produces<UserRegisterResponse>(StatusCodes.Status201Created)
    .ProducesProblem(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status409Conflict)
    .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
    .ProducesProblem(StatusCodes.Status429TooManyRequests);

// --- MerchantUser BFF authenticated surface (merchant-user-google-sso REQ-12/13/15/16/17) ---
// One group binds the CSRF double-submit filter ONCE for the whole authenticated merchant-user surface (the credentialed
// merchant-user CORS policy is applied to /api/v1/merchants/users/* by PolCorsPolicyProvider). Every route gates on the
// single-scheme "merchant-user" policy (MerchantUserSession only, T11); the CSRF filter exempts safe methods, and the
// anonymous pre-session routes (login/callback/register) are mapped OUTSIDE this group, so they are untouched by it.
// MerchantBoundFilter then fail-closes the whole group on a BOUND merchant-user (REQ-17.2/F10).
var merchantUsers = api.MapGroup("/merchants/users")
    .AddEndpointFilter<UserCsrfFilter>()
    .AddEndpointFilter<BoundFilter>();

// Logout = revoke the CURRENT session family (this device only); other devices stay signed in (REQ-12.1). The
// presented cookie identifies the family.
merchantUsers.MapPost("/auth/logout", async (
    HttpContext http, IMerchantSessionStore sessions, UserSessionCookies cookies,
    IMerchantAuthAuditWriter audit, IClock clock, CancellationToken ct) =>
{
    var token = cookies.ReadSessionToken(http);
    if (token is not null)
    {
        var session = await sessions.FindByTokenHashAsync(UserTokens.Hash(token), ct);
        if (session is not null)
        {
            await sessions.RevokeFamilyAsync(session.FamilyId, ct);
            audit.Append(MerchantAuthAudit.For(MerchantAuthEventType.Logout, http.TraceIdentifier, clock.UtcNow, session.MerchantUserId));
            await audit.SaveChangesAsync(ct);
        }
    }
    cookies.Clear(http);
    return Results.NoContent();
}).RequireAuthorization("merchant-user")
    .WithTags("MerchantUser Auth")
    .WithName("MerchantUserLogout")
    .WithSummary("Log out this device")
    .WithDescription("Revoke the current merchant-user session family (this device only) and clear the cookie.")
    .Produces(StatusCodes.Status204NoContent)
    .ProducesProblem(StatusCodes.Status401Unauthorized);

// Logout-all = revoke EVERY session of this merchant-user across all devices (REQ-12.2).
merchantUsers.MapPost("/auth/logout-all", async (
    HttpContext http, IUserScope scope, IMerchantSessionStore sessions, UserSessionCookies cookies,
    IMerchantAuthAuditWriter audit, IClock clock, CancellationToken ct) =>
{
    var userId = scope.Current.MerchantUserId;
    await sessions.RevokeAllForUserAsync(userId, ct);
    audit.Append(MerchantAuthAudit.For(MerchantAuthEventType.LogoutAll, http.TraceIdentifier, clock.UtcNow, userId));
    await audit.SaveChangesAsync(ct);
    cookies.Clear(http);
    return Results.NoContent();
}).RequireAuthorization("merchant-user")
    .WithTags("MerchantUser Auth")
    .WithName("MerchantUserLogoutAll")
    .WithSummary("Log out all devices")
    .WithDescription("Revoke every session of this merchant-user across all devices and clear the cookie.")
    .Produces(StatusCodes.Status204NoContent)
    .ProducesProblem(StatusCodes.Status401Unauthorized);

// The merchant-user SPA reads its own resolved identity (REQ-17.5): merchantUserId/email/merchantId + active role codes +
// the effective permission set, all from the per-request IMerchantUserScope. A merchant-Bearer caller binds no scope -> 403.
merchantUsers.MapGet("/me", async (IUserScope scope, IMerchantRoleRepository roles, CancellationToken ct) =>
{
    if (!scope.IsBound)
        return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Your merchant-user account is not active.");
    var me = scope.Current;
    var roleCodes = await roles.ListActiveRoleCodesForUserAsync(me.MerchantUserId, me.MerchantId, ct);
    return Results.Ok(new MerchantUserMeResponse(me.MerchantUserId, me.Email, me.MerchantId, roleCodes, me.Permissions));
}).RequireAuthorization("merchant-user")
    .WithTags("MerchantUser Auth")
    .WithName("GetMerchantUserMe")
    .WithSummary("Resolve the current merchant-user")
    .WithDescription("The SPA reads its own identity: merchant, active role codes, and effective permissions. Not bound (merchant-Bearer) -> 403.")
    .Produces<MerchantUserMeResponse>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status403Forbidden)
    .ProducesProblem(StatusCodes.Status401Unauthorized);

// --- MerchantUser Role RBAC (REQ-3/6) ---
// Reads need only an authenticated merchant-user (REQ-3.6); role mutations gate on roles.manage and the
// assignment gates on users.roles. status crosses the wire as "active"/"inactive" via explicit projection.
// Backed by the SAME Iam.Application.Roles handlers the admin console uses (rf2) —
// RoleSideContextResolver.ForMerchantUser is the one place that turns the bound scope into
// RoleSideContext.Merchant(merchantId), never the endpoint itself (design.md).
static MerchantUserRoleResponse MerchantUserRoleToWire(RoleListItem r) => new(
    r.Code, r.Name, r.Description, r.Color,
    r.Status == RoleStatus.Active ? "active" : "inactive",
    r.PermissionKeys, r.UserCount, r.Shared);
// Strict: an unrecognized value (typo, blank, null) is a 400 — never a silent default to Active.
static RoleStatus ParseMerchantUserRoleStatus(string? status) => status?.ToLowerInvariant() switch
{
    "active" => RoleStatus.Active,
    "inactive" => RoleStatus.Inactive,
    _ => throw new ArgumentException($"Invalid role status '{status}'. Expected 'active' or 'inactive'."),
};

merchantUsers.MapGet("/permissions", async (IMediator mediator, CancellationToken ct) =>
{
    var catalog = await mediator.Send(new GetPermissionCatalogQuery(Scope.Merchant), ct);
    return Results.Ok(new MerchantUserPermissionCatalogResponse(
        catalog.Groups.Select(g => new MerchantUserPermissionGroupResponse(g.Key, g.LabelTh)).ToArray(),
        catalog.Permissions.Select(p => new MerchantUserPermissionItemResponse(p.Key, p.LabelTh, p.Resource)).ToArray()));
}).RequireAuthorization("merchant-user")
    .WithTags("MerchantUser Roles")
    .WithName("ListMerchantUserPermissions")
    .WithSummary("MerchantUser permission catalog")
    .WithDescription("The permission/group catalog backing the merchant-user role matrix (resource = the permission's group key).")
    .Produces<MerchantUserPermissionCatalogResponse>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status401Unauthorized);

merchantUsers.MapGet("/roles", async (IUserScope scope, IMediator mediator, CancellationToken ct) =>
{
    var context = RoleSideContextResolver.ForMerchantUser(scope);
    var result = await mediator.Send(new ListRolesQuery { Context = context, Limit = int.MaxValue }, ct);
    return Results.Ok(result.Items.Select(MerchantUserRoleToWire));
})
    .RequireAuthorization("merchant-user")
    .WithTags("MerchantUser Roles")
    .WithName("ListMerchantUserRoles")
    .WithSummary("List merchant-user roles")
    .WithDescription("All merchant-user roles (shared + this merchant's own) with their permissions and bound-user counts.")
    .Produces<IEnumerable<MerchantUserRoleResponse>>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status401Unauthorized);

merchantUsers.MapGet("/roles/{code}", async (string code, IUserScope scope, IMediator mediator, CancellationToken ct) =>
{
    var context = RoleSideContextResolver.ForMerchantUser(scope);
    var role = await mediator.Send(new GetRoleQuery(context, code), ct);
    return role is null ? Results.Problem(statusCode: StatusCodes.Status404NotFound) : Results.Ok(MerchantUserRoleToWire(role));
}).RequireAuthorization("merchant-user")
    .WithTags("MerchantUser Roles")
    .WithName("GetMerchantUserRole")
    .WithSummary("Read a merchant-user role by code")
    .WithDescription("Return a single merchant-user role with its permissions. Unknown or not visible to this merchant -> 404.")
    .Produces<MerchantUserRoleResponse>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status401Unauthorized);

merchantUsers.MapPost("/roles", async (
    CreateMerchantUserRoleRequest body, IUserScope scope, HttpContext http, IMediator mediator, CancellationToken ct) =>
{
    var context = RoleSideContextResolver.ForMerchantUser(scope);
    var result = await mediator.Send(new CreateRoleCommand(
        context, body.Code ?? "", body.Name ?? "", body.Description, body.Color, ParseMerchantUserRoleStatus(body.Status),
        body.Permissions ?? [], http.TraceIdentifier), ct);
    return Results.Created($"/api/v1/merchants/users/roles/{result.Code}", MerchantUserRoleToWire(result));
}).RequireAuthorization("merchant-user").RequirePermission(Keys.RolesManage)
    .WithTags("MerchantUser Roles")
    .WithName("CreateMerchantUserRole")
    .WithSummary("Create a merchant-user role")
    .WithDescription("Requires roles.manage. Duplicate code (incl. a shared code) -> 409; permission key outside the catalog or from the Platform side -> 400.")
    .Produces<MerchantUserRoleResponse>(StatusCodes.Status201Created)
    .ProducesProblem(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status409Conflict)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status403Forbidden);

merchantUsers.MapPut("/roles/{code}", async (
    string code, UpdateMerchantUserRoleRequest body, IUserScope scope, HttpContext http, IMediator mediator, CancellationToken ct) =>
{
    var context = RoleSideContextResolver.ForMerchantUser(scope);
    var result = await mediator.Send(new UpdateRoleCommand(
        context, code, body.Name ?? "", body.Description, body.Color, ParseMerchantUserRoleStatus(body.Status),
        body.Permissions ?? [], http.TraceIdentifier), ct);
    return Results.Ok(MerchantUserRoleToWire(result));
}).RequireAuthorization("merchant-user").RequirePermission(Keys.RolesManage)
    .WithTags("MerchantUser Roles")
    .WithName("UpdateMerchantUserRole")
    .WithSummary("Update a merchant-user role")
    .WithDescription("Requires roles.manage. Code is immutable (from the route); a role not owned by this merchant (incl. a shared seed) -> 409; deactivating merchant_manager -> 409.")
    .Produces<MerchantUserRoleResponse>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status409Conflict)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status403Forbidden);

merchantUsers.MapDelete("/roles/{code}", async (
    string code, IUserScope scope, HttpContext http, IMediator mediator, CancellationToken ct) =>
{
    var context = RoleSideContextResolver.ForMerchantUser(scope);
    await mediator.Send(new DeleteRoleCommand(context, code, http.TraceIdentifier), ct);
    return Results.NoContent();
}).RequireAuthorization("merchant-user").RequirePermission(Keys.RolesManage)
    .WithTags("MerchantUser Roles")
    .WithName("DeleteMerchantUserRole")
    .WithSummary("Delete a merchant-user role")
    .WithDescription("Requires roles.manage. A role not owned by this merchant (incl. a shared seed) -> 409; merchant_manager is undeletable -> 409; a role with bound users -> 409.")
    .Produces(StatusCodes.Status204NoContent)
    .ProducesProblem(StatusCodes.Status409Conflict)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status403Forbidden);

// Set another merchant-user's roles to exactly the given set, within the acting merchant-user's merchant (REQ-16.3). Unknown
// role code -> 400; a target outside the acting merchant -> 404 (no existence leak).
merchantUsers.MapPut("/{merchantUserId:guid}/roles", async (
    Guid merchantUserId, SetMerchantUserRolesRequest body, IUserScope scope, IMediator mediator, CancellationToken ct) =>
{
    var me = scope.Current;
    await mediator.Send(new MerchantSetRolesCommand(merchantUserId, body.RoleCodes ?? [], me.MerchantId, me.MerchantUserId), ct);
    return Results.NoContent();
}).RequireAuthorization("merchant-user").RequirePermission(Keys.UsersRoles)
    .WithTags("MerchantUser Roles")
    .WithName("SetMerchantUserUserRoles")
    .WithSummary("Set a merchant-user's roles")
    .WithDescription("Requires merchant-user.user.roles. Replace a merchant-user's roles with exactly the given set, scoped to your merchant. Unknown role code -> 400; target not in your merchant -> 404.")
    .Produces(StatusCodes.Status204NoContent)
    .ProducesProblem(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status403Forbidden);

// --- Admin approves/rejects a merchant-user (cross-plane, merchant-user-google-sso REQ-6/18) ---
// The Admin permission (merchant-user.approve/reject) + the accessible-merchant floor (IAdminQuery) run HERE, at the host,
// before crossing into the MerchantUser module (critique B3) — the dispatched command receives an already-validated
// merchant id and carries no Admin import. On the admin group, so the admin CSRF filter + Session policy apply.
admin.MapPost("/merchants/users/{subject}/approve", async (
    string subject, ApproveMerchantUserRequest body, IAdminScope scope, IAdminQuery adminQuery,
    HttpContext http, IMediator mediator, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(body.MerchantCode))
        return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "A merchant code is required to approve.");

    // The accessible-merchant floor: a Scoped admin sees only its assigned merchants, a Super is unrestricted. An
    // unknown code OR a merchant outside the admin's scope returns null -> 404 (no existence leak, REQ-6.3/22.3).
    var merchant = await adminQuery.GetMerchantByCodeAsync(body.MerchantCode, ct);
    if (merchant is null)
        return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Merchant not found or not in your scope.");
    if (!string.Equals(merchant.Status, "Active", StringComparison.OrdinalIgnoreCase))
        return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "The selected merchant is not active.");

    var result = await mediator.Send(new ApproveCommand(
        subject, merchant.Id, body.RoleCodes ?? [],
        http.User.FindFirst("sub")?.Value ?? "unknown", scope.Current.AdminId, http.TraceIdentifier), ct);
    return Results.Ok(new ApproveMerchantUserResponse(result.MerchantUserId, result.Status.ToString(), result.AlreadyActive));
}).RequireAuthorization("admin").RequirePermission(Keys.MerchantUserApprove)
    .WithTags("Admin MerchantUsers")
    .WithName("ApproveMerchantUser")
    .WithSummary("Approve a merchant-user onto a merchant")
    .WithDescription("Requires merchant-user.approve. Binds the merchant-user to a merchant in the admin's accessible set + assigns roles + activates, in one transaction. Already-Active -> idempotent 200; unknown target -> 404; inactive/out-of-scope merchant -> 409/404; unknown/inactive role or non-Pending target -> 409.")
    .Produces<ApproveMerchantUserResponse>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status409Conflict)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status403Forbidden);

admin.MapPost("/merchants/users/{subject}/reject", async (
    string subject, RejectMerchantUserRequest body, HttpContext http, IMediator mediator, CancellationToken ct) =>
{
    var result = await mediator.Send(new RejectCommand(
        subject, body.Reason, http.User.FindFirst("sub")?.Value ?? "unknown", http.TraceIdentifier), ct);
    return Results.Ok(new RejectMerchantUserResponse(result.MerchantUserId, result.Status.ToString()));
}).RequireAuthorization("admin").RequirePermission(Keys.MerchantUserReject)
    .WithTags("Admin MerchantUsers")
    .WithName("RejectMerchantUser")
    .WithSummary("Reject a pending merchant-user")
    .WithDescription("Requires merchant-user.reject. Sets the merchant-user Rejected and revokes any live sessions. Unknown target -> 404; a non-Pending target -> 409.")
    .Produces<RejectMerchantUserResponse>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status409Conflict)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status403Forbidden);

// --- Admin identity foundation management (REQ-3..10) + SPA bootstrap (REQ-13) ---

// The Admin SPA reads its own resolved identity to render the right scope/navigation (REQ-13). adminId/tier/
// accessible come from the per-request IAdminScope the middleware materialized; a Super returns an
// unrestricted flag (never the full merchant list), a Scoped admin gets its assigned {id, code} pairs.
admin.MapGet("/me", async (IAdminScope scope, IAdminMerchantDirectory merchants, CancellationToken ct) =>
{
    if (!scope.IsBound)
        return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Your admin account is not active.");

    var me = scope.Current;
    AdminAccessibleResponse accessible;
    if (me.Accessible.IsUnrestricted)
    {
        accessible = new AdminAccessibleResponse(IsUnrestricted: true, Merchants: null);
    }
    else
    {
        var codes = await merchants.GetCodesByIdsAsync(me.Accessible.Merchants, ct);
        accessible = new AdminAccessibleResponse(
            IsUnrestricted: false,
            Merchants: me.Accessible.Merchants
                .Select(id => new AdminAccessibleMerchantResponse(id, codes.GetValueOrDefault(id))).ToArray());
    }

    // permissions = effective action permissions (admin-role-rbac REQ-9.1)
    return Results.Ok(new AdminMeResponse(me.AdminId, me.Email, me.Tier.ToString(), accessible, me.Permissions));
}).RequireAuthorization("admin")
    .WithTags("Admin Auth")
    .WithName("GetAdminMe")
    .WithSummary("Resolve the current admin")
    .WithDescription("The SPA reads its own identity: tier, accessible merchants (or unrestricted), and effective permissions. Inactive account -> 403.")
    .Produces<AdminMeResponse>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status403Forbidden)
    .ProducesProblem(StatusCodes.Status401Unauthorized);

// Super invites a Scoped admin by verified email; the subject binds on the invitee's first login (REQ-3.4). This is
// the admins-area ROOT (POST /api/v1/admins): mapped on `api` with AdminCsrfFilter applied per-endpoint — a group's
// empty-string root pattern would render the trailing-slash "/api/v1/admins/" (REQ-1.4). Same CSRF + auth as the group.
api.MapPost("/admins", async (
    CreateAdminRequest body, IAdminScope scope, HttpContext http, IMediator mediator, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(body.Email))
        throw new ArgumentException("Email is required.");
    var result = await mediator.Send(new CreateScopedCommand(
        body.Email, scope.Current.AdminId, http.TraceIdentifier,
        body.PositionId, body.OfficeId, body.LevelId, body.DivisionId), ct);
    return Results.Created($"/api/v1/admins/{result.AdminId}", result);
}).AddEndpointFilter<CsrfFilter>().RequireAuthorization("admin").RequirePlatformUserTier(Tier.Super)
    .WithTags("Admin Admins")
    .WithName("CreateScopedAdmin")
    .WithSummary("Invite a scoped admin")
    .WithDescription("Super-only. Invite a Scoped admin by verified email; the subject binds on their first login. Missing email -> 400.")
    .Produces<CreateScopedResult>(StatusCodes.Status201Created)
    .ProducesProblem(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status403Forbidden);

// --- Admin account management (admin-account-management) ---
// tier/status cross the wire as stable lowercase strings via explicit projection — there is no global
// string-enum converter (B2), mirroring RoleToWire.
static string TierToWire(Tier t) => t == Tier.Super ? "super" : "scoped";
static Tier? WireToTier(string wire) => wire.ToLowerInvariant() switch
{
    "super" => Tier.Super,
    "scoped" => Tier.Scoped,
    _ => null,
};
static string AccountStatusToWire(UserStatus s) => s == UserStatus.Active ? "active" : "suspended";
static string SessionStatusToWire(SessionStatus s) => s switch
{
    SessionStatus.Active => "active",
    SessionStatus.Superseded => "superseded",
    _ => "revoked",
};
static AdminListItemResponse AdminToWire(UserListItem a) =>
    new(a.AdminId, a.Email, TierToWire(a.Tier), AccountStatusToWire(a.Status), a.CreatedAt, a.SubjectBound);
static MasterRefResponse? MasterRefToWire(ProfileRef? r) => r is null ? null : new(r.Id, r.Code, r.Name);
static PlatformUserSessionResponse SessionToWire(SessionView v) =>
    new(v.SessionId, v.FamilyId, SessionStatusToWire(v.Status), v.IssuedAt, v.IdleExpiresAt, v.AbsoluteExpiresAt,
        v.CreatedIp, v.UserAgent, v.IsLive);

// The admin directory (REQ-1). Mapped on `api` (not the admins group): a group empty-string root pattern would
// render the forbidden trailing slash "/api/v1/admins/", same as POST /admins. Gated user.view — reads use the
// permission axis (a user.roles holder needs the directory to assign roles; see the role-composition note).
api.MapGet("/admins", async (HttpContext http, IMediator mediator, CancellationToken ct) =>
{
    var p = SfsQueryParser.Parse(http.Request.Query);
    var result = await mediator.Send(new ListAdminsQuery
    {
        Page = p.Page, Limit = p.Limit, Filters = p.Filters, Sort = p.Sort, Search = p.Search,
    }, ct);
    // Re-wrap into a new PagedResult — a record with-expression cannot change T (mirrors ListRoles).
    return Results.Ok(new PagedResult<AdminListItemResponse>(
        [.. result.Items.Select(AdminToWire)], result.Page, result.Limit, result.Total));
})
    .RequireAuthorization("admin")
    .RequirePermission(Keys.UserView)
    .WithMetadata(new SfsQueryParamsMarker())
    .WithTags("Admin Admins")
    .WithName("ListAdmins")
    .WithSummary("List admin accounts")
    .WithDescription("Requires the user.view permission. Paged admin directory. Supports SFS: page, limit, filters (email/tier/status), sort (email/createdAt), search (email). tier/status filter values are the lowercase wire forms; an out-of-domain value -> 400.")
    .Produces<PagedResult<AdminListItemResponse>>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status403Forbidden);

// One admin's full detail (REQ-2). Accessible merchants are mapped id->code in the host, byte-for-byte the /me
// pattern, so the query handler stays free of the merchant directory. Unknown id -> 404.
admin.MapGet("/{id:guid}", async (Guid id, IAdminMerchantDirectory merchants, IMediator mediator, CancellationToken ct) =>
{
    var detail = await mediator.Send(new GetAdminByIdQuery(id), ct);
    if (detail is null)
        return Results.Problem(statusCode: StatusCodes.Status404NotFound);

    AdminAccessibleResponse accessible;
    if (detail.Accessible.IsUnrestricted)
    {
        accessible = new AdminAccessibleResponse(IsUnrestricted: true, Merchants: null);
    }
    else
    {
        var codes = await merchants.GetCodesByIdsAsync(detail.Accessible.Merchants, ct);
        accessible = new AdminAccessibleResponse(
            IsUnrestricted: false,
            Merchants: detail.Accessible.Merchants
                .Select(tid => new AdminAccessibleMerchantResponse(tid, codes.GetValueOrDefault(tid))).ToArray());
    }

    return Results.Ok(new AdminDetailResponse(
        detail.AdminId, detail.Email, TierToWire(detail.Tier), AccountStatusToWire(detail.Status),
        detail.CreatedAt, detail.SubjectBound, accessible, detail.RoleCodes,
        MasterRefToWire(detail.Position), MasterRefToWire(detail.Office),
        MasterRefToWire(detail.Level), MasterRefToWire(detail.Division)));
}).RequireAuthorization("admin").RequirePermission(Keys.UserView)
    .WithTags("Admin Admins")
    .WithName("GetAdmin")
    .WithSummary("Read an admin account")
    .WithDescription("Requires the user.view permission. Returns the account's tier, status, accessible merchants (unrestricted for a Super), and all assigned role codes. Unknown id -> 404.")
    .Produces<AdminDetailResponse>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status403Forbidden);

// The admin's effective permissions = union over ACTIVE roles (REQ-6), the same rule as /me. Unknown id -> 404.
admin.MapGet("/{id:guid}/effective-permissions", async (Guid id, IMediator mediator, CancellationToken ct) =>
{
    var permissions = await mediator.Send(new GetEffectivePermissionsQuery(id), ct);
    return permissions is null ? Results.Problem(statusCode: StatusCodes.Status404NotFound) : Results.Ok(permissions);
}).RequireAuthorization("admin").RequirePermission(Keys.UserView)
    .WithTags("Admin Admins")
    .WithName("GetAdminEffectivePermissions")
    .WithSummary("Read an admin's effective permissions")
    .WithDescription("Requires the user.view permission. The distinct, ordinal-sorted union of permission keys from the admin's ACTIVE roles (same rule as /me). Works for a suspended admin. Unknown id -> 404.")
    .Produces<IReadOnlyList<string>>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status403Forbidden);

// Super assigns a merchant to a Scoped admin (REQ-4.1). Inactive/unknown merchant or duplicate -> 409.
admin.MapPost("/{id:guid}/merchants", async (
    Guid id, AssignMerchantRequest body, IAdminScope scope, HttpContext http, IMediator mediator, CancellationToken ct) =>
{
    var result = await mediator.Send(new AssignMerchantCommand(id, body.MerchantId, scope.Current.AdminId, http.TraceIdentifier), ct);
    return Results.Ok(result);
}).RequireAuthorization("admin").RequirePlatformUserTier(Tier.Super)
    .WithTags("Admin Admins")
    .WithName("AssignMerchantToAdmin")
    .WithSummary("Assign a merchant to an admin")
    .WithDescription("Super-only. Grant a Scoped admin access to a merchant. Inactive/unknown merchant or duplicate -> 409.")
    .Produces<AssignMerchantResult>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status409Conflict)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status403Forbidden);

// Super unassigns a merchant — a hard delete of the assignment row (REQ-4.2). Unknown assignment -> 404.
admin.MapDelete("/{id:guid}/merchants/{merchantId:guid}", async (
    Guid id, Guid merchantId, IAdminScope scope, HttpContext http, IMediator mediator, CancellationToken ct) =>
{
    await mediator.Send(new UnassignMerchantCommand(id, merchantId, scope.Current.AdminId, http.TraceIdentifier), ct);
    return Results.NoContent();
}).RequireAuthorization("admin").RequirePlatformUserTier(Tier.Super)
    .WithTags("Admin Admins")
    .WithName("UnassignMerchantFromAdmin")
    .WithSummary("Unassign a merchant from an admin")
    .WithDescription("Super-only. Hard-delete the merchant assignment row. Unknown assignment -> 404.")
    .Produces(StatusCodes.Status204NoContent)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status403Forbidden);

// Super suspends another admin; suspending your OWN account is rejected so oversight is never locked out (REQ-8.2).
admin.MapPost("/{id:guid}/suspend", async (
    Guid id, IAdminScope scope, HttpContext http, IMediator mediator, CancellationToken ct) =>
{
    if (id == scope.Current.AdminId)
        return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "An admin cannot suspend their own account.");
    await mediator.Send(new SuspendCommand(id, scope.Current.AdminId, http.TraceIdentifier), ct);
    return Results.NoContent();
}).RequireAuthorization("admin").RequirePlatformUserTier(Tier.Super)
    .WithTags("Admin Admins")
    .WithName("SuspendAdmin")
    .WithSummary("Suspend an admin")
    .WithDescription("Super-only. Suspend another admin; suspending your own account is rejected (403) so oversight is never locked out.")
    .Produces(StatusCodes.Status204NoContent)
    .ProducesProblem(StatusCodes.Status403Forbidden)
    .ProducesProblem(StatusCodes.Status401Unauthorized);

// Super reactivates a suspended admin (REQ-3). Idempotent 204; unknown id -> 404. On the Suspended->Active
// transition the target's sessions are revoked (a fresh login is required); the already-Active case revokes nothing.
admin.MapPost("/{id:guid}/reactivate", async (
    Guid id, IAdminScope scope, HttpContext http, IMediator mediator, CancellationToken ct) =>
{
    await mediator.Send(new ReactivateCommand(id, scope.Current.AdminId, http.TraceIdentifier), ct);
    return Results.NoContent();
}).RequireAuthorization("admin").RequirePlatformUserTier(Tier.Super)
    .WithTags("Admin Admins")
    .WithName("ReactivateAdmin")
    .WithSummary("Reactivate a suspended admin")
    .WithDescription("Super-only. Restore a suspended admin to Active and revoke its existing sessions (a fresh login is required). Idempotent when already Active. Unknown id -> 404.")
    .Produces(StatusCodes.Status204NoContent)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status403Forbidden);

// Super promotes/demotes another admin's tier; changing your OWN tier is rejected (mirrors REQ-8.2 — a lone
// Super demoting itself could strand oversight). Idempotent: setting the current tier is a no-op.
admin.MapPost("/{id:guid}/tier", async (
    Guid id, ChangeAdminTierRequest body, IAdminScope scope, HttpContext http, IMediator mediator, CancellationToken ct) =>
{
    if (id == scope.Current.AdminId)
        return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "An admin cannot change their own tier.");
    if (WireToTier(body.Tier) is not { } newTier)
        return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: $"Unknown tier '{body.Tier}'.");
    var result = await mediator.Send(new ChangeAdminTierCommand(id, newTier, scope.Current.AdminId, http.TraceIdentifier), ct);
    return Results.Ok(result);
}).RequireAuthorization("admin").RequirePlatformUserTier(Tier.Super)
    .WithTags("Admin Admins")
    .WithName("ChangeAdminTier")
    .WithSummary("Promote or demote an admin's tier")
    .WithDescription("Super-only. Changes an admin between scoped and super; changing your own tier is rejected (403) so oversight is never stranded. Idempotent when already at the requested tier. Unknown id -> 404.")
    .Produces<ChangeAdminTierResult>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status403Forbidden);

// Edit an admin's org-profile FKs (Position/Office/Level/Division). Full replace (a null field clears it);
// each non-null FK must reference an existing, active master -> 400 otherwise. Unknown admin -> 404. Gated
// user.manage — the write counterpart to the user.view read gate.
admin.MapPut("/{id:guid}/profile", async (
    Guid id, UpdateAdminProfileRequest body, IAdminScope scope, HttpContext http, IMediator mediator, CancellationToken ct) =>
{
    await mediator.Send(new UpdateProfileCommand(
        id, body.PositionId, body.OfficeId, body.LevelId, body.DivisionId,
        scope.Current.AdminId, http.TraceIdentifier), ct);
    return Results.NoContent();
}).RequireAuthorization("admin").RequirePermission(Keys.UserManage)
    .WithTags("Admin Admins")
    .WithName("UpdateAdminProfile")
    .WithSummary("Edit an admin's org profile")
    .WithDescription("Requires the user.manage permission. Sets Position/Office/Level/Division by master id (null clears). Unknown admin -> 404; unknown or inactive master -> 400.")
    .Produces(StatusCodes.Status204NoContent)
    .ProducesProblem(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status403Forbidden);

// --- Reference master data (Position/Office/Level/Division) ---
// Runtime CRUD for the four reference lists that back the admin org-profile FKs. Each is its own top-level API
// area (/api/v1/{positions|offices|levels|divisions}, 2026-07-20) — moved OUT of the /admins group entirely,
// mirroring D9's ProvisionMerchant/GetMerchant move above: every verb re-attaches CsrfFilter explicitly instead
// of inheriting it from the /admins group, and the credentialed admin CORS policy is re-attached via
// CorsExtensions.cs's IsAdminPlane (not here). All 5 verbs gated user.manage. DELETE is a soft-deactivate
// (IsActive=false) — masters are never hard-deleted (the AdminAccount FK is Restrict). One generic registration
// per list (delegate-parameterized since masterdata-split — the four modules share no base type; the host
// merely notices the shapes rhyme).
MapMasterCrud<IPositionStore, PositionItem>(api, "positions",
    (s, p, l, q, ct) => s.ListAsync(p, l, q, ct),
    (s, id, ct) => s.GetByIdAsync(id, ct),
    (s, c, n, ct) => s.CreateAsync(c, n, ct),
    (s, id, n, a, ct) => s.UpdateAsync(id, n, a, ct),
    (s, id, ct) => s.DeactivateAsync(id, ct),
    m => new MasterResponse(m.Id, m.Code, m.Name, m.IsActive));
MapMasterCrud<IOfficeStore, OfficeItem>(api, "offices",
    (s, p, l, q, ct) => s.ListAsync(p, l, q, ct),
    (s, id, ct) => s.GetByIdAsync(id, ct),
    (s, c, n, ct) => s.CreateAsync(c, n, ct),
    (s, id, n, a, ct) => s.UpdateAsync(id, n, a, ct),
    (s, id, ct) => s.DeactivateAsync(id, ct),
    m => new MasterResponse(m.Id, m.Code, m.Name, m.IsActive));
MapMasterCrud<ILevelStore, LevelItem>(api, "levels",
    (s, p, l, q, ct) => s.ListAsync(p, l, q, ct),
    (s, id, ct) => s.GetByIdAsync(id, ct),
    (s, c, n, ct) => s.CreateAsync(c, n, ct),
    (s, id, n, a, ct) => s.UpdateAsync(id, n, a, ct),
    (s, id, ct) => s.DeactivateAsync(id, ct),
    m => new MasterResponse(m.Id, m.Code, m.Name, m.IsActive));
MapMasterCrud<IDivisionStore, DivisionItem>(api, "divisions",
    (s, p, l, q, ct) => s.ListAsync(p, l, q, ct),
    (s, id, ct) => s.GetByIdAsync(id, ct),
    (s, c, n, ct) => s.CreateAsync(c, n, ct),
    (s, id, n, a, ct) => s.UpdateAsync(id, n, a, ct),
    (s, id, ct) => s.DeactivateAsync(id, ct),
    m => new MasterResponse(m.Id, m.Code, m.Name, m.IsActive));

static void MapMasterCrud<TStore, TItem>(RouteGroupBuilder parent, string segment,
    Func<TStore, int, int, string?, CancellationToken, Task<PagedResult<TItem>>> list,
    Func<TStore, Guid, CancellationToken, Task<TItem>> getById,
    Func<TStore, string, string, CancellationToken, Task<TItem>> create,
    Func<TStore, Guid, string, bool, CancellationToken, Task<TItem>> update,
    Func<TStore, Guid, CancellationToken, Task<TItem>> deactivate,
    Func<TItem, MasterResponse> toWire) where TStore : class
{
    // Each of the 4 standalone modules (masterdata-split) gets its own Scalar group — "Positions" etc., no
    // "Admin" prefix (these are reference lists, not admin-account operations) — instead of one shared
    // "Admin Master Data" bucket, so the split is visible in the API surface too.
    var tag = $"{char.ToUpperInvariant(segment[0])}{segment[1..]}";

    // Map the root endpoints DIRECTLY with an explicit "/{segment}" path (not a nested MapGroup + empty-string
    // root, which renders the forbidden trailing-slash canonical path — REQ-1.4; see the /api/v1 note above).
    parent.MapGet($"/{segment}", async (HttpContext http, TStore store, CancellationToken ct) =>
    {
        var p = SfsQueryParser.Parse(http.Request.Query);
        var result = await list(store, p.Page, p.Limit, p.Search?.Query, ct);
        return Results.Ok(new PagedResult<MasterResponse>(
            [.. result.Items.Select(toWire)],
            result.Page, result.Limit, result.Total));
    }).AddEndpointFilter<CsrfFilter>().RequireAuthorization("admin").RequirePermission(Keys.UserManage)
        .WithTags(tag)
        .WithName($"List{segment}")
        .WithSummary($"List {segment}")
        .Produces<PagedResult<MasterResponse>>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden);

    parent.MapGet($"/{segment}/{{id:guid}}", async (Guid id, TStore store, CancellationToken ct) =>
    {
        var item = await getById(store, id, ct);
        return Results.Ok(toWire(item));
    }).AddEndpointFilter<CsrfFilter>().RequireAuthorization("admin").RequirePermission(Keys.UserManage)
        .WithTags(tag)
        .WithName($"Get{segment}")
        .WithSummary($"Read a {segment} entry by id")
        .WithDescription("Requires the user.manage permission. Unknown id -> 404.")
        .Produces<MasterResponse>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden);

    parent.MapPost($"/{segment}", async (MasterWriteRequest body, TStore store, CancellationToken ct) =>
    {
        var item = await create(store, body.Code ?? "", body.Name ?? "", ct);
        var wire = toWire(item);
        return Results.Created($"/api/v1/{segment}/{wire.Id}", wire);
    }).AddEndpointFilter<CsrfFilter>().RequireAuthorization("admin").RequirePermission(Keys.UserManage)
        .WithTags(tag)
        .WithName($"Create{segment}")
        .WithSummary($"Create a {segment} entry")
        .WithDescription("Requires the user.manage permission. Duplicate code -> 409; code must match ^[a-z0-9_]+$ -> 400.")
        .Produces<MasterResponse>(StatusCodes.Status201Created)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden);

    parent.MapPut($"/{segment}/{{id:guid}}", async (Guid id, MasterUpdateRequest body, TStore store, CancellationToken ct) =>
    {
        var item = await update(store, id, body.Name ?? "", body.IsActive, ct);
        return Results.Ok(toWire(item));
    }).AddEndpointFilter<CsrfFilter>().RequireAuthorization("admin").RequirePermission(Keys.UserManage)
        .WithTags(tag)
        .WithName($"Update{segment}")
        .WithSummary($"Rename or (de)activate a {segment} entry")
        .WithDescription("Requires the user.manage permission. Code is immutable. Unknown id -> 404.")
        .Produces<MasterResponse>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden);

    parent.MapDelete($"/{segment}/{{id:guid}}", async (Guid id, TStore store, CancellationToken ct) =>
    {
        await deactivate(store, id, ct);
        return Results.NoContent();
    }).AddEndpointFilter<CsrfFilter>().RequireAuthorization("admin").RequirePermission(Keys.UserManage)
        .WithTags(tag)
        .WithName($"Deactivate{segment}")
        .WithSummary($"Deactivate a {segment} entry")
        .WithDescription("Requires the user.manage permission. Soft-deactivate only (sets isActive=false); existing references (FK Restrict) stay valid — never a hard delete. Unknown id -> 404.")
        .Produces(StatusCodes.Status204NoContent)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden);
}

// List an admin's sessions (REQ-4). Super-gated. Unknown admin -> 404; a real admin with none -> 200 + []. Token
// hashes never leave the store. isLive is evaluated at read time.
admin.MapGet("/{id:guid}/sessions", async (Guid id, IMediator mediator, CancellationToken ct) =>
{
    var sessions = await mediator.Send(new ListSessionsQuery(id), ct);
    return sessions is null
        ? Results.Problem(statusCode: StatusCodes.Status404NotFound)
        : Results.Ok(sessions.Select(SessionToWire).ToArray());
}).RequireAuthorization("admin").RequirePlatformUserTier(Tier.Super)
    .WithTags("Admin Admins")
    .WithName("ListPlatformUserSessions")
    .WithSummary("List an admin's sessions")
    .WithDescription("Super-only. The admin's sessions, newest first, with a read-time isLive flag. Token material is never returned. Unknown admin -> 404.")
    .Produces<IReadOnlyList<PlatformUserSessionResponse>>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status403Forbidden);

// Revoke a session (REQ-5). Super-gated. Revokes the WHOLE rotation family (a single-row revoke would leave the
// rotated successor live). Unknown session or one owned by a different admin -> 404. Idempotent 204.
admin.MapDelete("/{id:guid}/sessions/{sessionId:guid}", async (
    Guid id, Guid sessionId, IAdminScope scope, HttpContext http, IMediator mediator,
    ILoggerFactory loggerFactory, CancellationToken ct) =>
{
    var result = await mediator.Send(
        new RevokeSessionCommand(id, sessionId, scope.Current.AdminId, http.TraceIdentifier), ct);
    // Security-log the specifics the append-only audit table has no column for (REQ-5.2), keyed by correlation id.
    loggerFactory.CreateLogger("Admin.SessionManagement").LogInformation(
        "Admin session family revoked: sessionId={SessionId} familyId={FamilyId} targetAdminId={TargetAdminId} correlationId={CorrelationId}",
        result.SessionId, result.FamilyId, result.AdminId, http.TraceIdentifier);
    return Results.NoContent();
}).RequireAuthorization("admin").RequirePlatformUserTier(Tier.Super)
    .WithTags("Admin Admins")
    .WithName("RevokePlatformUserSession")
    .WithSummary("Revoke an admin's session")
    .WithDescription("Super-only. Revoke the session's entire rotation family. Unknown session, or a session owned by a different admin -> 404. Idempotent (already-revoked -> 204).")
    .Produces(StatusCodes.Status204NoContent)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status403Forbidden);

// --- Admin Role RBAC (admin-role-rbac, rf2-iam-rbac) ---
// Orthogonal to Tier: roles grant ACTIONS. Reads need only an authenticated admin (REQ-6.4); mutations are
// gated on the user.roles permission, dogfooding RequirePermission (REQ-6.3). status crosses the wire as
// "active"/"inactive" via explicit projection — there is no global string-enum converter (B2). Backed by the
// SAME Iam.Application.Roles handlers the merchant-user console uses (rf2) — RoleSideContextResolver.ForAdmin
// is the one place that turns the bound scope into RoleSideContext.Platform() (design.md).
static RoleResponse RoleToWire(RoleListItem r) => new(
    r.Code, r.Name, r.Description, r.Color,
    r.Status == RoleStatus.Active ? "active" : "inactive",
    r.PermissionKeys, r.UserCount);
// Strict: an unrecognized value (typo, blank, null) is a 400 — never a silent default to Active (B2).
static RoleStatus ParseRoleStatus(string? status) => status?.ToLowerInvariant() switch
{
    "active" => RoleStatus.Active,
    "inactive" => RoleStatus.Inactive,
    _ => throw new ArgumentException($"Invalid role status '{status}'. Expected 'active' or 'inactive'."),
};

// Permission catalog for the matrix (REQ-1.5): resource = the permission's group key.
admin.MapGet("/permissions", async (IMediator mediator, CancellationToken ct) =>
{
    var catalog = await mediator.Send(new GetPermissionCatalogQuery(Scope.Platform), ct);
    return Results.Ok(new PermissionCatalogResponse(
        catalog.Groups.Select(g => new PermissionGroupResponse(g.Key, g.LabelTh)).ToArray(),
        catalog.Permissions.Select(p => new PermissionItemResponse(p.Key, p.LabelTh, p.Resource)).ToArray()));
}).RequireAuthorization("admin")
    .WithTags("Admin Roles")
    .WithName("ListPermissions")
    .WithSummary("Permission catalog")
    .WithDescription("The permission/group catalog backing the role matrix (resource = the permission's group key).")
    .Produces<PermissionCatalogResponse>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status401Unauthorized);

admin.MapGet("/roles", async (HttpContext http, IAdminScope scope, IMediator mediator, CancellationToken ct) =>
{
    var p = SfsQueryParser.Parse(http.Request.Query);
    var result = await mediator.Send(new ListRolesQuery
    {
        Context = RoleSideContextResolver.ForAdmin(scope),
        Page = p.Page, Limit = p.Limit, Filters = p.Filters, Sort = p.Sort, Search = p.Search,
    }, ct);
    // Map items to the wire DTO by constructing a NEW PagedResult — a record with-expression cannot change T (REQ-12.2).
    return Results.Ok(new PagedResult<RoleResponse>(
        [.. result.Items.Select(RoleToWire)], result.Page, result.Limit, result.Total));
})
    .RequireAuthorization("admin")
    .WithMetadata(new SfsQueryParamsMarker())
    .WithTags("Admin Roles")
    .WithName("ListRoles")
    .WithSummary("List roles")
    .WithDescription("Paged admin roles with permissions and bound-user counts. Supports SFS: page, limit, filters, sort, search.")
    .Produces<PagedResult<RoleResponse>>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status401Unauthorized);

admin.MapGet("/roles/{code}", async (string code, IAdminScope scope, IMediator mediator, CancellationToken ct) =>
{
    var role = await mediator.Send(new GetRoleQuery(RoleSideContextResolver.ForAdmin(scope), code), ct);
    return role is null ? Results.Problem(statusCode: StatusCodes.Status404NotFound) : Results.Ok(RoleToWire(role));
}).RequireAuthorization("admin")
    .WithTags("Admin Roles")
    .WithName("GetRole")
    .WithSummary("Read a role by code")
    .WithDescription("Return a single role with its permissions. Unknown code -> 404.")
    .Produces<RoleResponse>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status401Unauthorized);

// Create: duplicate code -> 409; permission key outside catalog -> 400 (REQ-2.3/3.3).
admin.MapPost("/roles", async (
    CreateRoleRequest body, IAdminScope scope, HttpContext http, IMediator mediator, CancellationToken ct) =>
{
    var result = await mediator.Send(new CreateRoleCommand(
        RoleSideContextResolver.ForAdmin(scope), body.Code ?? "", body.Name ?? "", body.Description, body.Color,
        ParseRoleStatus(body.Status), body.Permissions ?? [], http.TraceIdentifier), ct);
    return Results.Created($"/api/v1/admins/roles/{result.Code}", RoleToWire(result));
}).RequireAuthorization("admin").RequirePermission(Keys.UserRoles)
    .WithTags("Admin Roles")
    .WithName("CreateRole")
    .WithSummary("Create a role")
    .WithDescription("Requires the user.roles permission. Duplicate code -> 409; permission key outside the catalog -> 400.")
    .Produces<RoleResponse>(StatusCodes.Status201Created)
    .ProducesProblem(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status409Conflict)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status403Forbidden);

// Update: code is immutable (taken from the route, never the body); deactivating platform_admin -> 409 (REQ-2.4/8.3).
admin.MapPut("/roles/{code}", async (
    string code, UpdateRoleRequest body, IAdminScope scope, HttpContext http, IMediator mediator, CancellationToken ct) =>
{
    var result = await mediator.Send(new UpdateRoleCommand(
        RoleSideContextResolver.ForAdmin(scope), code, body.Name ?? "", body.Description, body.Color,
        ParseRoleStatus(body.Status), body.Permissions ?? [], http.TraceIdentifier), ct);
    return Results.Ok(RoleToWire(result));
}).RequireAuthorization("admin").RequirePermission(Keys.UserRoles)
    .WithTags("Admin Roles")
    .WithName("UpdateRole")
    .WithSummary("Update a role")
    .WithDescription("Requires the user.roles permission. Code is immutable (from the route); deactivating platform_admin -> 409.")
    .Produces<RoleResponse>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status409Conflict)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status403Forbidden);

// Delete: a role with bound users is undeletable (409, REQ-4.4).
admin.MapDelete("/roles/{code}", async (
    string code, IAdminScope scope, HttpContext http, IMediator mediator, CancellationToken ct) =>
{
    await mediator.Send(new DeleteRoleCommand(RoleSideContextResolver.ForAdmin(scope), code, http.TraceIdentifier), ct);
    return Results.NoContent();
}).RequireAuthorization("admin").RequirePermission(Keys.UserRoles)
    .WithTags("Admin Roles")
    .WithName("DeleteRole")
    .WithSummary("Delete a role")
    .WithDescription("Requires the user.roles permission. A role with bound users is undeletable -> 409.")
    .Produces(StatusCodes.Status204NoContent)
    .ProducesProblem(StatusCodes.Status409Conflict)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status403Forbidden);

// Set an admin's roles to exactly the given set (REQ-4.2). Unknown role code -> 400; unknown admin -> 404.
admin.MapPut("/{id:guid}/roles", async (
    Guid id, SetAdminRolesRequest body, IAdminScope scope, HttpContext http, IMediator mediator, CancellationToken ct) =>
{
    await mediator.Send(new SetRolesCommand(id, body.RoleCodes ?? [], scope.Current.AdminId, http.TraceIdentifier), ct);
    return Results.NoContent();
}).RequireAuthorization("admin").RequirePermission(Keys.UserRoles)
    .WithTags("Admin Admins")
    .WithName("SetAdminRoles")
    .WithSummary("Set an admin's roles")
    .WithDescription("Requires the user.roles permission. Replace an admin's roles with exactly the given set. Unknown role code -> 400; unknown admin -> 404.")
    .Produces(StatusCodes.Status204NoContent)
    .ProducesProblem(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status403Forbidden);

// rf2-iam-rbac REQ-5: fail fast at boot if any RequirePermission gate references a key absent from the catalog,
// or a key whose side does not match the endpoint's own auth policy (side-aware, REQ-5.4) — one guard now covers
// both consoles, incl. the cross-catalog merchant-user.approve/reject keys gated under the "admin" policy.
PermissionParity.Assert(app.Services);

app.Run();

internal sealed record CreateRoleRequest(
    string? Code, string? Name, string? Description, string? Color, string? Status, IReadOnlyList<string>? Permissions);
internal sealed record UpdateRoleRequest(
    string? Name, string? Description, string? Color, string? Status, IReadOnlyList<string>? Permissions);
internal sealed record SetAdminRolesRequest(IReadOnlyList<string>? RoleCodes);

// --- MerchantUser BFF request/response bodies (merchant-user-google-sso REQ-15/16/17). ActingMerchant/ActingMerchantUserId are
// taken from the resolved IMerchantUserScope, never the body. ---
internal sealed record CreateMerchantUserRoleRequest(
    string? Code, string? Name, string? Description, string? Color, string? Status, IReadOnlyList<string>? Permissions);
internal sealed record UpdateMerchantUserRoleRequest(
    string? Name, string? Description, string? Color, string? Status, IReadOnlyList<string>? Permissions);
internal sealed record SetMerchantUserRolesRequest(IReadOnlyList<string>? RoleCodes);
// `Roles` = the merchant-user's ACTIVE role codes (the multi-role model's read of REQ-17.5's `role`).
internal sealed record MerchantUserMeResponse(
    Guid MerchantUserId, string Email, Guid MerchantId, IReadOnlyList<string> Roles, IReadOnlySet<string> Permissions);
// `Shared` = a Platform-seeded role visible to every merchant (Iam.Domain.Roles.Role.MerchantId is null) —
// additive field, admin-side RoleResponse has no equivalent since every Platform role IS the shared bucket.
internal sealed record MerchantUserRoleResponse(
    string Code, string Name, string? Description, string? Color, string Status,
    IReadOnlyList<string> Permissions, int UserCount, bool Shared);
internal sealed record MerchantUserPermissionCatalogResponse(
    IReadOnlyCollection<MerchantUserPermissionGroupResponse> Groups, IReadOnlyCollection<MerchantUserPermissionItemResponse> Permissions);
internal sealed record MerchantUserPermissionGroupResponse(string Key, string Label);
internal sealed record MerchantUserPermissionItemResponse(string Key, string Label, string Resource);
// Admin approve/reject of a merchant-user (REQ-6). The admin subject + correlation id are taken server-side, never the body.
internal sealed record ApproveMerchantUserRequest(string? MerchantCode, IReadOnlyList<string>? RoleCodes);
internal sealed record RejectMerchantUserRequest(string? Reason);
internal sealed record ApproveMerchantUserResponse(Guid MerchantUserId, string Status, bool AlreadyActive);
internal sealed record RejectMerchantUserResponse(Guid MerchantUserId, string Status);

internal sealed record CreateProductRequest(string Name, Money Price);
internal sealed record CreatePaymentSessionRequest(
    Guid OrderId, Money Amount, string Method, Code Psp);
internal sealed record AddItemToCartRequest(Guid ProductId, int Quantity);
internal sealed record SetCartItemQuantityRequest(int Quantity);
internal sealed record CreateCartResponse(Guid CartId);
internal sealed record StartCheckoutRequest(Guid CartId, string? Recipient);
internal sealed record OrderSummaryResponse(
    Guid OrderId, Money Amount, string Status, Guid? PaymentSessionId);

// Admin provisioning request body (reference 2.4): { "merchant": { ... }, "pspConnections": [ ... ] }.
// AdminSubject + correlation id are NOT in the body — the host sets them from the authenticated request.
internal sealed record ProvisionMerchantRequest(
    ProvisionMerchantBody? Merchant,
    IReadOnlyList<ProvisionPspConnectionRequest>? PspConnections);

// Merchant scalars are first-class columns; every other key under "merchant" (branding/routing/session/
// timezone/locale/...) is captured by JsonExtensionData and stored verbatim in the merchant Metadata.
internal sealed class ProvisionMerchantBody
{
    public string Code { get; init; } = default!;
    public string DisplayName { get; init; } = default!;
    public string LegalEntityId { get; init; } = default!;
    public string Country { get; init; } = default!;
    public string Currency { get; init; } = default!;
    public IReadOnlyList<string>? EnabledChannels { get; init; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Metadata { get; init; }
}

// "secrets" is write-only; the non-secret PSP config (environment/currencyCode/card/installment/return
// URLs/...) sits at the top level of each connection and is captured verbatim via JsonExtensionData.
internal sealed class ProvisionPspConnectionRequest
{
    public string Psp { get; init; } = default!;
    public IReadOnlyList<string>? EnabledMethods { get; init; }
    public string? MerchantId { get; init; }
    public Dictionary<string, string>? Secrets { get; init; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Config { get; init; }
}

/// <summary>Boot/request guards for admin provisioning, factored out so they can be unit-tested.</summary>
internal static class ProvisioningGuards
{
    // The field names the "secrets" envelope owns (see PspSecretEnvelopeFactory). They are write-only and
    // must NEVER appear as readable connection config; matched case-insensitively to catch casing typos.
    private static readonly HashSet<string> SecretFieldNames =
        new(StringComparer.OrdinalIgnoreCase) { "secretKey", "publicKey", "webhookSecret" };

    /// <summary>Rejects a connection whose non-secret config bag contains a secret-owned field (a payload
    /// that put a credential beside, not inside, "secrets") so it can never persist/echo as readable config.</summary>
    public static void RejectSecretsInConfig(IReadOnlyDictionary<string, JsonElement>? config)
    {
        if (config is null)
            return;

        foreach (var key in config.Keys)
            if (SecretFieldNames.Contains(key))
                throw new ArgumentException(
                    $"'{key}' is a secret field and must be inside 'secrets', not at the connection top level.");
    }

    /// <summary>Fails fast when a connection string has no usable credential — a blank SQL-auth password
    /// means the runtime secret was never injected. Integrated security needs no password.</summary>
    public static void RequireInjectedCredential(string connectionString, string name)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        if (!builder.IntegratedSecurity && string.IsNullOrEmpty(builder.Password))
            throw new InvalidOperationException(
                $"ConnectionStrings:{name} has no password — the runtime secret was not injected. Set ConnectionStrings__{name}.");
    }

    /// <summary>Fails fast when the admin OIDC confidential client id is unmapped. The admin BFF login cannot
    /// build an authorization request without it.</summary>
    public static void RequireConfidentialClientId(string? clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId) || clientId.StartsWith("REPLACE_WITH_", StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Google:Oidc:ClientId is required for the admin BFF login. Map it via Google__Oidc__ClientId.");
    }

    /// <summary>Fails fast when the admin OIDC client secret was not injected (blank or a committed placeholder).
    /// The error never echoes the value (REQ-8.3).</summary>
    public static void RequireConfidentialClientSecret(string? clientSecret)
    {
        if (string.IsNullOrWhiteSpace(clientSecret) || clientSecret.StartsWith("REPLACE_WITH_", StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Google:Oidc:ClientSecret is required for the admin BFF login — the runtime secret was not " +
                "injected. Set Google__Oidc__ClientSecret (environment / user-secrets / Vault).");
    }

    /// <summary>Fails fast when a CONFIGURED merchant-user OIDC client id is a committed placeholder (a blank id is
    /// allowed — it disables the merchant-user scheme, REQ-14.2). Only called when the id is non-blank.</summary>
    public static void RequireMerchantUserClientId(string? clientId)
    {
        if (clientId is not null && clientId.StartsWith("REPLACE_WITH_", StringComparison.Ordinal))
            throw new InvalidOperationException(
                "MerchantUser:Oidc:ClientId is a placeholder. Map a real client id via MerchantUser__Oidc__ClientId, or " +
                "leave it blank to disable the merchant-user OIDC login scheme.");
    }

    /// <summary>Fails fast when a merchant-user OIDC client id is configured but its secret was not injected (blank or a
    /// committed placeholder). The error never echoes the value (REQ-14.1/14.3).</summary>
    public static void RequireMerchantUserClientSecret(string? clientSecret)
    {
        if (string.IsNullOrWhiteSpace(clientSecret) || clientSecret.StartsWith("REPLACE_WITH_", StringComparison.Ordinal))
            throw new InvalidOperationException(
                "MerchantUser:Oidc:ClientSecret is required when a MerchantUser:Oidc:ClientId is configured — the runtime " +
                "secret was not injected. Set MerchantUser__Oidc__ClientSecret (environment / user-secrets / Vault).");
    }
}

// Admin identity foundation request bodies (REQ-3/4). ActingAdminId + correlation id are NOT in the body —
// the host sets them from the resolved IAdminScope + the authenticated request.
internal sealed record CreateAdminRequest(
    string Email, Guid? PositionId = null, Guid? OfficeId = null, Guid? LevelId = null, Guid? DivisionId = null);
internal sealed record AssignMerchantRequest(Guid MerchantId);
internal sealed record ChangeAdminTierRequest(string Tier);
// Org-profile edit + master-data CRUD (admin-account-management: profile FKs). Master code is set at create,
// immutable thereafter; update only renames / toggles active.
internal sealed record UpdateAdminProfileRequest(Guid? PositionId, Guid? OfficeId, Guid? LevelId, Guid? DivisionId);
internal sealed record MasterWriteRequest(string? Code, string? Name);
internal sealed record MasterUpdateRequest(string? Name, bool IsActive);
internal sealed record MasterResponse(Guid Id, string Code, string Name, bool IsActive);
internal sealed record MasterRefResponse(Guid Id, string Code, string Name);

internal sealed record CreateProductResponse(Guid ProductId);
internal sealed record CreatePaymentSessionResponse(Guid PaymentSessionId);
internal sealed record StartRedirectResponse(string RedirectUrl);
internal sealed record WebhookResponse(string Outcome);

// Admin read responses — named records (not anonymous objects) so the OpenAPI doc carries a response schema
// Scalar can render. Wire shape matches the previous anonymous objects (camelCase via the web JSON defaults).
internal sealed record AdminMeResponse(
    Guid AdminId, string Email, string Tier, AdminAccessibleResponse AccessibleMerchants,
    IReadOnlySet<string> Permissions);
internal sealed record AdminAccessibleResponse(
    bool IsUnrestricted,
    // Omitted entirely (not null) for a Super, matching the prior shape.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyCollection<AdminAccessibleMerchantResponse>? Merchants);
internal sealed record AdminAccessibleMerchantResponse(Guid Id, string? Code);
// admin-account-management REQ-1.2/1.6: one admin directory row; tier/status are lowercase wire strings.
internal sealed record AdminListItemResponse(
    Guid AdminId, string Email, string Tier, string Status, DateTime CreatedAt, bool SubjectBound);
// admin-account-management REQ-2.1: full detail. The accessible-merchants field is named AccessibleMerchants to match
// GET /me's AdminMeResponse exactly (same nested DTO AND same JSON key), so a client can share one renderer.
internal sealed record AdminDetailResponse(
    Guid AdminId, string Email, string Tier, string Status, DateTime CreatedAt, bool SubjectBound,
    AdminAccessibleResponse AccessibleMerchants, IReadOnlyList<string> RoleCodes,
    MasterRefResponse? Position, MasterRefResponse? Office, MasterRefResponse? Level, MasterRefResponse? Division);
// admin-account-management REQ-4.2: one session row; status is a lowercase wire string; NO token material.
internal sealed record PlatformUserSessionResponse(
    Guid SessionId, Guid FamilyId, string Status, DateTime IssuedAt, DateTime IdleExpiresAt,
    DateTime AbsoluteExpiresAt, string? CreatedIp, string? UserAgent, bool IsLive);
internal sealed record PermissionCatalogResponse(
    IReadOnlyCollection<PermissionGroupResponse> Groups, IReadOnlyCollection<PermissionItemResponse> Permissions);
internal sealed record PermissionGroupResponse(string Key, string Label);
internal sealed record PermissionItemResponse(string Key, string Label, string Resource);
internal sealed record RoleResponse(
    string Code, string Name, string? Description, string? Color, string Status,
    IReadOnlyList<string> Permissions, int UserCount);

// Bridges PspCode <-> its stable wire code via the domain's single-source-of-truth PspCodes mapping,
// so the host owns the serialization concern and the domain enum stays attribute-free.
internal sealed class PspCodeJsonConverter : JsonConverter<Code>
{
    public override Code Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var code = reader.GetString() ?? throw new JsonException("psp must be a string code.");
        try { return Codes.FromCode(code); }
        catch (ArgumentException ex) { throw new JsonException(ex.Message); } // unknown code -> 400, not 500
    }

    public override void Write(Utf8JsonWriter writer, Code value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToCode());
}

/// <summary>Exposed so <c>WebApplicationFactory&lt;Program&gt;</c> can boot the host in tests.</summary>
public partial class Program
{
    // Strip a route constraint from a path segment ("{cartId:guid}" -> "{cartId}") so an ApiDescription's
    // RelativePath matches the OpenAPI document's path key. Source-generated so the pattern compiles once.
    [GeneratedRegex(@"\{([^:}]+):[^}]+\}")]
    private static partial Regex RouteConstraintRegex();
}
