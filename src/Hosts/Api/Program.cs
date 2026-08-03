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
using Checkouts.Domain.Items;
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
using Orders.Domain.Items;
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
using Products.Domain;
using Products.Infrastructure;
using Products.Infrastructure.Sp;
// Scalar.AspNetCore also has a DocumentType — the wire enum below is the domain's.
using DocumentType = Products.Domain.DocumentType;
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
using GetRegistrationHistoryQuery = Merchants.Application.Users.GetRegistrationHistoryQuery;
using RegistrationHistoryResult = Merchants.Application.Users.RegistrationHistoryResult;
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
using Api.BackgroundDispatch;
using Api.Iam;
using Api.Merchants;
using Api.Persistence;
using Api.Webhooks;
using Persistence.ControlPlane;
using Persistence.MerchantRuntime;
using Persistence.MerchantRuntime.Outbox;
using Persistence.MerchantUsers;
using Persistence.MerchantUsers.Outbox;
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

// Document-search upstream. Unless a deployment names its own connection strings, both default to the app
// connection re-pointed at the simulated catalogues that live beside the app database on the same instance
// — so no environment gains a variable for them (REQ-3.4) and every host still boots when the simulated
// databases are absent (nothing connects until a search request arrives). Pointing at the real
// motordb/centerdb on cutover day is an override of these two values, not a code change.
builder.Services.Configure<SpDocumentOptions>(builder.Configuration.GetSection(SpDocumentOptions.SectionName));
builder.Services.PostConfigure<SpDocumentOptions>(spDocument =>
{
    if (string.IsNullOrWhiteSpace(spDocument.MotorConnectionString))
        spDocument.MotorConnectionString =
            new SqlConnectionStringBuilder(appConnString) { InitialCatalog = "hippodb" }.ConnectionString;
    if (string.IsNullOrWhiteSpace(spDocument.NonMotorConnectionString))
        spDocument.NonMotorConnectionString =
            new SqlConnectionStringBuilder(appConnString) { InitialCatalog = "mammothdb" }.ConnectionString;
});

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
    // The BFF logins are confidential OIDC clients (up to provider × side registrations). For EVERY configured
    // provider (non-blank ClientId) the id must not be a committed placeholder and its secret MUST be injected —
    // fail fast at boot rather than on the first login (REQ-8.1/8.2/14.1/14.2). Admin additionally requires at
    // least ONE configured provider (an admin console with no login is a dead deploy); a merchant-user side with
    // zero providers is allowed — that login may be intentionally disabled (the schemes are skipped, REQ-14.2).
    ProvisioningGuards.RequireOidcProviders(builder.Configuration, "AdminAuth", requireAtLeastOne: true);
    ProvisioningGuards.RequireOidcProviders(builder.Configuration, "MerchantAuth", requireAtLeastOne: false);
    // The webhook URL each PSP charge calls back on is derived from this origin per connection
    // (captive-payment-alignment REQ-4.1/4.3) — a blank value ships charges whose confirmation never
    // reaches us, so the order stays AwaitingPayment after the customer has already paid.
    ProvisioningGuards.RequirePublicBaseUrl(builder.Configuration);
}

// The 3 runtime clusters + the Provisioning UoW (task 8.5.7), all on the single pol_app connection. The
// write-floor authorizer differs per capability (WriteAuthorizers.cs): an ordinary admin-console request
// (ControlPlaneDbContext), an ordinary merchant request (MerchantUserDbContext/MerchantRuntimeDbContext), or
// the ONE cross-context provisioning writer — a single instance, since ProvisioningCoordinator constructs
// its own context instances per attempt rather than resolving them from this container.
builder.Services.AddControlPlanePersistence(
    appConnString, sp => new ControlPlaneAdminWriteAuthorizer(sp.GetRequiredService<IAdminScope>()));

// multi-tier-deployment task 1: the outbox dispatchers (formerly the standalone Worker host's hosted
// services, PLAN "Worker merge") now run in THIS process, draining from a background-created scope with no
// HttpContext. Same scope-discriminated selection as IActorContext below (BackgroundDispatchScope) — an
// HTTP request gets the ordinary merchant-request write floor, a background dispatch scope gets the
// cross-merchant drain capability (WorkerWriteAuthorizer).
builder.Services.AddMerchantUserPersistence(appConnString, ResolveMerchantWriteAuthorizer)
    .AddMerchantUserOutboxDispatcher();
builder.Services.AddMerchantRuntimePersistence(appConnString, ResolveMerchantWriteAuthorizer)
    .AddMerchantRuntimeOutboxDispatcher();

// Three-way selection (bugfix-merchant-prebind-wiring F3): background dispatch scope → the cross-merchant
// drain capability; HTTP with a bound admin scope → the narrow admin approval capability (approve/reject
// write set only — an admin request has no bound merchant actor, so the ordinary merchant floor would deny
// the approve write unconditionally); any other HTTP request → the ordinary merchant-request floor. The
// admin-vs-merchant split is decided per write inside HttpMerchantWriteAuthorizer, not at context
// construction (the context may be constructed before authentication binds the scope).
static IWriteAuthorizer ResolveMerchantWriteAuthorizer(IServiceProvider sp) =>
    BackgroundDispatchScope.IsHttpRequest(sp)
        ? new HttpMerchantWriteAuthorizer(sp.GetRequiredService<IAdminScope>(), sp.GetRequiredService<IActorContext>())
        : new WorkerWriteAuthorizer();

// policy-reference-record REQ-3.2-admin: a SEPARATE MerchantRuntimeDbContext instance, built with
// AdminItemPolicyWriteAuthorizer(IAdminScope) instead of the ambient MerchantRequestWriteAuthorizer above —
// an admin request has no bound merchant (HasActor=false), so the ambient write floor denies it
// unconditionally regardless of entity type (mirror AddProvisioning's per-capability context factory).
builder.Services.AddAdminItemPolicyWriter(
    appConnString, sp => new AdminItemPolicyWriteAuthorizer(sp.GetRequiredService<IAdminScope>()));

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

// MerchantUser BFF: a SECOND set of confidential OIDC clients (Authorization Code + PKCE) for the server-side
// merchant-user login, fully isolated from the Admin ones — distinct "MerchantUser{Provider}" schemes + callbacks +
// cookie names (REQ-8/9/14). Adds the schemes WITHOUT changing the default; a blank ClientId skips that provider's
// scheme so a half-configured env does not fault the whole host (REQ-14.2). The merchant-user session lifetime +
// cookie posture come from MerchantUser:Session.
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

// multi-tier-deployment task 1: HTTP requests resolve HttpActorContext (unchanged); a scope the outbox
// dispatcher creates for a background batch (no HttpContext) resolves WorkerActorContext instead — the
// framework primitive already registered above is the discriminator, no new interface needed. This is the
// highest-risk part of the Worker merge (see design.md) — it sits directly on the
// GuardedRuntimeDbContext/IWriteAuthorizer security boundary, hence the dedicated composition-root tests.
builder.Services.AddScoped<HttpActorContext>();
builder.Services.AddScoped<WorkerActorContext>();
builder.Services.AddScoped<IActorContext>(sp =>
    BackgroundDispatchScope.IsHttpRequest(sp)
        ? sp.GetRequiredService<HttpActorContext>()
        : sp.GetRequiredService<WorkerActorContext>());

// Google id-token Bearer is retired for the funnel (T11 — rf1 big-bang, no legacy audience). The
// MerchantUserSession cookie scheme is the explicit default (each protected group still pins its own scheme via
// its policy — the default only matters for UseAuthentication's principal-population pass).
builder.Services.AddAuthentication(UserSessionAuthenticationHandler.SchemeName);

// Admin BFF: confidential OIDC clients (Authorization Code + PKCE) for the server-side admin login.
// Adds the "Admin{Provider}" OIDC + "oidc-noop" sign-in schemes WITHOUT changing the default set above.
builder.Services.Configure<AdminAuthOptions>(builder.Configuration.GetSection(AdminAuthOptions.SectionName));
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
        // Products reads only page/limit + its own typed productFilters — no SFS surface to advertise (REQ-7.4).
        if (context.Description.ActionDescriptor.EndpointMetadata.OfType<ProductQueryParamsMarker>().Any())
            SfsOpenApi.AddProductQueryParameters(operation);
        return Task.CompletedTask;
    });

    // Document-level: title/description + the security schemes other teams pick from Scalar's auth dropdown,
    // plus the per-operation security requirement each route's authorization policy implies.
    options.AddDocumentTransformer((document, context, _) =>
    {
        document.Info.Title = "pol-core API";
        document.Info.Version = "v1";
        document.Info.Description =
            "ระบบจัดการการชำระเงินแบบ captive (redirect-only, PCI SAQ A) ประกอบด้วยหน้าร้านฝั่ง Merchant, Admin BFF และ MerchantUser BFF";

        // x-tagGroups: nest the 18 route tags under the 12 src/Modules/* business modules that back them
        // (see docs/reference/src-structure.md §4 for the tag-to-module map), instead of a flat tag list.
        // Webhooks rides on Payments and Auth/MerchantUser Auth ride on the Admins/Merchants identity
        // modules (their BFF plumbing lives in Hosts/Api/*, but the operations are that module's concern) —
        // both have no module folder of their own, so they group under the module whose data they touch.
        // Group/tag display names are Thai (Scalar sidebar content); "Webhooks" and "Iam" stay English —
        // established protocol/security acronyms, not translated business content.
        document.Extensions ??= new Dictionary<string, IOpenApiExtension>();
        document.Extensions["x-tagGroups"] = new JsonNodeExtension(JsonNode.Parse("""
            [
              { "name": "ผลิตภัณฑ์", "tags": ["ผลิตภัณฑ์"] },
              { "name": "ตะกร้าสินค้า", "tags": ["ตะกร้าสินค้า"] },
              { "name": "เช็คเอาต์", "tags": ["เช็คเอาต์"] },
              { "name": "คำสั่งซื้อ", "tags": ["คำสั่งซื้อ", "คำสั่งซื้อ (ผู้ดูแลระบบ)"] },
              { "name": "การชำระเงิน", "tags": ["การชำระเงิน", "Webhooks"] },
              { "name": "ร้านค้า", "tags": ["ร้านค้า (ผู้ดูแลระบบ)", "การเข้าสู่ระบบ (ผู้ใช้ร้านค้า)", "ผู้ใช้ร้านค้า (ผู้ดูแลระบบ)"] },
              { "name": "ผู้ดูแลระบบ", "tags": ["ผู้ดูแลระบบ", "การเข้าสู่ระบบ"] },
              { "name": "Iam", "tags": ["บทบาท (ผู้ดูแลระบบ)", "บทบาท (ผู้ใช้ร้านค้า)"] },
              { "name": "แผนก", "tags": ["แผนก"] },
              { "name": "ระดับ", "tags": ["ระดับ"] },
              { "name": "สำนักงาน", "tags": ["สำนักงาน"] },
              { "name": "ตำแหน่ง", "tags": ["ตำแหน่ง"] }
            ]
            """)!);

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
            Description = "คุกกี้ session ของ Admin BFF ที่ออกโดย OIDC login flow (GET /api/v1/admins/auth/login) "
                + "ตั้งค่าให้อัตโนมัติในเบราว์เซอร์ บน production (HTTPS) จะใช้ชื่อคุกกี้ `__Host-adm_session`",
        };
        document.Components.SecuritySchemes["MerchantUserSession"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey,
            In = ParameterLocation.Cookie,
            Name = UserSessionCookies.SessionCookieNameDevHttp,
            Description = "คุกกี้ session ของ MerchantUser BFF ที่ออกโดย OIDC login flow (GET /api/v1/merchants/users/auth/login) "
                + "ตั้งค่าให้อัตโนมัติในเบราว์เซอร์ บน production (HTTPS) จะใช้ชื่อคุกกี้ `__Host-mch_session` (T11 — "
                + "single-scheme แล้ว ของเดิมที่ fallback เป็น Bearer ถูก retired ไปแล้ว)",
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
    // Product document enums (ProductGroup/DocumentType/PaymentStatus) cross the wire as their uppercase
    // member names (the VCentralPay SP contract values). PspCode keeps its dedicated converter above.
    // allowIntegerValues:false rejects numeric tokens like {"productGroup":99} at bind time (400) so a
    // caller cannot smuggle an out-of-contract enum value past the string contract.
    o.SerializerOptions.Converters.Add(
        new System.Text.Json.Serialization.JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false));
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
// to the handler, which verifies -> fetches-to-confirm -> claims idempotency -> transitions -> enqueues
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
    .WithSummary("Webhook callback จาก PSP")
    .WithDescription("ตรวจสอบลายเซ็นของ PSP, claim idempotency, ยืนยันการชำระเงิน แล้ว emit event PaymentPaid โดย route ตาม trusted connection id หากไม่พบ id -> 404, ลายเซ็นไม่ถูกต้อง -> 401")
    .Produces<WebhookResponse>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status429TooManyRequests);

// T11 (rf1 big-bang): the merchant-Bearer fallback is retired, so every write endpoint gates on the single-scheme
// "merchant-user" policy + its permission unconditionally — the former MerchantUser:EnforcePermissionsOnWrites
// toggle (a transitional un-gated Bearer state) no longer has a Bearer path to fall back to, so it is deleted.

// GET /products — the document catalogue, and the ONLY product endpoint: the catalogue is read-only over HTTP
// because the documents originate in the upstream policy system, not from a merchant filling in a form. The
// write seam is CreateProductCommand, reachable from an importer/tests but deliberately not mapped to a route.
// It carries no merchant of its own, so the request is scoped by the mandatory saleCode inside productFilters and
// gated by the merchant-user policy; the input surface is exactly SP guide §2: paging plus the typed
// productFilters (REQ-7.1). The search itself runs against the upstream procedures and each page is mirrored
// into shop.Products on the way out, so 503 is a real outcome here: the upstream being unreachable is not a
// 500 of ours (products-sp-gateway REQ-7.1/8.4).
api.MapGet("/products", async (HttpContext http, IMediator mediator, CancellationToken ct) =>
{
    var p = SfsQueryParser.ParsePaging(http.Request.Query);
    var result = await mediator.Send(new ListProductsQuery
    {
        Page = p.Page, Limit = p.Limit,
        ProductFilters = ProductFilterDto.Parse(http.Request.Query["productFilters"]),
    }, ct);
    return Results.Ok(result);
})
    .RequireAuthorization("merchant-user")
    .WithMetadata(new ProductQueryParamsMarker())
    .WithTags("ผลิตภัณฑ์")
    .WithName("ListProducts")
    .WithSummary("รายการผลิตภัณฑ์")
    .WithDescription("รายการเอกสารประกันแบบแบ่งหน้า ค้นสดจากระบบต้นทางแล้วบันทึกลงแคตตาล็อกกลาง รับ page, limit และ productFilters (บังคับ — ต้องมี saleCode ซึ่งเป็นตัวจำกัดขอบเขตข้อมูล) ตาม §2 ของเอกสาร SP; ต้องระบุ insuranceType (Motor|NonMotor) เมื่อไม่ได้ส่ง productGroup และห้ามขัดแย้งกับ productGroup; countMode (EXACT|FAST ค่าเริ่มต้น EXACT) — FAST ให้ totalRows/totalPages เป็น null; ตอบเป็น envelope §5.1 ที่คัดลอกค่ามาจากระบบต้นทาง; ไม่รองรับ filters/sort/search")
    .Produces<ProductPage>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

// Cart — open, add/merge lines, review, adjust, clear. Merchant comes from the principal; the commands are
// IMerchantScoped so RLS + the merchant guard confine every cart to the bound merchant.
api.MapPost("/carts", async (IActorContext actor, IMediator mediator, CancellationToken ct) =>
{
    var id = await mediator.Send(new CreateCartCommand(actor.MerchantId), ct);
    return TypedResults.Ok(new CreateCartResponse(id));
}).RequireAuthorization("merchant-user").RequireUserCsrf()
    .WithTags("ตะกร้าสินค้า")
    .WithName("CreateCart")
    .WithSummary("เปิดตะกร้าสินค้า")
    .WithDescription("เปิดตะกร้าสินค้าเปล่าใหม่ให้กับร้านค้าที่ยืนยันตัวตนแล้ว")
    .Produces<CreateCartResponse>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status401Unauthorized);

api.MapPost("/carts/{cartId:guid}/items", async (
    Guid cartId, AddItemToCartRequest body, IActorContext actor, IMediator mediator, CancellationToken ct) =>
{
    // The unit price is the catalog's, NEVER the client's: look the product up first and price the line
    // from it (the cart is "selected plans + quote", reference 2.4). A document that is not UNPAID is already
    // sold, so it cannot be added -> 400 (REQ-2.1).
    var product = await mediator.Send(new GetProductByIdQuery(body.ProductId), ct);
    if (product is null || product.PaymentStatus != PaymentStatus.UNPAID)
        return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Unknown or inactive product.");

    // The single currency boundary: Product carries a bare decimal (§5.2), while Cart/Checkout/Order/
    // PaymentSession stay Money{Amount,Currency}, so THB is minted here and nowhere else (REQ-8.4).
    var result = await mediator.Send(new AddItemToCartCommand(
        cartId, actor.MerchantId, body.ProductId, body.Quantity, Money.Of(product.TotalPremium, "THB")), ct);
    return Results.Ok(result);
}).RequireAuthorization("merchant-user").RequireUserCsrf()
    .WithTags("ตะกร้าสินค้า")
    .WithName("AddCartItem")
    .WithSummary("เพิ่มรายการสินค้าในตะกร้า")
    .WithDescription("เพิ่ม product line จากแคตตาล็อกเข้าตะกร้า โดยตั้งราคาตามแคตตาล็อก หากไม่พบผลิตภัณฑ์หรือไม่ active -> 400")
    .Produces<AddItemResult>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status401Unauthorized);

api.MapGet("/carts/{cartId:guid}", async (
    Guid cartId, IActorContext actor, IMediator mediator, CancellationToken ct) =>
{
    var view = await mediator.Send(new GetCartQuery(cartId, actor.MerchantId), ct);
    return view is null ? Results.NotFound() : Results.Ok(view);
}).RequireAuthorization("merchant-user")
    .WithTags("ตะกร้าสินค้า")
    .WithName("GetCart")
    .WithSummary("ดูตะกร้าสินค้า")
    .WithDescription("คืนตะกร้าพร้อม line ทั้งหมดและ subtotal ที่คำนวณราคาแล้ว หากไม่พบตะกร้า -> 404")
    .Produces<CartView>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status401Unauthorized);

api.MapDelete("/carts/{cartId:guid}/items/{productId:guid}", async (
    Guid cartId, Guid productId, IActorContext actor, IMediator mediator, CancellationToken ct) =>
{
    var view = await mediator.Send(new RemoveItemFromCartCommand(cartId, actor.MerchantId, productId), ct);
    return TypedResults.Ok(view);
}).RequireAuthorization("merchant-user").RequireUserCsrf()
    .WithTags("ตะกร้าสินค้า")
    .WithName("RemoveCartItem")
    .WithSummary("ลบรายการในตะกร้า")
    .WithDescription("ลบ product line ออกจากตะกร้า แล้วคืนตะกร้าที่อัปเดตแล้ว")
    .Produces<CartView>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status401Unauthorized);

api.MapPut("/carts/{cartId:guid}/items/{productId:guid}", async (
    Guid cartId, Guid productId, SetCartItemQuantityRequest body, IActorContext actor, IMediator mediator, CancellationToken ct) =>
{
    var view = await mediator.Send(new SetCartItemQuantityCommand(cartId, actor.MerchantId, productId, body.Quantity), ct);
    return TypedResults.Ok(view);
}).RequireAuthorization("merchant-user").RequireUserCsrf()
    .WithTags("ตะกร้าสินค้า")
    .WithName("SetCartItemQuantity")
    .WithSummary("ปรับจำนวนรายการในตะกร้า")
    .WithDescription("ปรับจำนวนของ product line แล้วคืนตะกร้าที่อัปเดตแล้ว")
    .Produces<CartView>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status401Unauthorized);

api.MapPost("/carts/{cartId:guid}/clear", async (
    Guid cartId, IActorContext actor, IMediator mediator, CancellationToken ct) =>
{
    var view = await mediator.Send(new ClearCartCommand(cartId, actor.MerchantId), ct);
    return TypedResults.Ok(view);
}).RequireAuthorization("merchant-user").RequireUserCsrf()
    .WithTags("ตะกร้าสินค้า")
    .WithName("ClearCart")
    .WithSummary("ล้างตะกร้าสินค้า")
    .WithDescription("ลบทุก line ออกจากตะกร้า แล้วคืนตะกร้าที่ว่างแล้ว")
    .Produces<CartView>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status401Unauthorized);

// Checkout. Start prices the checkout from the CART's subtotal (never a client-supplied amount), captures
// an optional notification recipient, then Confirm emits CheckoutConfirmed -> Orders opens the order.
// insurance-pivot REQ-6/7: the client supplies ONLY identity + insured-person PII per line — UnitPrice
// comes from the cart, SumInsured/CoverageDurationDays/Insurer come from the server-side GetProductByIdQuery
// (never the client, same trust boundary Price already has).
api.MapPost("/checkouts", async (
    StartCheckoutRequest body, IActorContext actor, IMediator mediator, CancellationToken ct) =>
{
    var cart = await mediator.Send(new GetCartQuery(body.CartId, actor.MerchantId), ct);
    if (cart is null)
        return Results.NotFound();
    // A cart already frozen by a live checkout cannot start a second one (REQ-2.2). The cart is reopened by
    // POST /checkouts/{id}/abandon, so this is recoverable, not terminal.
    if (cart.Status != nameof(Carts.Domain.CartStatus.Open))
        return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "The cart is not open.");
    if (cart.Subtotal is not { } subtotal)
        return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Cannot check out an empty cart.");

    // 1 insured person per line (locked decision) -> every cart line must be quantity 1.
    if (cart.Items.Any(i => i.Quantity != 1))
        return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Insurance items must have quantity 1.");

    var cartProductIds = cart.Items.Select(i => i.ProductId).ToHashSet();
    var insuredProductIds = body.InsuredPersons.Select(p => p.ProductId).ToList();
    if (insuredProductIds.Count != insuredProductIds.Distinct().Count())
        return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Duplicate ProductId in insuredPersons.");
    if (!cartProductIds.SetEquals(insuredProductIds))
        return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "insuredPersons must cover every cart line exactly once.");

    var items = new List<CheckoutItemInput>();
    foreach (var item in cart.Items)
    {
        var product = await mediator.Send(new GetProductByIdQuery(item.ProductId), ct);
        if (product is null || product.PaymentStatus != PaymentStatus.UNPAID)   // already sold -> 409 (REQ-2.1)
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "A cart product is no longer available.");

        var person = body.InsuredPersons.Single(p => p.ProductId == item.ProductId);
        items.Add(new CheckoutItemInput(
            item.ProductId, item.Quantity, item.UnitPrice,
            product.DocumentNo, product.ProductGroup.ToString(), product.DocumentType.ToString(),
            product.PolicyNumber, product.StartDate, product.EndDate,
            person.FirstName, person.LastName, person.IdNumber, person.DateOfBirth));
    }

    var result = await mediator.Send(
        new StartCheckoutCommand(actor.MerchantId, body.CartId, subtotal, items, body.Recipient), ct);

    // Second unit of work, by design (REQ-2.1): the snapshot is already frozen inside the session above, and
    // IX_CheckoutSessions_CartId_Open blocks a second checkout even if this freeze never lands — a cart left
    // Open with a live session is recovered by abandoning it. The freeze carries the Version the snapshot
    // was read at: a cart edit that slipped in anywhere between GetCartQuery and this commit loses to the
    // concurrency token (PR #166 review), the just-opened session is abandoned, and the merchant retries
    // from the cart's current state.
    try
    {
        await mediator.Send(new MarkCartCheckedOutCommand(body.CartId, actor.MerchantId, cart.Version), ct);
    }
    catch (ConcurrencyConflictException)
    {
        await mediator.Send(new AbandonCheckoutCommand(result.CheckoutSessionId, actor.MerchantId), ct);
        return Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "The cart changed while checkout was starting; try again.");
    }
    return Results.Ok(result);
}).RequireAuthorization("merchant-user").RequireUserCsrf()
    .WithTags("เช็คเอาต์")
    .WithName("StartCheckout")
    .WithSummary("เริ่มเช็คเอาต์")
    .WithDescription("คำนวณราคาเช็คเอาต์จาก subtotal ของตะกร้า (ไม่ใช้จำนวนเงินจาก client) พร้อม snapshot เงื่อนไขประกันฝั่ง server แล้วตรึงตะกร้าเป็น CheckedOut หากไม่พบตะกร้า -> 404, ตะกร้าว่าง/ไม่ตรงกัน/qty!=1 -> 400, ตะกร้าไม่ได้เปิดอยู่/มี checkout ที่ยังไม่ปิด/ผลิตภัณฑ์ไม่ active -> 409")
    .Produces<StartCheckoutResult>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status409Conflict)
    .ProducesProblem(StatusCodes.Status401Unauthorized);

api.MapPost("/checkouts/{checkoutSessionId:guid}/confirm", async (
    Guid checkoutSessionId, IActorContext actor, IMediator mediator, CancellationToken ct) =>
{
    var result = await mediator.Send(new ConfirmCheckoutCommand(checkoutSessionId, actor.MerchantId), ct);
    return Results.Ok(result);
}).RequireAuthorization("merchant-user").RequireUserCsrf()
    .WithTags("เช็คเอาต์")
    .WithName("ConfirmCheckout")
    .WithSummary("ยืนยันเช็คเอาต์")
    .WithDescription("ยืนยัน checkout session แล้ว emit event CheckoutConfirmed เพื่อให้ Orders เปิดคำสั่งซื้อ")
    .Produces<ConfirmCheckoutResult>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status401Unauthorized);

// Abandon = the way out of a checkout the merchant no longer wants (REQ-2.5-2.9). Two units of work, the
// mirror image of start: the session goes Abandoned first, then the cart is reopened. Both halves are
// no-ops when already in the target state, so a retry after a half-completed call finishes the job.
api.MapPost("/checkouts/{checkoutSessionId:guid}/abandon", async (
    Guid checkoutSessionId, IActorContext actor, IMediator mediator, CancellationToken ct) =>
{
    var result = await mediator.Send(new AbandonCheckoutCommand(checkoutSessionId, actor.MerchantId), ct);
    await mediator.Send(new ReopenCartCommand(result.CartId, actor.MerchantId), ct);
    return Results.Ok(result);
}).RequireAuthorization("merchant-user").RequireUserCsrf()
    .WithTags("เช็คเอาต์")
    .WithName("AbandonCheckout")
    .WithSummary("ยกเลิกเช็คเอาต์")
    .WithDescription("ยกเลิก checkout session ที่ยังไม่ยืนยัน แล้วปลดล็อกตะกร้ากลับเป็น Open เรียกซ้ำบน session ที่ยกเลิกไปแล้วตอบสำเร็จโดยไม่เปลี่ยนอะไร หากไม่พบ session -> 404, session ยืนยันไปแล้ว -> 409")
    .Produces<AbandonCheckoutResult>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status409Conflict);

var createPaymentSession = api.MapPost("/payments/sessions", async (
    CreatePaymentSessionRequest body,
    IActorContext actor,
    IMediator mediator,
    CancellationToken ct) =>
{
    var result = await mediator.Send(new CreateSessionCommand(
        body.OrderId, actor.MerchantId, body.Method, body.Psp), ct);
    return TypedResults.Ok(new CreatePaymentSessionResponse(result.PaymentSessionId));
});
createPaymentSession.RequireAuthorization("merchant-user").RequirePermission(Keys.PaymentCreate).RequireUserCsrf()
    .WithTags("การชำระเงิน")
    .WithName("CreatePaymentSession")
    .WithSummary("สร้าง payment session")
    .WithDescription("เปิด payment session ให้คำสั่งซื้อตาม method/PSP ที่เลือก โดยยอดเงินอ่านจากแถว order ฝั่ง server เท่านั้น (ไม่รับจาก body) ต้องมี merchant-user policy + สิทธิ์ payment.create หาก method ไม่ใช่รหัส canonical (card/promptpay/installment) -> 400, ไม่พบคำสั่งซื้อ -> 404, คำสั่งซื้อไม่ได้รอชำระ/ไม่มี PSP connection/connection ปิดหรือไม่เปิด method นั้น/adapter ยังรับ method นั้นไม่ได้/มี session ที่เปิดอยู่ด้วยช่องทางอื่น -> 409 (ช่องทางเดิมคืน session ใบเดิม)")
    .Produces<CreatePaymentSessionResponse>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status403Forbidden)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status409Conflict);

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
startRedirect.RequireAuthorization("merchant-user").RequirePermission(Keys.PaymentRedirect).RequireUserCsrf()
    .WithTags("การชำระเงิน")
    .WithName("StartPaymentRedirect")
    .WithSummary("เริ่ม redirect ไปยัง PSP")
    .WithDescription("claim แล้ว charge: คืน URL redirect ของ PSP สำหรับ payment session ต้องมี merchant-user policy + สิทธิ์ payment.redirect หากไม่พบ -> 404, สถานะไม่ถูกต้อง/ชนกัน -> 409")
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
        summary.OrderId, summary.Amount, summary.Status, summary.PaymentSessionId,
        summary.Lines.Select(l => new OrderSummaryLineResponse(l.ProductId, l.InsuredFirstName, l.InsuredLastName, l.MaskedInsuredIdNumber)).ToList()));
}).AllowAnonymous()
    .WithTags("คำสั่งซื้อ")
    .WithName("GetOrderSummary")
    .WithSummary("สรุปคำสั่งซื้อผ่านลิงก์")
    .WithDescription("capability link แบบสาธารณะ: opaque token จะ resolve สรุปคำสั่งซื้อแบบไม่ระบุตัวตนได้ หากไม่พบ token -> 404, หมดอายุ -> 410 เลข IdNumber ของผู้เอาประกันแต่ละคนจะถูก mask และไม่ส่ง DateOfBirth กลับมาเลย")
    .Produces<OrderSummaryResponse>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status410Gone);

api.MapPost("/orders/{orderId:guid}/summary/resend", async (
    Guid orderId, IActorContext actor, IMediator mediator, CancellationToken ct) =>
{
    var result = await mediator.Send(new ResendOrderSummaryCommand(orderId, actor.MerchantId), ct);
    return Results.Ok(result);
}).RequireAuthorization("merchant-user").RequireUserCsrf()
    .WithTags("คำสั่งซื้อ")
    .WithName("ResendOrderSummary")
    .WithSummary("ส่งลิงก์สรุปคำสั่งซื้อซ้ำ")
    .WithDescription("หมุน token ของสรุปคำสั่งซื้อและต่ออายุ TTL แล้วคืนลิงก์ใหม่")
    .Produces<ResendOrderSummaryResult>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status401Unauthorized);

// Merchant-authenticated order list — every line's InsuredIdNumber masked (REQ-7.4). No reveal audit here.
api.MapGet("/orders", async (IActorContext actor, IMediator mediator, CancellationToken ct) =>
{
    var result = await mediator.Send(new GetOrdersQuery(actor.MerchantId), ct);
    return Results.Ok(result);
}).RequireAuthorization("merchant-user")
    .WithTags("คำสั่งซื้อ")
    .WithName("ListOrders")
    .WithSummary("รายการคำสั่งซื้อของร้านค้าที่ผูกอยู่")
    .WithDescription("InsuredIdNumber ของทุก line จะถูก mask (เห็นแค่ 4 ตัวท้าย) ใช้ endpoint อ่านรายละเอียดถ้าต้องการค่าเต็ม")
    .Produces<OrdersListView>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status401Unauthorized);

// Merchant-authenticated single-order detail — every line's InsuredIdNumber in FULL, one RevealAudit row
// written per line returned (REQ-7.5), fail-closed (GetOrderDetailHandler saves the audit before building
// the response; if that save throws, the shared exception handler turns it into a 5xx with no PII returned).
api.MapGet("/orders/{orderId:guid}", async (
    Guid orderId, IActorContext actor, IMediator mediator, CancellationToken ct) =>
{
    var result = await mediator.Send(
        new GetOrderDetailCommand(actor.MerchantId, orderId, "merchant-user", actor.UserId!.Value.ToString()), ct);
    return Results.Ok(result);
}).RequireAuthorization("merchant-user")
    .WithTags("คำสั่งซื้อ")
    .WithName("GetOrderDetail")
    .WithSummary("อ่านคำสั่งซื้อแบบเต็มพร้อม audit trail")
    .WithDescription("InsuredIdNumber ของทุก line จะคืนค่าเต็ม เขียน reveal-audit หนึ่งแถวต่อหนึ่ง line ที่คืนค่า ถ้าเขียน audit ไม่สำเร็จ จะ fail closed (5xx, ไม่คืนข้อมูล PII)")
    .Produces<OrderDetailView>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status404NotFound);

// policy-reference-record REQ-3: merchant-plane write for one item's external insurance-reference record.
// Not gated on Order.Status (REQ-3.4 — writable on a Cancelled order too, insurance-pivot's state machine
// untouched). Unknown item, or an item under another merchant, both read as 404 (REQ-3.3 — no existence leak).
api.MapPut("/orders/{orderId:guid}/items/{itemId:guid}/policy", async (
    Guid orderId, Guid itemId, UpsertItemPolicyRequest body, IActorContext actor, IMediator mediator,
    CancellationToken ct) =>
{
    var input = new ItemPolicyInput(
        body.InsuranceCategory, body.ReferenceNumberType, body.ReferenceNumber, body.EndorsementNumber,
        body.RenewalReminderNumber, body.InsuredObjectReference, body.NetPremium, body.GrossPremium,
        body.PremiumRemittanceStatus, body.DeductedAt);
    var result = await mediator.Send(
        new UpsertItemPolicyCommand(actor.MerchantId, itemId, input, actor.UserId!.Value.ToString()), ct);
    return Results.Ok(result);
}).RequireAuthorization("merchant-user").RequirePermission(Keys.PoliciesWrite).RequireUserCsrf()
    .WithTags("คำสั่งซื้อ")
    .WithName("UpsertItemPolicy")
    .WithSummary("บันทึกเลขอ้างอิงกรมธรรม์ภายนอกของ item")
    .WithDescription("สร้างหรืออัปเดตข้อมูล policy-reference ภายนอกของหนึ่ง order item ต้องมี merchant-user policy + สิทธิ์ policies.write หากไม่พบ item หรือ item อยู่ร้านค้าอื่น -> 404")
    .Produces<UpsertItemPolicyResult>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status403Forbidden)
    .ProducesProblem(StatusCodes.Status404NotFound);

// Reconciliation report: the bound merchant's orders grouped by status + currency (count + total).
api.MapGet("/reports/reconciliation", async (IActorContext actor, IMediator mediator, CancellationToken ct) =>
{
    var view = await mediator.Send(new GetReconciliationSummaryQuery(actor.MerchantId), ct);
    return TypedResults.Ok(view);
}).RequireAuthorization("merchant-user")
    .WithTags("คำสั่งซื้อ")
    .WithName("GetReconciliationReport")
    .WithSummary("รายงาน reconciliation")
    .WithDescription("คำสั่งซื้อของร้านค้าที่ผูกอยู่ จัดกลุ่มตามสถานะและสกุลเงิน (จำนวน + ยอดรวม)")
    .Produces<ReconciliationView>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status401Unauthorized);

// policy-reference-record REQ-4.1/4.2/4.4: merchant-plane policy report — auto-scoped to the bound merchant
// via the ambient query filter (no whitelist exposes merchantId, mirrors /products). SFS filter/sort/paging.
api.MapGet("/reports/policies", async (HttpContext http, IActorContext actor, IMediator mediator, CancellationToken ct) =>
{
    var p = SfsQueryParser.Parse(http.Request.Query);
    var result = await mediator.Send(new ListPolicyReportQuery
    {
        MerchantId = actor.MerchantId,
        Page = p.Page, Limit = p.Limit, Filters = p.Filters, Sort = p.Sort, Search = p.Search,
    }, ct);
    return Results.Ok(result);
})
    .RequireAuthorization("merchant-user").RequirePermission(Keys.PoliciesRead)
    .WithMetadata(new SfsQueryParamsMarker())
    .WithTags("คำสั่งซื้อ")
    .WithName("ListPolicyReport")
    .WithSummary("รายงานเลขอ้างอิงกรมธรรม์ (ร้านค้าตัวเอง)")
    .WithDescription("รายงานแบบแบ่งหน้าของ external policy reference, insurance category, การเคลียร์เบี้ยประกัน และสถานะการชำระเงินที่คำนวณได้ ของ item ที่ขายแล้ว ต้องมี merchant-user policy + สิทธิ์ policies.read รองรับ SFS (page, limit, filters, sort)")
    .Produces<PagedResult<PolicyReportItem>>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status403Forbidden);

// --- Admin BFF (/api/v1/admins route group, REQ-1/7/10) ---
// One group binds the CSRF double-submit filter ONCE for the whole admin surface (the credentialed admin CORS
// policy is applied to /api/v1/admins/* by PolCorsPolicyProvider). Per-endpoint authorization stays explicit: login
// is anonymous; every other route gates on the Session "admin" policy. The CSRF filter exempts safe methods,
// so the login/callback GETs pass untouched.
var admin = api.MapGroup("/admins").RequireCsrf();

// Top-level browser navigation (AllowAnonymous, rate-limited): validate the post-login returnTo against the
// allowlist, then hand off to the {provider}'s OIDC handler, which builds the Authorization Code + PKCE + state
// + nonce redirect to the IdP. The callback (AdminAuth:Providers:{Provider}:CallbackPath) is handled by the OIDC
// middleware itself, which establishes the session via OnTicketReceived — there is no mapped callback endpoint.
// An unknown or unconfigured provider slug is simply absent from the registered map -> 404.
admin.MapGet("/auth/{provider}/login", (
    string provider, HttpContext http, AdminOidcProviders providers, IOptions<AdminSessionOptions> session) =>
{
    if (!providers.TryGetValue(provider.ToLowerInvariant(), out var scheme))
        return Results.NotFound();
    var returnTo = ReturnUrlPolicy.Resolve(
        http.Request.Query["returnTo"].ToString(), session.Value.ReturnUrlAllowlist, session.Value.DefaultReturnPath);
    return Results.Challenge(
        new AuthenticationProperties { RedirectUri = returnTo },
        [scheme]);
})
.AllowAnonymous()
.RequireRateLimiting(AuthRateLimiting.PolicyName)
    .WithTags("การเข้าสู่ระบบ")
    .WithName("AdminLogin")
    .WithSummary("เริ่มเข้าสู่ระบบผู้ดูแลระบบ")
    .WithDescription("ตรวจสอบ returnTo กับ allowlist แล้ว redirect ไปยัง provider (google/microsoft; OIDC Authorization Code + PKCE) callback จะเป็นตัวสร้าง session cookie หาก provider ไม่รู้จักหรือยังไม่ได้ตั้งค่า -> 404")
    .Produces(StatusCodes.Status302Found)
    .ProducesProblem(StatusCodes.Status404NotFound)
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
    .WithTags("การเข้าสู่ระบบ")
    .WithName("AdminLogout")
    .WithSummary("ออกจากระบบเครื่องนี้")
    .WithDescription("เพิกถอน session family ปัจจุบัน (เฉพาะเครื่องนี้) แล้วล้างคุกกี้")
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
    .WithTags("การเข้าสู่ระบบ")
    .WithName("AdminLogoutAll")
    .WithSummary("ออกจากระบบทุกเครื่อง")
    .WithDescription("เพิกถอนทุก session ของผู้ดูแลระบบคนนี้ในทุกเครื่อง แล้วล้างคุกกี้")
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
    .RequireCsrf() // re-attached explicitly — no longer inherited from the /admins group (REQ-7.1)
    .RequireAuthorization("admin").RequirePlatformUserTier(Tier.Super) // provisioning is Super-only (REQ-8.4)
    .WithTags("ร้านค้า (ผู้ดูแลระบบ)")
    .WithName("ProvisionMerchant")
    .WithSummary("Provision ร้านค้าใหม่")
    .WithDescription("เฉพาะ Super สร้างร้านค้าพร้อม PSP connection (secret เก็บใน vault, config เก็บตามที่ส่งมา) รหัสซ้ำ -> 409, input ไม่ถูกต้อง -> 400")
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
}).RequireCsrf().RequireAuthorization("admin") // GET is CSRF-exempt by design; attached for REQ-7.1
    .WithTags("ร้านค้า (ผู้ดูแลระบบ)")
    .WithName("GetMerchant")
    .WithSummary("อ่านข้อมูลร้านค้าตามรหัส")
    .WithDescription("admin แบบ Scoped เห็นเฉพาะร้านค้าที่ถูก assign ให้; Super เห็นได้ไม่จำกัด นอก scope หรือไม่พบ -> 404 (ไม่รั่วว่ามีอยู่จริงหรือไม่)")
    .Produces<MerchantView>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status401Unauthorized);

// --- MerchantUser BFF auth (merchant-user-google-sso REQ-8/9/14) ---
// Auth is its own /api/v1/merchants/auth group, mirroring /api/v1/admins/auth (provider-scoped OIDC): login here
// (anonymous), logout/logout-all on the filtered ref below, and the OIDC callbacks
// (MerchantAuth:Providers:{Provider}:CallbackPath) under the same prefix — while /merchants/users keeps the real
// user resources (register + me). NOTE PolCorsPolicyProvider carves BOTH /merchants/users AND /merchants/auth out of
// the admin plane so the merchant-user SPA's credentialed CORS applies here.
var merchantAuthAnon = api.MapGroup("/merchants/auth");

// Top-level browser navigation (AllowAnonymous, rate-limited): validate the post-login returnTo against the merchant-user
// allowlist, then hand off to the {provider}'s "MerchantUser{Provider}" OIDC handler, which builds the Authorization
// Code + PKCE + state + nonce redirect to the IdP. The callback is handled by the OIDC middleware itself, which runs
// the 4-way state branch via OnTicketReceived -> UserLoginService (session cookie for an Active merchant-user, a
// signed registration/correction ticket + redirect to /register otherwise) — there is no mapped callback. An unknown
// or unconfigured provider slug is simply absent from the registered map -> 404.
merchantAuthAnon.MapGet("/{provider}/login", (
    string provider, HttpContext http, UserOidcProviders providers, IOptions<UserSessionOptions> session) =>
{
    if (!providers.TryGetValue(provider.ToLowerInvariant(), out var scheme))
        return Results.NotFound();
    var returnTo = ReturnUrlPolicy.Resolve(
        http.Request.Query["returnTo"].ToString(), session.Value.ReturnUrlAllowlist, session.Value.DefaultReturnPath);
    return Results.Challenge(
        new AuthenticationProperties { RedirectUri = returnTo },
        [scheme]);
})
.AllowAnonymous()
.RequireRateLimiting(UserAuthRateLimiting.PolicyName)
    .WithTags("การเข้าสู่ระบบ (ผู้ใช้ร้านค้า)")
    .WithName("MerchantUserLogin")
    .WithSummary("เริ่มเข้าสู่ระบบผู้ใช้ร้านค้า")
    .WithDescription("ตรวจสอบ returnTo กับ allowlist แล้ว redirect ไปยัง provider (google/microsoft; OIDC Authorization Code + PKCE) callback จะสร้าง session cookie ให้ merchant-user ที่ Active หรือ redirect ผู้สมัครไป /register พร้อม ticket ที่เซ็นแล้ว หาก provider ไม่รู้จักหรือยังไม่ได้ตั้งค่า -> 404")
    .Produces(StatusCodes.Status302Found)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status429TooManyRequests);

// --- MerchantUser self-service registration entry (REQ-3.7) ---
// The anonymous pre-session register endpoint stays under /merchants/users — it creates the user resource, so it is
// resource-shaped, not auth-shaped.
var merchantUsersAnon = api.MapGroup("/merchants/users");

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
        formModel, photoBytes, photoContentType, http.TraceIdentifier, ticket.Provider), ct);

    return Results.Created($"/api/v1/merchants/users/{result.MerchantUserId}",
        new UserRegisterResponse(result.MerchantUserId, result.Status.ToString()));
})
    .AllowAnonymous()
    .DisableAntiforgery()
    .RequireRateLimiting(UserAuthRateLimiting.PolicyName)
    .WithTags("การเข้าสู่ระบบ (ผู้ใช้ร้านค้า)")
    .WithName("MerchantUserRegister")
    .WithSummary("ส่งคำขอลงทะเบียนผู้ใช้ร้านค้า")
    .WithDescription("ส่งข้อมูลแบบ multipart โดยไม่ต้องยืนยันตัวตน แต่ต้องมี ticket กำกับ (form + รูปถ่าย ไม่บังคับ) สร้าง MerchantUser สถานะ PendingApproval แล้ว enqueue registration event หาก ticket ไม่ถูกต้อง/หมดอายุ -> 400; ส่งซ้ำ/replay (unique Subject index) -> 409; ไฟล์ใหญ่เกินไป -> 413")
    .Accepts<IFormFile>("multipart/form-data")
    .Produces<UserRegisterResponse>(StatusCodes.Status201Created)
    .ProducesProblem(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status409Conflict)
    .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
    .ProducesProblem(StatusCodes.Status429TooManyRequests);

// --- MerchantUser BFF authenticated surface (merchant-user-google-sso REQ-12/13/15/16/17) ---
// TWO filtered groups bind the CSRF double-submit filter for the whole authenticated merchant-user surface (the
// credentialed merchant-user CORS policy is applied to /api/v1/merchants/users/* AND /api/v1/merchants/auth/* by
// PolCorsPolicyProvider): the auth group (logout/logout-all) and the users/resource group (me + roles). Every route
// gates on the single-scheme "merchant-user" policy (MerchantUserSession only, T11); the CSRF filter exempts safe
// methods, and the anonymous pre-session routes (login/callback/register) are mapped OUTSIDE these groups, so they
// are untouched. MerchantBoundFilter then fail-closes both groups on a BOUND merchant-user (REQ-17.2/F10).
var merchantAuth = api.MapGroup("/merchants/auth")
    .RequireUserCsrf()
    .AddEndpointFilter<BoundFilter>();
var merchantUsers = api.MapGroup("/merchants/users")
    .RequireUserCsrf()
    .AddEndpointFilter<BoundFilter>();

// Logout = revoke the CURRENT session family (this device only); other devices stay signed in (REQ-12.1). The
// presented cookie identifies the family.
merchantAuth.MapPost("/logout", async (
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
    .WithTags("การเข้าสู่ระบบ (ผู้ใช้ร้านค้า)")
    .WithName("MerchantUserLogout")
    .WithSummary("ออกจากระบบเครื่องนี้")
    .WithDescription("เพิกถอน session family ปัจจุบันของผู้ใช้ร้านค้า (เฉพาะเครื่องนี้) แล้วล้างคุกกี้")
    .Produces(StatusCodes.Status204NoContent)
    .ProducesProblem(StatusCodes.Status401Unauthorized);

// Logout-all = revoke EVERY session of this merchant-user across all devices (REQ-12.2).
merchantAuth.MapPost("/logout-all", async (
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
    .WithTags("การเข้าสู่ระบบ (ผู้ใช้ร้านค้า)")
    .WithName("MerchantUserLogoutAll")
    .WithSummary("ออกจากระบบทุกเครื่อง")
    .WithDescription("เพิกถอนทุก session ของผู้ใช้ร้านค้าคนนี้ในทุกเครื่อง แล้วล้างคุกกี้")
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
    .WithTags("การเข้าสู่ระบบ (ผู้ใช้ร้านค้า)")
    .WithName("GetMerchantUserMe")
    .WithSummary("อ่านข้อมูลผู้ใช้ร้านค้าปัจจุบัน")
    .WithDescription("ให้ SPA อ่านตัวตนของตัวเอง: ร้านค้า, active role code, และสิทธิ์ที่มีผลจริง (effective permissions) หากยังไม่ผูก (merchant-Bearer) -> 403")
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
    .WithTags("บทบาท (ผู้ใช้ร้านค้า)")
    .WithName("ListMerchantUserPermissions")
    .WithSummary("แคตตาล็อกสิทธิ์ของ MerchantUser")
    .WithDescription("แคตตาล็อกสิทธิ์/กลุ่มที่ใช้เป็นฐานของ role matrix ฝั่งผู้ใช้ร้านค้า (resource = group key ของสิทธิ์)")
    .Produces<MerchantUserPermissionCatalogResponse>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status401Unauthorized);

merchantUsers.MapGet("/roles", async (IUserScope scope, IMediator mediator, CancellationToken ct) =>
{
    var context = RoleSideContextResolver.ForMerchantUser(scope);
    var result = await mediator.Send(new ListRolesQuery { Context = context, Limit = int.MaxValue }, ct);
    return Results.Ok(result.Items.Select(MerchantUserRoleToWire));
})
    .RequireAuthorization("merchant-user")
    .WithTags("บทบาท (ผู้ใช้ร้านค้า)")
    .WithName("ListMerchantUserRoles")
    .WithSummary("รายการบทบาทผู้ใช้ร้านค้า")
    .WithDescription("บทบาทผู้ใช้ร้านค้าทั้งหมด (ที่แชร์ + ของร้านค้าตัวเอง) พร้อมสิทธิ์และจำนวนผู้ใช้ที่ผูกอยู่")
    .Produces<IEnumerable<MerchantUserRoleResponse>>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status401Unauthorized);

merchantUsers.MapGet("/roles/{code}", async (string code, IUserScope scope, IMediator mediator, CancellationToken ct) =>
{
    var context = RoleSideContextResolver.ForMerchantUser(scope);
    var role = await mediator.Send(new GetRoleQuery(context, code), ct);
    return role is null ? Results.Problem(statusCode: StatusCodes.Status404NotFound) : Results.Ok(MerchantUserRoleToWire(role));
}).RequireAuthorization("merchant-user")
    .WithTags("บทบาท (ผู้ใช้ร้านค้า)")
    .WithName("GetMerchantUserRole")
    .WithSummary("อ่านบทบาทผู้ใช้ร้านค้าตามรหัส")
    .WithDescription("คืนบทบาทผู้ใช้ร้านค้าหนึ่งรายการพร้อมสิทธิ์ หากไม่พบหรือร้านค้านี้มองไม่เห็น -> 404")
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
    .WithTags("บทบาท (ผู้ใช้ร้านค้า)")
    .WithName("CreateMerchantUserRole")
    .WithSummary("สร้างบทบาทผู้ใช้ร้านค้า")
    .WithDescription("ต้องมีสิทธิ์ roles.manage รหัสซ้ำ (รวมรหัสที่แชร์อยู่) -> 409; permission key ที่ไม่อยู่ในแคตตาล็อกหรือมาจากฝั่ง Platform -> 400")
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
    .WithTags("บทบาท (ผู้ใช้ร้านค้า)")
    .WithName("UpdateMerchantUserRole")
    .WithSummary("แก้ไขบทบาทผู้ใช้ร้านค้า")
    .WithDescription("ต้องมีสิทธิ์ roles.manage รหัส (code จาก route) แก้ไขไม่ได้; บทบาทที่ไม่ใช่ของร้านค้านี้ (รวม shared seed) -> 409; ปิดใช้งาน merchant_manager -> 409")
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
    .WithTags("บทบาท (ผู้ใช้ร้านค้า)")
    .WithName("DeleteMerchantUserRole")
    .WithSummary("ลบบทบาทผู้ใช้ร้านค้า")
    .WithDescription("ต้องมีสิทธิ์ roles.manage บทบาทที่ไม่ใช่ของร้านค้านี้ (รวม shared seed) -> 409; merchant_manager ลบไม่ได้ -> 409; บทบาทที่ยังมีผู้ใช้ผูกอยู่ -> 409")
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
    .WithTags("บทบาท (ผู้ใช้ร้านค้า)")
    .WithName("SetMerchantUserUserRoles")
    .WithSummary("กำหนดบทบาทของผู้ใช้ร้านค้า")
    .WithDescription("ต้องมีสิทธิ์ merchant-user.user.roles แทนที่บทบาทของผู้ใช้ร้านค้าด้วยชุดที่ระบุมาทั้งหมด จำกัดเฉพาะร้านค้าของคุณ หากไม่รู้จัก role code -> 400; เป้าหมายไม่อยู่ร้านค้าคุณ -> 404")
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
    .WithTags("ผู้ใช้ร้านค้า (ผู้ดูแลระบบ)")
    .WithName("ApproveMerchantUser")
    .WithSummary("อนุมัติผู้ใช้ร้านค้าเข้าร้านค้าหนึ่ง")
    .WithDescription("ต้องมีสิทธิ์ merchant-user.approve ผูกผู้ใช้ร้านค้าเข้ากับร้านค้าที่อยู่ใน accessible set ของ admin + กำหนดบทบาท + เปิดใช้งาน ในทรานแซกชันเดียว หาก Active อยู่แล้ว -> idempotent 200; ไม่พบเป้าหมาย -> 404; ร้านค้าไม่ active/นอก scope -> 409/404; role ไม่รู้จัก/ไม่ active หรือเป้าหมายไม่ใช่ Pending -> 409")
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
    .WithTags("ผู้ใช้ร้านค้า (ผู้ดูแลระบบ)")
    .WithName("RejectMerchantUser")
    .WithSummary("ปฏิเสธผู้ใช้ร้านค้าที่รอดำเนินการ")
    .WithDescription("ต้องมีสิทธิ์ merchant-user.reject ตั้งสถานะผู้ใช้ร้านค้าเป็น Rejected และเพิกถอน session ที่ยัง live อยู่ ไม่พบเป้าหมาย -> 404; เป้าหมายไม่ใช่ Pending -> 409")
    .Produces<RejectMerchantUserResponse>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status409Conflict)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status403Forbidden);

// registration-attempt-history REQ-2/3/4: per-attempt form snapshots + lifecycle timeline for ONE merchant
// user. PII masked by default; ?reveal=true returns full values and the handler persists a `revealed` audit
// BEFORE building the response (fail-closed). `reveal` must keep its default — without one, a request that
// omits ?reveal= would 400 and kill the primary masked path (B4). Returns the Application record directly:
// enums serialize as strings via the global JsonStringEnumConverter, nothing needs reshaping (m4).
admin.MapGet("/merchants/users/{subject}/registrations", async (
    string subject, HttpContext http, IAdminScope scope, IMediator mediator, CancellationToken ct,
    bool reveal = false) =>
{
    // Accessible-merchant floor (REQ-2.7): threaded as primitives like the merchants.policies endpoints —
    // a merchant-bound target outside the admin's scope reads as 404 inside the handler (no existence leak).
    var result = await mediator.Send(new GetRegistrationHistoryQuery(
        subject, reveal, http.User.FindFirst("sub")?.Value ?? "unknown", http.TraceIdentifier,
        scope.Accessible.IsUnrestricted, scope.Accessible.Merchants), ct);
    return result is null ? Results.NotFound() : Results.Ok(result);
}).RequireAuthorization("admin").RequirePermission(Keys.MerchantUserView)
    .WithTags("ผู้ใช้ร้านค้า (ผู้ดูแลระบบ)")
    .WithName("GetMerchantUserRegistrationHistory")
    .WithSummary("ดูประวัติการลงทะเบียนของผู้ใช้ร้านค้ารายคน")
    .WithDescription("ต้องมีสิทธิ์ merchants.users.view คืน snapshot ฟอร์มทุกครั้ง (เรียงตาม AttemptNo) + timeline จาก RegistrationAudits โดย mask PII เป็นค่าเริ่มต้น ส่ง ?reveal=true เพื่อดูค่าเต็ม (ระบบบันทึก audit ว่าเปิดดูทุกครั้ง) ไม่พบเป้าหมาย หรือเป้าหมายผูกกับ merchant นอก scope ของ admin -> 404")
    .Produces<RegistrationHistoryResult>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status403Forbidden);

// policy-reference-record REQ-3.2-admin/3.3-admin: cross-merchant escape-hatch write for one item's external
// insurance-reference record — a Super admin may write ANY merchant's item; a Scoped admin is confined to its
// accessible set. Both "item missing" and "item outside the admin's scope" read as 404 (no existence leak,
// same discipline as IAdminQuery.GetMerchantByCodeAsync above).
admin.MapPut("/orders/{orderId:guid}/items/{itemId:guid}/policy", async (
    Guid orderId, Guid itemId, UpsertItemPolicyRequest body, IAdminScope scope, IMediator mediator,
    CancellationToken ct) =>
{
    var input = new ItemPolicyInput(
        body.InsuranceCategory, body.ReferenceNumberType, body.ReferenceNumber, body.EndorsementNumber,
        body.RenewalReminderNumber, body.InsuredObjectReference, body.NetPremium, body.GrossPremium,
        body.PremiumRemittanceStatus, body.DeductedAt);
    var result = await mediator.Send(new UpsertItemPolicyAdminCommand(
        itemId, input, scope.Current.AdminId.ToString(), scope.Accessible.IsUnrestricted, scope.Accessible.Merchants), ct);
    return Results.Ok(result);
}).RequireAuthorization("admin").RequirePermission(Keys.MerchantsPoliciesWrite)
    .WithTags("คำสั่งซื้อ (ผู้ดูแลระบบ)")
    .WithName("UpsertItemPolicyAdmin")
    .WithSummary("บันทึกเลขอ้างอิงกรมธรรม์ภายนอกของ item (ผู้ดูแลระบบ ข้ามร้านค้า)")
    .WithDescription("สร้างหรืออัปเดตข้อมูล policy-reference ภายนอกของหนึ่ง order item ของร้านค้าใดก็ได้ ต้องมี admin policy + สิทธิ์ merchants.policies.write admin แบบ Super เขียนร้านค้าไหนก็ได้; admin แบบ Scoped จำกัดเฉพาะ accessible set หากไม่พบ item หรือ item อยู่นอก scope ของ admin -> 404 (ไม่รั่วว่ามีอยู่จริงหรือไม่)")
    .Produces<UpsertItemPolicyAdminResult>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status403Forbidden)
    .ProducesProblem(StatusCodes.Status404NotFound);

// policy-reference-record REQ-4.2/4.4: admin cross-merchant policy report — IAdminItemPolicyReader escape-hatch
// confined by IAdminScope.Accessible; optional ?merchantId= narrows further (not part of the SFS whitelist,
// mirrors ProductSfs's own merchantId exclusion). A Scoped admin naming a merchant outside its accessible set
// gets an empty page, never a leak.
admin.MapGet("/reports/policies", async (HttpContext http, IAdminScope scope, IMediator mediator, CancellationToken ct) =>
{
    var p = SfsQueryParser.Parse(http.Request.Query);
    Guid? merchantId = Guid.TryParse(http.Request.Query["merchantId"], out var mid) ? mid : null;
    var result = await mediator.Send(new ListPolicyReportAdminQuery
    {
        IsUnrestrictedAdmin = scope.Accessible.IsUnrestricted, AccessibleMerchantIds = scope.Accessible.Merchants,
        MerchantId = merchantId,
        Page = p.Page, Limit = p.Limit, Filters = p.Filters, Sort = p.Sort, Search = p.Search,
    }, ct);
    return Results.Ok(result);
})
    .RequireAuthorization("admin").RequirePermission(Keys.MerchantsPoliciesRead)
    .WithMetadata(new SfsQueryParamsMarker())
    .WithTags("คำสั่งซื้อ (ผู้ดูแลระบบ)")
    .WithName("ListPolicyReportAdmin")
    .WithSummary("รายงานเลขอ้างอิงกรมธรรม์ (ผู้ดูแลระบบ ข้ามร้านค้า)")
    .WithDescription("รายงานแบบแบ่งหน้าของ external policy reference ของ item ที่ขายแล้วข้ามร้านค้า ต้องมี admin policy + สิทธิ์ merchants.policies.read admin แบบ Super เห็นทุกร้านค้า; admin แบบ Scoped จำกัดเฉพาะ accessible set ใส่ ?merchantId= เพื่อกรองเพิ่มภายใน scope ได้ รองรับ SFS (page, limit, filters, sort)")
    .Produces<PagedResult<PolicyReportItem>>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status400BadRequest)
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
    .WithTags("การเข้าสู่ระบบ")
    .WithName("GetAdminMe")
    .WithSummary("อ่านข้อมูลผู้ดูแลระบบปัจจุบัน")
    .WithDescription("ให้ SPA อ่านตัวตนของตัวเอง: tier, ร้านค้าที่เข้าถึงได้ (หรือไม่จำกัด) และสิทธิ์ที่มีผลจริง (effective permissions) หากบัญชีถูกปิดใช้งาน -> 403")
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
}).RequireCsrf().RequireAuthorization("admin").RequirePlatformUserTier(Tier.Super)
    .WithTags("ผู้ดูแลระบบ")
    .WithName("CreateScopedAdmin")
    .WithSummary("เชิญ Scoped admin")
    .WithDescription("เฉพาะ Super เชิญ Scoped admin ด้วยอีเมลที่ยืนยันแล้ว subject จะผูกตอน login ครั้งแรก ไม่มีอีเมล -> 400")
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
    .WithTags("ผู้ดูแลระบบ")
    .WithName("ListAdmins")
    .WithSummary("รายการบัญชีผู้ดูแลระบบ")
    .WithDescription("ต้องมีสิทธิ์ user.view ทำเนียบผู้ดูแลระบบแบบแบ่งหน้า รองรับ SFS: page, limit, filters (email/tier/status), sort (email/createdAt), search (email) ค่า filter tier/status เป็น wire form ตัวพิมพ์เล็ก ค่านอกโดเมน -> 400")
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
    .WithTags("ผู้ดูแลระบบ")
    .WithName("GetAdmin")
    .WithSummary("อ่านบัญชีผู้ดูแลระบบ")
    .WithDescription("ต้องมีสิทธิ์ user.view คืน tier, status, ร้านค้าที่เข้าถึงได้ (ไม่จำกัดสำหรับ Super) และ role code ที่กำหนดให้ทั้งหมด หากไม่พบ id -> 404")
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
    .WithTags("ผู้ดูแลระบบ")
    .WithName("GetAdminEffectivePermissions")
    .WithSummary("อ่านสิทธิ์ที่มีผลจริงของผู้ดูแลระบบ")
    .WithDescription("ต้องมีสิทธิ์ user.view union แบบไม่ซ้ำ เรียงตาม ordinal ของ permission key จากบทบาทที่ ACTIVE ของผู้ดูแลระบบ (กฎเดียวกับ /me) ใช้ได้แม้บัญชีถูก suspend หากไม่พบ id -> 404")
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
    .WithTags("ผู้ดูแลระบบ")
    .WithName("AssignMerchantToAdmin")
    .WithSummary("มอบสิทธิ์ร้านค้าให้ผู้ดูแลระบบ")
    .WithDescription("เฉพาะ Super ให้สิทธิ์ Scoped admin เข้าถึงร้านค้าหนึ่ง ร้านค้าไม่ active/ไม่รู้จัก หรือซ้ำ -> 409")
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
    .WithTags("ผู้ดูแลระบบ")
    .WithName("UnassignMerchantFromAdmin")
    .WithSummary("ถอนสิทธิ์ร้านค้าจากผู้ดูแลระบบ")
    .WithDescription("เฉพาะ Super ลบแถว merchant assignment แบบถาวร ไม่พบ assignment -> 404")
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
    .WithTags("ผู้ดูแลระบบ")
    .WithName("SuspendAdmin")
    .WithSummary("ระงับใช้งานผู้ดูแลระบบ")
    .WithDescription("เฉพาะ Super ระงับใช้งานผู้ดูแลระบบคนอื่น ระงับบัญชีตัวเองไม่ได้ (403) เพื่อไม่ให้ oversight ถูกล็อกออก")
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
    .WithTags("ผู้ดูแลระบบ")
    .WithName("ReactivateAdmin")
    .WithSummary("เปิดใช้งานผู้ดูแลระบบที่ถูกระงับ")
    .WithDescription("เฉพาะ Super คืนสถานะผู้ดูแลระบบที่ถูกระงับกลับเป็น Active และเพิกถอน session เดิม (ต้อง login ใหม่) idempotent ถ้า Active อยู่แล้ว หากไม่พบ id -> 404")
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
    .WithTags("ผู้ดูแลระบบ")
    .WithName("ChangeAdminTier")
    .WithSummary("เลื่อนหรือลด tier ของผู้ดูแลระบบ")
    .WithDescription("เฉพาะ Super เปลี่ยน tier ผู้ดูแลระบบระหว่าง scoped กับ super เปลี่ยน tier ตัวเองไม่ได้ (403) เพื่อไม่ให้ oversight ค้าง idempotent ถ้า tier ตรงกับที่ขออยู่แล้ว หากไม่พบ id -> 404")
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
    .WithTags("ผู้ดูแลระบบ")
    .WithName("UpdateAdminProfile")
    .WithSummary("แก้ไขข้อมูลองค์กรของผู้ดูแลระบบ")
    .WithDescription("ต้องมีสิทธิ์ user.manage ตั้งค่า Position/Office/Level/Division ด้วย master id (null คือล้างค่า) หากไม่พบผู้ดูแลระบบ -> 404; master ไม่รู้จักหรือไม่ active -> 400")
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
MapMasterCrud<IPositionStore, PositionItem>(api, "positions", "ตำแหน่ง",
    (s, p, l, q, ct) => s.ListAsync(p, l, q, ct),
    (s, id, ct) => s.GetByIdAsync(id, ct),
    (s, c, n, ct) => s.CreateAsync(c, n, ct),
    (s, id, n, a, ct) => s.UpdateAsync(id, n, a, ct),
    (s, id, ct) => s.DeactivateAsync(id, ct),
    m => new MasterResponse(m.Id, m.Code, m.Name, m.IsActive));
MapMasterCrud<IOfficeStore, OfficeItem>(api, "offices", "สำนักงาน",
    (s, p, l, q, ct) => s.ListAsync(p, l, q, ct),
    (s, id, ct) => s.GetByIdAsync(id, ct),
    (s, c, n, ct) => s.CreateAsync(c, n, ct),
    (s, id, n, a, ct) => s.UpdateAsync(id, n, a, ct),
    (s, id, ct) => s.DeactivateAsync(id, ct),
    m => new MasterResponse(m.Id, m.Code, m.Name, m.IsActive));
MapMasterCrud<ILevelStore, LevelItem>(api, "levels", "ระดับ",
    (s, p, l, q, ct) => s.ListAsync(p, l, q, ct),
    (s, id, ct) => s.GetByIdAsync(id, ct),
    (s, c, n, ct) => s.CreateAsync(c, n, ct),
    (s, id, n, a, ct) => s.UpdateAsync(id, n, a, ct),
    (s, id, ct) => s.DeactivateAsync(id, ct),
    m => new MasterResponse(m.Id, m.Code, m.Name, m.IsActive));
MapMasterCrud<IDivisionStore, DivisionItem>(api, "divisions", "แผนก",
    (s, p, l, q, ct) => s.ListAsync(p, l, q, ct),
    (s, id, ct) => s.GetByIdAsync(id, ct),
    (s, c, n, ct) => s.CreateAsync(c, n, ct),
    (s, id, n, a, ct) => s.UpdateAsync(id, n, a, ct),
    (s, id, ct) => s.DeactivateAsync(id, ct),
    m => new MasterResponse(m.Id, m.Code, m.Name, m.IsActive));

static void MapMasterCrud<TStore, TItem>(RouteGroupBuilder parent, string segment, string thaiLabel,
    Func<TStore, int, int, string?, CancellationToken, Task<PagedResult<TItem>>> list,
    Func<TStore, Guid, CancellationToken, Task<TItem>> getById,
    Func<TStore, string, string, CancellationToken, Task<TItem>> create,
    Func<TStore, Guid, string, bool, CancellationToken, Task<TItem>> update,
    Func<TStore, Guid, CancellationToken, Task<TItem>> deactivate,
    Func<TItem, MasterResponse> toWire) where TStore : class
{
    // Each of the 4 standalone modules (masterdata-split) gets its own Scalar group — its own Thai noun, no
    // "ผู้ดูแลระบบ" suffix (these are reference lists, not admin-account operations) — instead of one shared
    // "Admin Master Data" bucket, so the split is visible in the API surface too.
    var tag = thaiLabel;

    // Map the root endpoints DIRECTLY with an explicit "/{segment}" path (not a nested MapGroup + empty-string
    // root, which renders the forbidden trailing-slash canonical path — REQ-1.4; see the /api/v1 note above).
    parent.MapGet($"/{segment}", async (HttpContext http, TStore store, CancellationToken ct) =>
    {
        var p = SfsQueryParser.Parse(http.Request.Query);
        var result = await list(store, p.Page, p.Limit, p.Search?.Query, ct);
        return Results.Ok(new PagedResult<MasterResponse>(
            [.. result.Items.Select(toWire)],
            result.Page, result.Limit, result.Total));
    }).RequireCsrf().RequireAuthorization("admin").RequirePermission(Keys.UserManage)
        .WithTags(tag)
        .WithName($"List{segment}")
        .WithSummary($"รายการ{thaiLabel}ทั้งหมด")
        .Produces<PagedResult<MasterResponse>>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden);

    parent.MapGet($"/{segment}/{{id:guid}}", async (Guid id, TStore store, CancellationToken ct) =>
    {
        var item = await getById(store, id, ct);
        return Results.Ok(toWire(item));
    }).RequireCsrf().RequireAuthorization("admin").RequirePermission(Keys.UserManage)
        .WithTags(tag)
        .WithName($"Get{segment}")
        .WithSummary($"อ่านข้อมูล{thaiLabel}ตาม id")
        .WithDescription("ต้องมีสิทธิ์ user.manage หากไม่พบ id -> 404")
        .Produces<MasterResponse>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden);

    parent.MapPost($"/{segment}", async (MasterWriteRequest body, TStore store, CancellationToken ct) =>
    {
        var item = await create(store, body.Code ?? "", body.Name ?? "", ct);
        var wire = toWire(item);
        return Results.Created($"/api/v1/{segment}/{wire.Id}", wire);
    }).RequireCsrf().RequireAuthorization("admin").RequirePermission(Keys.UserManage)
        .WithTags(tag)
        .WithName($"Create{segment}")
        .WithSummary($"สร้าง{thaiLabel}ใหม่")
        .WithDescription("ต้องมีสิทธิ์ user.manage รหัสซ้ำ -> 409; รหัสต้องตรงกับ ^[a-z0-9_]+$ -> 400")
        .Produces<MasterResponse>(StatusCodes.Status201Created)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden);

    parent.MapPut($"/{segment}/{{id:guid}}", async (Guid id, MasterUpdateRequest body, TStore store, CancellationToken ct) =>
    {
        var item = await update(store, id, body.Name ?? "", body.IsActive, ct);
        return Results.Ok(toWire(item));
    }).RequireCsrf().RequireAuthorization("admin").RequirePermission(Keys.UserManage)
        .WithTags(tag)
        .WithName($"Update{segment}")
        .WithSummary($"เปลี่ยนชื่อหรือเปิด/ปิดการใช้งาน{thaiLabel}")
        .WithDescription("ต้องมีสิทธิ์ user.manage รหัส (code) แก้ไขไม่ได้ หากไม่พบ id -> 404")
        .Produces<MasterResponse>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden);

    parent.MapDelete($"/{segment}/{{id:guid}}", async (Guid id, TStore store, CancellationToken ct) =>
    {
        await deactivate(store, id, ct);
        return Results.NoContent();
    }).RequireCsrf().RequireAuthorization("admin").RequirePermission(Keys.UserManage)
        .WithTags(tag)
        .WithName($"Deactivate{segment}")
        .WithSummary($"ปิดการใช้งาน{thaiLabel}")
        .WithDescription("ต้องมีสิทธิ์ user.manage เป็นการปิดการใช้งานแบบ soft เท่านั้น (ตั้ง isActive=false) ข้อมูลที่ถูกอ้างอิงอยู่ (FK Restrict) ยังใช้ได้ ไม่ใช่การลบถาวร หากไม่พบ id -> 404")
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
    .WithTags("ผู้ดูแลระบบ")
    .WithName("ListPlatformUserSessions")
    .WithSummary("รายการ session ของผู้ดูแลระบบ")
    .WithDescription("เฉพาะ Super session ของผู้ดูแลระบบ เรียงใหม่สุดก่อน พร้อม flag isLive ที่คำนวณตอนอ่าน ไม่คืนค่า token จริง หากไม่พบผู้ดูแลระบบ -> 404")
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
    .WithTags("ผู้ดูแลระบบ")
    .WithName("RevokePlatformUserSession")
    .WithSummary("เพิกถอน session ของผู้ดูแลระบบ")
    .WithDescription("เฉพาะ Super เพิกถอนทั้ง rotation family ของ session ไม่พบ session หรือ session เป็นของผู้ดูแลระบบคนอื่น -> 404 idempotent (เพิกถอนไปแล้ว -> 204)")
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
    .WithTags("บทบาท (ผู้ดูแลระบบ)")
    .WithName("ListPermissions")
    .WithSummary("แคตตาล็อกสิทธิ์")
    .WithDescription("แคตตาล็อกสิทธิ์/กลุ่มที่ใช้เป็นฐานของ role matrix (resource = group key ของสิทธิ์)")
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
    .WithTags("บทบาท (ผู้ดูแลระบบ)")
    .WithName("ListRoles")
    .WithSummary("รายการบทบาท")
    .WithDescription("บทบาทผู้ดูแลระบบแบบแบ่งหน้า พร้อมสิทธิ์และจำนวนผู้ใช้ที่ผูกอยู่ รองรับ SFS: page, limit, filters, sort, search")
    .Produces<PagedResult<RoleResponse>>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status401Unauthorized);

admin.MapGet("/roles/{code}", async (string code, IAdminScope scope, IMediator mediator, CancellationToken ct) =>
{
    var role = await mediator.Send(new GetRoleQuery(RoleSideContextResolver.ForAdmin(scope), code), ct);
    return role is null ? Results.Problem(statusCode: StatusCodes.Status404NotFound) : Results.Ok(RoleToWire(role));
}).RequireAuthorization("admin")
    .WithTags("บทบาท (ผู้ดูแลระบบ)")
    .WithName("GetRole")
    .WithSummary("อ่านบทบาทตามรหัส")
    .WithDescription("คืนบทบาทหนึ่งรายการพร้อมสิทธิ์ หากไม่พบรหัส -> 404")
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
    .WithTags("บทบาท (ผู้ดูแลระบบ)")
    .WithName("CreateRole")
    .WithSummary("สร้างบทบาท")
    .WithDescription("ต้องมีสิทธิ์ user.roles รหัสซ้ำ -> 409; permission key ที่ไม่อยู่ในแคตตาล็อก -> 400")
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
    .WithTags("บทบาท (ผู้ดูแลระบบ)")
    .WithName("UpdateRole")
    .WithSummary("แก้ไขบทบาท")
    .WithDescription("ต้องมีสิทธิ์ user.roles รหัส (code จาก route) แก้ไขไม่ได้; ปิดใช้งาน platform_admin -> 409")
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
    .WithTags("บทบาท (ผู้ดูแลระบบ)")
    .WithName("DeleteRole")
    .WithSummary("ลบบทบาท")
    .WithDescription("ต้องมีสิทธิ์ user.roles บทบาทที่ยังมีผู้ใช้ผูกอยู่ลบไม่ได้ -> 409")
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
    .WithTags("ผู้ดูแลระบบ")
    .WithName("SetAdminRoles")
    .WithSummary("กำหนดบทบาทของผู้ดูแลระบบ")
    .WithDescription("ต้องมีสิทธิ์ user.roles แทนที่บทบาทของผู้ดูแลระบบด้วยชุดที่ระบุมาทั้งหมด หากไม่รู้จัก role code -> 400; ไม่พบผู้ดูแลระบบ -> 404")
    .Produces(StatusCodes.Status204NoContent)
    .ProducesProblem(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status403Forbidden);

// rf2-iam-rbac REQ-5: fail fast at boot if any RequirePermission gate references a key absent from the catalog,
// or a key whose side does not match the endpoint's own auth policy (side-aware, REQ-5.4) — one guard now covers
// both consoles, incl. the cross-catalog merchant-user.approve/reject keys gated under the "admin" policy.
PermissionParity.Assert(app);

// CSRF parity: every unsafe endpoint under a cookie-session policy must carry its own side's CSRF filter —
// a forgotten .RequireCsrf()/.RequireUserCsrf() is a boot failure here, not a silent runtime gap.
CsrfParity.Assert(app);

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

// No Amount: the charge is priced from the order row server-side (a body that still sends "amount" is
// simply ignored — the platform never mints a charge the order does not back).
internal sealed record CreatePaymentSessionRequest(
    Guid OrderId, string Method, Code Psp);
internal sealed record AddItemToCartRequest(Guid ProductId, int Quantity);
internal sealed record SetCartItemQuantityRequest(int Quantity);
internal sealed record CreateCartResponse(Guid CartId);
internal sealed record StartCheckoutInsuredPerson(
    Guid ProductId, string FirstName, string LastName, string IdNumber, DateTime DateOfBirth);
internal sealed record StartCheckoutRequest(
    Guid CartId, string? Recipient, IReadOnlyList<StartCheckoutInsuredPerson> InsuredPersons);
internal sealed record OrderSummaryLineResponse(
    Guid ProductId, string InsuredFirstName, string InsuredLastName, string InsuredIdNumber);
internal sealed record OrderSummaryResponse(
    Guid OrderId, Money Amount, string Status, Guid? PaymentSessionId, IReadOnlyList<OrderSummaryLineResponse> Lines);

// policy-reference-record REQ-1/REQ-3: field-for-field wire twin of ItemPolicyInput — a dedicated request
// type rather than binding the domain input directly (this codebase's convention, e.g. AddItemToCartRequest).
internal sealed record UpsertItemPolicyRequest(
    InsuranceCategory? InsuranceCategory, ReferenceNumberType? ReferenceNumberType, string? ReferenceNumber,
    string? EndorsementNumber, string? RenewalReminderNumber, string? InsuredObjectReference,
    Money? NetPremium, Money? GrossPremium, PremiumRemittanceStatus PremiumRemittanceStatus, DateOnly? DeductedAt);

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

    /// <summary>Fails fast when <c>Psp:PublicBaseUrl</c> is missing or is not an absolute URI. Every
    /// per-connection backend-notification URL handed to a PSP is derived from it
    /// (<c>{PublicBaseUrl}/api/v1/webhooks/{pspConnectionId}</c>), so a blank value produces a callback URL
    /// the PSP cannot reach: the customer pays, the confirmation never arrives, and the order stays
    /// AwaitingPayment. Development is exempt — the committed placeholder keeps the local host and the test
    /// suite booting (captive-payment-alignment REQ-4.3/4.6).</summary>
    public static void RequirePublicBaseUrl(IConfiguration configuration)
    {
        var publicBaseUrl = configuration[$"{PspOptions.SectionName}:PublicBaseUrl"];
        // The scheme check is not pedantry: on Unix, Uri.TryCreate accepts a bare path like "/api/v1" as an
        // absolute file:// URI, so "absolute" alone would admit a value no PSP can ever POST to.
        if (string.IsNullOrWhiteSpace(publicBaseUrl)
            || !Uri.TryCreate(publicBaseUrl, UriKind.Absolute, out var origin)
            || (origin.Scheme != Uri.UriSchemeHttps && origin.Scheme != Uri.UriSchemeHttp))
            throw new InvalidOperationException(
                "Psp:PublicBaseUrl must be an absolute http(s) URI naming this API's public origin (e.g. " +
                "https://api.example.com) — the per-connection PSP webhook URL is derived from it. " +
                "Set Psp__PublicBaseUrl.");
    }

    /// <summary>Fails fast on a misconfigured BFF OIDC side (<paramref name="sectionName"/> = "AdminAuth" /
    /// "MerchantAuth"). For every provider with a non-blank ClientId: the id must not be a committed
    /// placeholder, the secret must be injected (blank or placeholder = never injected), the Authority must be a
    /// real https URL (the committed Microsoft Authority ships a REPLACE_WITH_TENANT_ID placeholder — booting with
    /// it means every login dies at the metadata fetch), the CallbackPath must be set and unique within the side,
    /// and an ADMIN Microsoft provider must pin a tenant (a tenant-specific Authority or a non-empty
    /// AllowedTenants — never open to every Entra tenant). A blank ClientId disables that provider (its scheme is
    /// skipped, REQ-14.2); <paramref name="requireAtLeastOne"/> additionally demands one configured provider (the
    /// admin console with no login is a dead deploy). The error never echoes a secret value (REQ-8.3/14.3).</summary>
    public static void RequireOidcProviders(IConfiguration configuration, string sectionName, bool requireAtLeastOne)
    {
        var anyConfigured = false;
        var callbackPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in configuration.GetSection($"{sectionName}:Providers").GetChildren())
        {
            var clientId = provider["ClientId"];
            if (string.IsNullOrWhiteSpace(clientId))
                continue;
            if (clientId.StartsWith("REPLACE_WITH_", StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"{sectionName}:Providers:{provider.Key}:ClientId is a placeholder. Map a real client id via " +
                    $"{sectionName}__Providers__{provider.Key}__ClientId, or leave it blank to disable this provider.");
            var clientSecret = provider["ClientSecret"];
            if (string.IsNullOrWhiteSpace(clientSecret) || clientSecret.StartsWith("REPLACE_WITH_", StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"{sectionName}:Providers:{provider.Key}:ClientSecret is required when its ClientId is configured — " +
                    $"the runtime secret was not injected. Set {sectionName}__Providers__{provider.Key}__ClientSecret " +
                    "(environment / user-secrets / Vault).");

            var authority = provider["Authority"] ?? "";
            if (!authority.StartsWith("https://", StringComparison.Ordinal)
                || authority.Contains("REPLACE_WITH_", StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"{sectionName}:Providers:{provider.Key}:Authority must be a real https URL — the committed value " +
                    $"is blank or a placeholder. Set {sectionName}__Providers__{provider.Key}__Authority.");

            var callbackPath = provider["CallbackPath"];
            if (string.IsNullOrWhiteSpace(callbackPath) || !callbackPaths.Add(callbackPath))
                throw new InvalidOperationException(
                    $"{sectionName}:Providers:{provider.Key}:CallbackPath must be set and unique per provider — " +
                    "two providers sharing a callback would race the same middleware path.");

            // The admin console must never accept EVERY Entra tenant: a multi-tenant Authority
            // (common/organizations/consumers) needs an explicit AllowedTenants allowlist.
            if (sectionName == "AdminAuth"
                && string.Equals(provider.Key, "Microsoft", StringComparison.OrdinalIgnoreCase)
                && (authority.Contains("/common", StringComparison.OrdinalIgnoreCase)
                    || authority.Contains("/organizations", StringComparison.OrdinalIgnoreCase)
                    || authority.Contains("/consumers", StringComparison.OrdinalIgnoreCase))
                && !provider.GetSection("AllowedTenants").GetChildren().Any(t => !string.IsNullOrWhiteSpace(t.Value)))
                throw new InvalidOperationException(
                    "AdminAuth:Providers:Microsoft with a multi-tenant Authority requires a non-empty AllowedTenants " +
                    "allowlist — the admin console must not accept every Entra tenant. Pin the Authority to your " +
                    "tenant id, or set AdminAuth__Providers__Microsoft__AllowedTenants__0.");

            anyConfigured = true;
        }

        if (requireAtLeastOne && !anyConfigured)
            throw new InvalidOperationException(
                $"{sectionName}:Providers requires at least one provider with a configured ClientId — the login " +
                $"cannot build an authorization request without one. Set {sectionName}__Providers__Google__ClientId (or Microsoft).");
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
