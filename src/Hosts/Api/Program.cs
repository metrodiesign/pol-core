using System.Text;
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
using Mediator;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpLogging;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Orders.Application;
using Orders.Domain.Items;
using Orders.Infrastructure;
using Payments.Application.ConfirmPaymentStatus;
using Payments.Application.CreateSession;
using Payments.Application.HandlePspWebhook;
using Payments.Application.MethodPayable;
using Api.PaymentCapabilities;
using Payments.Application.Ports;
using Payments.Application.ReleaseOpenSession;
using Payments.Application.StartRedirect;
using Payments.Domain.Psp;
using Payments.Infrastructure;
using Payments.Infrastructure.Psp;
using Merchants.Application;
using Merchants.Application.AdminControlPlane;
using Merchants.Domain;
using Merchants.Infrastructure;
using Products.Application;
using Products.Domain;
using Products.Infrastructure;
using Products.Infrastructure.Sp;
using Payments.Application;
// Scalar.AspNetCore also has a DocumentType — the wire enum below is the domain's.
using DocumentType = Products.Domain.DocumentType;
// Payments.Domain cannot be imported wholesale here (its SessionStatus collides with the admin one).
using PaymentMethods = Payments.Domain.PaymentMethods;
// The order summary carries its status as the wire STRING; this is what those strings are named after, so
// the customer endpoints branch on nameof(...) instead of on literals.
using OrderStatus = Orders.Domain.OrderStatus;
using OrderInitiatingAudience = Orders.Domain.OrderInitiatingAudience;
// Payments.Application.GetSession cannot be imported wholesale either — its SessionView collides with the
// admin one, the same way Payments.Domain's SessionStatus does.
using GetPaymentSessionQuery = Payments.Application.GetSession.GetSessionQuery;
using PaymentSessionView = Payments.Application.GetSession.SessionView;
using ListPaymentSessionsQuery = Payments.Application.GetSession.ListSessionsQuery;
using PaymentSessionListItem = Payments.Application.GetSession.PaymentSessionListItem;
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
using ResolveInvitationByIdQuery = Merchants.Application.Users.ResolveInvitationByIdQuery;
using ListMerchantUsersQuery = Merchants.Application.Users.ListMerchantUsersQuery;
using MerchantUserListItem = Merchants.Application.Users.MerchantUserListItem;
using GetMerchantUserQuery = Merchants.Application.Users.GetMerchantUserQuery;
using MerchantUserDetail = Merchants.Application.Users.MerchantUserDetail;
using GetMerchantUserEditQuery = Merchants.Application.Users.GetMerchantUserEditQuery;
using MerchantUserEditView = Merchants.Application.Users.MerchantUserEditView;
using CreateMerchantUserInvitationCommand = Merchants.Application.Users.CreateInvitationCommand;
using CreateMerchantUserInvitationResult = Merchants.Application.Users.CreateInvitationResult;
using RevokeMerchantUserInvitationCommand = Merchants.Application.Users.RevokeInvitationCommand;
using UpdateMerchantUserCommand = Merchants.Application.Users.UpdateMerchantUserCommand;
using ChangeMerchantUserLifecycleCommand = Merchants.Application.Users.ChangeMerchantUserLifecycleCommand;
using MerchantUserLifecycleAction = Merchants.Application.Users.MerchantUserLifecycleAction;
using SubmitRegistrationResult = Merchants.Application.Users.SubmitRegistrationResult;
using IInvitationDeliveryProtector = Merchants.Application.Users.IInvitationDeliveryProtector;
using IInvitationEmailSender = Merchants.Application.Users.IInvitationEmailSender;
using IMerchantSessionStore = Merchants.Application.Users.ISessionStore;
using IMerchantAuthAuditWriter = Merchants.Application.Users.IAuthAuditWriter;
using IMerchantRoleRepository = Merchants.Application.Users.Roles.IRoleRepository;
using MerchantSetRolesCommand = Merchants.Application.Users.SetRolesCommand;
using MerchantAuthAudit = Merchants.Domain.Users.AuthAudit;
using MerchantAuthEventType = Merchants.Domain.Users.AuthEventType;
using MerchantUserInvitation = Merchants.Domain.Users.MerchantUserInvitation;
using Iam.Application.Roles;
using Iam.Domain.Permissions;
// Microsoft.OpenApi also declares a `Scope` type — alias to disambiguate the two call sites that need
// Iam's Platform/Merchant enum (GetPermissionCatalogQuery).
using Scope = Iam.Domain.Permissions.Scope;
using Iam.Domain.Roles;
using Api;
using Api.Admins;
using Api.BackgroundDispatch;
using Api.Customers;
using Api.Iam;
using Api.Governance;
using Api.ControlPlane;
using Api.Merchants;
using Api.Orders;
using Api.Reporting;
using Api.Persistence;
using Api.Webhooks;
using Api.Notifications;
using Persistence.ControlPlane;
using Persistence.ControlPlane.Governance;
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
builder.Services.AddSingleton(sp => new DefaultPspSelection(Codes.FromCode(
    sp.GetRequiredService<IOptions<PspOptions>>().Value.DefaultCode)));

// Document-search upstream. Connection strings come from section SpDocument ONLY — no derive/
// fallback (external-sim-separate-containers supersedes products-sp-gateway REQ-3.4: hippodb/
// mammothdb now each run on their own SQL Server instance, not beside the app database, so there
// is no single server to re-point InitialCatalog against). Unset -> host still boots (REQ-5.7 of
// products-sp-gateway, unchanged: SpDocumentOptions has no .ValidateOnStart()) and a products
// search request gets 503 until the values are configured. Pointing at the real motordb/centerdb
// needs more than these two values: docker-compose.prod.yml has no SpDocument__* key for `api`,
// HIPPO_DB_SERVER/MAMMOTH_DB_SERVER are `:?`-required, migrate-entrypoint.sh bootstraps the sim
// tier unconditionally, and docker/entrypoint.sh hardcodes the sim catalog/principal — see
// SpDocumentOptions.cs for the full four-layer breakdown and the new guard that now fails the
// container instead of silently overwriting an operator-set value.
builder.Services.Configure<SpDocumentOptions>(builder.Configuration.GetSection(SpDocumentOptions.SectionName));

builder.Services.AddProductsModule();
builder.Services.AddCartModule();
builder.Services.AddOrdersModule();
builder.Services.AddPaymentsModule();
builder.Services.AddMerchantsModule();

// The committed appsettings.json ships an App string with a BLANK password (the real secret is injected
// at runtime). Outside Development, fail fast if that injection did not happen — otherwise the host boots
// and only the first request discovers the missing credential. Development may use integrated auth.
if (!builder.Environment.IsDevelopment())
{
    ProvisioningGuards.RequireInjectedCredential(appConnString, "App");
    // Admin is Microsoft workforce-only. Production rejects missing/invalid Microsoft settings and any other
    // enabled provider before OIDC registration; Development/Staging may run with login disabled for local tests.
    if (builder.Environment.IsProduction())
        ProvisioningGuards.RequireWorkforceAdminProvider(builder.Configuration);

    // Merchant-user providers remain independently configurable; zero providers is allowed when intentionally
    // disabled (the schemes are skipped, REQ-14.2).
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
builder.Services.AddControlPlanePersistence(appConnString, ResolveControlPlaneWriteAuthorizer)
    .AddGovernanceOutboxDispatcher()
    .AddGovernanceAuditAnchoring();

static IWriteAuthorizer ResolveControlPlaneWriteAuthorizer(IServiceProvider sp) =>
    BackgroundDispatchScope.IsHttpRequest(sp)
        ? new ControlPlaneAdminWriteAuthorizer(sp.GetRequiredService<IAdminScope>())
        : new ControlPlaneWorkerWriteAuthorizer();

// multi-tier-deployment task 1: the outbox dispatchers (formerly the standalone Worker host's hosted
// services, PLAN "Worker merge") now run in THIS process, draining from a background-created scope with no
// HttpContext. Same scope-discriminated selection as IActorContext below (BackgroundDispatchScope) — an
// HTTP request gets the ordinary merchant-request write floor, a background dispatch scope gets the
// cross-merchant drain capability (WorkerWriteAuthorizer).
builder.Services.AddMerchantUserPersistence(appConnString, ResolveMerchantWriteAuthorizer)
    .AddMerchantUserOutboxDispatcher();
builder.Services.AddMerchantRuntimePersistence(appConnString, ResolveMerchantWriteAuthorizer)
    .AddMerchantRuntimeOutboxDispatcher();
builder.Services.AddScoped<OrderCreationCoordinator>();

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
builder.Services.Configure<UserInvitationOptions>(
    builder.Configuration.GetSection(UserInvitationOptions.SectionName));
builder.Services.AddSingleton<IInvitationDeliveryProtector, InvitationDeliveryProtector>();
if (builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("Testing"))
    builder.Services.AddSingleton<IInvitationEmailSender, CaptureInvitationEmailSender>();
else
    builder.Services.AddSingleton<IInvitationEmailSender, SmtpInvitationEmailSender>();
builder.Services.AddMerchantsIdentity();

// Central IAM audit bridge + assignment counter (rf2) — IRoleStore itself comes from
// AddControlPlanePersistence above. Needs IAdminScope/IAuditWriter (AddAdminIdentity) and the two
// count-reader ports (AddControlPlanePersistence/AddMerchantUserPersistence) already bound.
builder.Services.AddIamRoleManagement();

// MerchantUser BFF: a SECOND set of confidential OIDC clients (Authorization Code + PKCE) for the server-side
// merchant-user login, fully isolated from the Admin ones — distinct "MerchantUser{Provider}" schemes + callbacks +
// cookie names (REQ-8/9/14). Adds the schemes WITHOUT changing the default; a blank ClientId skips that provider's
// scheme so a half-configured env does not fault the whole host (REQ-14.2). The merchant-user session lifetime +
// cookie posture come from MerchantSession. Legacy MerchantUser:Session values are resolved once at startup.
builder.Services.Configure<UserOidcOptions>(builder.Configuration.GetSection(UserOidcOptions.SectionName));
builder.Services.AddConsoleConfiguration(builder.Configuration, builder.Environment);
builder.Services.AddMerchantUserOidcAuthentication(builder.Configuration, builder.Environment);

// MerchantUser BFF session scheme: authenticate merchant-user requests via the __Host-mch_session cookie and register the
// SINGLE-SCHEME "merchant-user" policy (MerchantUserSession only, T11 — the Bearer fallback is retired). Background
// sweep prunes expired sessions so the control-plane session table does not grow unbounded (REQ-10.4).
builder.Services.AddMerchantUserSessionScheme();
builder.Services.AddHostedService<UserSessionPruneService>();

// Independent TTL sweep for staged KYC/photo objects (Codex review #191) — see PhotoStagingPruneService
// for why the sweep inside LocalPhotoStore.PutStagedAsync alone cannot hold the advertised 24-hour bound.
builder.Services.AddHostedService<PhotoStagingPruneService>();

// Data Protection key ring for the admin OIDC handler (correlation/state/nonce cookies), persisted to the
// control-plane DataProtectionKeys table via the keyed pol_admin context (REQ-8, Tech #5). Lazy — no SQL at boot.
builder.Services.AddAdminDataProtection();

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
// ConsoleSession policy scheme is the default. Existing protected groups still pin their own scheme; dual-console
// routes use endpoint metadata plus cookie presence to select exactly one audience without cross-audience fallback.
builder.Services.AddAuthentication(ConsoleSessionAuthentication.SchemeName);

// Admin BFF: confidential OIDC clients (Authorization Code + PKCE) for the server-side admin login.
// Adds the "Admin{Provider}" OIDC + "oidc-noop" sign-in schemes WITHOUT changing the default set above.
builder.Services.Configure<AdminAuthOptions>(builder.Configuration.GetSection(AdminAuthOptions.SectionName));
builder.Services.AddAdminOidcAuthentication(builder.Configuration, builder.Environment);

// Admin BFF session scheme: authenticate every /api/v1/admins/* request via the __Host-adm_session cookie and
// REDEFINE the "admin" authorization policy to pin it — retiring the Bearer "admin" audience (REQ-4/5/9/10).
builder.Services.AddPlatformUserSessionScheme();
builder.Services.AddConsoleSessionAuthentication();

// Background sweep: delete sessions past their absolute expiry so the store does not grow unbounded (REQ-11.5).
builder.Services.AddHostedService<SessionPruneService>();

// CORS for the two credentialed console SPAs; the customer SPA uses a same-origin /api proxy.
builder.Services.AddPolCors();

// OpenAPI document so the SPA teams have a machine-readable contract (served in Development only). The
// document also declares the two auth schemes (merchant-user session cookie + admin session cookie) and tags
// each operation with the scheme its authorization policy requires, so other teams can authenticate straight
// from the Scalar reference UI.
Action<OpenApiOptions> configureOpenApi = options =>
{
    options.AddSchemaTransformer((schema, context, _) =>
    {
        // PspCode has a custom JsonConverter the schema generator can't introspect, so it would emit an
        // empty schema. Describe the real wire shape: the stable string codes from the PspCodes mapping.
        if (context.JsonTypeInfo.Type == typeof(Code)
            || Nullable.GetUnderlyingType(context.JsonTypeInfo.Type) == typeof(Code))
        {
            schema.Type = JsonSchemaType.String;
            schema.Enum = Enum.GetValues<Code>().Select(p => (JsonNode)JsonValue.Create(p.ToCode())).ToList();
        }
        if (context.JsonTypeInfo.Type == typeof(Money))
        {
            schema.Type = JsonSchemaType.Object;
            schema.Properties = new Dictionary<string, IOpenApiSchema>
            {
                ["amount"] = new OpenApiSchema
                {
                    Type = JsonSchemaType.String,
                    Pattern = @"^\d+\.\d{4}$",
                    Example = JsonValue.Create("1500.0000"),
                },
                ["currency"] = new OpenApiSchema
                {
                    Type = JsonSchemaType.String,
                    Pattern = "^[A-Z]{3}$",
                    Example = JsonValue.Create("THB"),
                },
            };
            schema.Required = new HashSet<string>(StringComparer.Ordinal) { "amount", "currency" };
        }
        if (context.JsonTypeInfo.Type == typeof(ProductListItem))
        {
            schema.Required ??= new HashSet<string>(StringComparer.Ordinal);
            schema.Required.Add("productCode");
            schema.Required.Add("variantCode");
            if (schema.Properties?["productCode"] is OpenApiSchema productCode)
                productCode.Type = JsonSchemaType.String;
            if (schema.Properties?["variantCode"] is OpenApiSchema variantCode)
                variantCode.Type = JsonSchemaType.String;
        }
        if (context.JsonTypeInfo.Type.IsGenericType
            && context.JsonTypeInfo.Type.GetGenericTypeDefinition() == typeof(PagedResult<>))
        {
            schema.Required ??= new HashSet<string>(StringComparer.Ordinal);
            schema.Required.Add("totalPages");
            if (schema.Properties?["totalPages"] is OpenApiSchema totalPages)
            {
                totalPages.Type = JsonSchemaType.Integer;
                totalPages.Pattern = null;
            }
        }
        return Task.CompletedTask;
    });

    // Operation-level: SFS endpoints read page/limit/filters/sort/search from the raw query string, so ASP.NET
    // emits no parameters for them. Declare them wherever the SfsQueryParamsMarker is present (REQ-13).
    options.AddOperationTransformer(async (operation, context, cancellationToken) =>
    {
        if (context.Description.ActionDescriptor.EndpointMetadata.OfType<SfsQueryParamsMarker>().FirstOrDefault()
            is { } sfs)
            SfsOpenApi.AddQueryParameters(operation, sfs.MaxLimit);
        // Products reads only page/limit + its own typed productFilters — no SFS surface to advertise (REQ-7.4).
        if (context.Description.ActionDescriptor.EndpointMetadata.OfType<ProductQueryParamsMarker>().Any())
            SfsOpenApi.AddProductQueryParameters(operation);
        foreach (var query in context.Description.ActionDescriptor.EndpointMetadata.OfType<RawQueryParamMarker>())
            SfsOpenApi.AddRawQueryParameter(operation, query);
        if (context.Description.ActionDescriptor.EndpointMetadata.OfType<IfMatchMutationMarker>().FirstOrDefault()
            is { } mutation)
            ConcurrencyOpenApi.Apply(operation, mutation);
        else if (context.Description.ActionDescriptor.EndpointMetadata.OfType<EtagResponseMarker>().FirstOrDefault()
                 is { } etagResponse)
            ConcurrencyOpenApi.Apply(operation, etagResponse);
        if (context.Description.ActionDescriptor.EndpointMetadata.OfType<IdempotencyMutationMarker>().FirstOrDefault()
            is { } idempotency)
            ConcurrencyOpenApi.Apply(operation, idempotency);
        if (context.Description.ActionDescriptor.EndpointMetadata.OfType<AdminIfMatchMutationMarker>().FirstOrDefault()
            is { } adminMutation)
            ConcurrencyOpenApi.Apply(operation, adminMutation);
        else if (context.Description.ActionDescriptor.EndpointMetadata.OfType<AdminEtagResponseMarker>().FirstOrDefault()
                 is { } adminEtag)
            ConcurrencyOpenApi.Apply(operation, adminEtag);
        if (context.Description.ActionDescriptor.EndpointMetadata.OfType<AdminIdempotencyMutationMarker>().Any())
            ConcurrencyOpenApi.Apply(operation, new AdminIdempotencyMutationMarker());
        if (context.Description.HttpMethod is { } method
            && !HttpMethods.IsGet(method)
            && !HttpMethods.IsHead(method)
            && !HttpMethods.IsOptions(method)
            && !HttpMethods.IsTrace(method)
            && context.Description.ActionDescriptor.EndpointMetadata.OfType<CsrfProtected>()
                .Any(x => string.Equals(x.SchemeId, "AdminSession", StringComparison.Ordinal)))
        {
            var parameters = operation.Parameters ??= [];
            var existing = parameters.FirstOrDefault(x => x.In == ParameterLocation.Header
                && string.Equals(x.Name, CsrfFilter.HeaderName, StringComparison.OrdinalIgnoreCase));
            if (existing is OpenApiParameter csrf)
            {
                csrf.Required = true;
            }
            else if (existing is null)
            {
                parameters.Add(new OpenApiParameter
                {
                    Name = CsrfFilter.HeaderName,
                    In = ParameterLocation.Header,
                    Required = true,
                    Description = "CSRF token matching the AdminSession CSRF cookie.",
                    Schema = new OpenApiSchema { Type = JsonSchemaType.String },
                });
            }
        }
        if (context.Description.ActionDescriptor.EndpointMetadata.OfType<AudienceRequestBodyMarker>().FirstOrDefault()
            is { } audienceRequest)
            await AudienceOpenApi.ApplyAsync(operation, context, audienceRequest, cancellationToken);
        if (context.Description.ActionDescriptor.EndpointMetadata.OfType<AudienceResponseMarker>().FirstOrDefault()
            is { } audienceResponse)
            await AudienceOpenApi.ApplyAsync(operation, context, audienceResponse, cancellationToken);
        if (context.Description.ActionDescriptor.EndpointMetadata.OfType<GovernanceDecisionMarker>().FirstOrDefault()
            is { } decision)
            GovernanceOpenApi.Apply(operation, decision);
        else if (context.Description.ActionDescriptor.EndpointMetadata.OfType<GovernanceEtagMarker>().FirstOrDefault()
                 is { } etag)
            GovernanceOpenApi.Apply(operation, etag);
    });

    // Document-level: title/description + the security schemes other teams pick from Scalar's auth dropdown,
    // plus the per-operation security requirement each route's authorization policy implies.
    options.AddDocumentTransformer((document, context, _) =>
    {
        document.Info.Title = OpenApiDocuments.Title(context.DocumentName);
        document.Info.Version = "v1";
        document.Info.Description = OpenApiDocuments.Description(context.DocumentName);

        // x-tagGroups nests active route tags under their src/Modules/* owner instead of a flat tag list.
        // OpenApiDocuments owns the canonical map and removes groups/tags absent from this audience document.
        document.Extensions ??= new Dictionary<string, IOpenApiExtension>();
        document.Extensions["x-tagGroups"] = OpenApiDocuments.CreateTagGroups(document);

        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        if (OpenApiDocuments.IncludesSecurityScheme(context.DocumentName, "AdminSession"))
            document.Components.SecuritySchemes["AdminSession"] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.ApiKey,
                In = ParameterLocation.Cookie,
                // Scalar/OpenAPI serve in Development only, where the default host is dev HTTP and the handler
                // writes the non-__Host cookie. Document that name, not the prod one, so admins testing in /scalar
                // see the cookie they actually have.
                Name = SessionCookies.SessionCookieNameDevHttp,
                Description = "คุกกี้ session ของ Admin Console ที่ browser ได้รับอัตโนมัติหลังเข้าสู่ระบบผ่าน "
                    + "GET /api/v1/admins/auth/{provider}/login โดย provider คือ microsoft; "
                    + "บน production (HTTPS) ใช้ชื่อ `__Host-adm_session`",
            };
        if (OpenApiDocuments.IncludesSecurityScheme(context.DocumentName, "MerchantUserSession"))
            document.Components.SecuritySchemes["MerchantUserSession"] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.ApiKey,
                In = ParameterLocation.Cookie,
                Name = UserSessionCookies.SessionCookieNameDevHttp,
                Description = "คุกกี้ session ของ Merchant Console ที่ browser ได้รับอัตโนมัติหลังเข้าสู่ระบบผ่าน "
                    + "GET /api/v1/merchants/auth/{provider}/login โดย provider คือ microsoft; "
                    + "บน production (HTTPS) ใช้ชื่อ `__Host-mch_session`",
            };

        // Per-operation: attach the scheme each route's authorization policy requires so Scalar shows the right
        // auth on the right endpoint (merchant-user -> MerchantUserSession, admin -> AdminSession). The host
        // document is passed so the requirement serialises as a $ref into components.securitySchemes. Anonymous
        // routes (order summary link, admin login, webhook) carry no requirement.
        var schemesByRoute = new Dictionary<(string Path, string Method), IReadOnlyList<string>>();
        foreach (var d in context.ApplicationServices
                     .GetRequiredService<IApiDescriptionGroupCollectionProvider>()
                     .ApiDescriptionGroups.Items.SelectMany(g => g.Items))
        {
            var schemeIds = AuthPolicyScheme.SecuritySchemeIdsFor(d.ActionDescriptor.EndpointMetadata);
            if (schemeIds.Count > 0 && d.RelativePath is not null && d.HttpMethod is not null)
            {
                // RelativePath keeps route constraints ("{cartId:guid}"); the OpenAPI path strips them
                // ("{cartId}"). Normalise so the two keys match.
                var path = RouteConstraintRegex().Replace("/" + d.RelativePath.TrimStart('/'), "{$1}");
                if (path.Length > 1)
                    path = path.TrimEnd('/');
                schemesByRoute[(path, d.HttpMethod.ToUpperInvariant())] = schemeIds;
            }
        }
        foreach (var (pathKey, pathItem) in document.Paths)
        {
            if (pathItem.Operations is null)
                continue;
            foreach (var (method, operation) in pathItem.Operations)
                if (schemesByRoute.TryGetValue((pathKey, method.Method.ToUpperInvariant()), out var schemeIds))
                {
                    schemeIds = OpenApiDocuments.SecuritySchemeIds(context.DocumentName, schemeIds);
                    operation.Security ??= [];
                    foreach (var schemeId in schemeIds)
                        operation.Security.Add(new OpenApiSecurityRequirement
                        {
                            [new OpenApiSecuritySchemeReference(schemeId, document)] = [],
                        });
                }
        }
        return Task.CompletedTask;
    });
};

foreach (var documentName in OpenApiDocuments.All)
    builder.Services.AddOpenApi(documentName, options =>
    {
        options.ShouldInclude = description => OpenApiDocuments.ShouldInclude(documentName, description);
        configureOpenApi(options);
    });

// PspCode crosses the wire as its stable code ("2c2p"/"omise") via the domain's PspCodes mapping —
// not as an int or the C# member name. An unknown code fails body binding -> 400.
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new UtcDateTimeJsonConverter());
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
builder.Services.AddCustomerPaymentRateLimiter();

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

if (!app.Environment.IsDevelopment())
    UserInvitationOptions.RequireProduction(app.Configuration);

// Dev convenience: auto-apply pending EF migrations at boot so a freshly merged migration can't leave the
// local DB desynced from the code (the symptom is a runtime "Invalid object name" -> resolve-failed login).
// The runtime pol_app/pol_admin logins have no DDL rights, so this runs on the privileged Migrator
// connection from the gitignored appsettings.Development.json. Absent -> skip with a warning, never crash a
// healthy boot. Prod migrates out-of-band as sa via docker/migrate-entrypoint.sh, NOT here.
if (app.Environment.IsDevelopment()
    && app.Configuration.GetConnectionString("Migrator") is { Length: > 0 } migratorConn)
{
    var options = new DbContextOptionsBuilder<PolDbContext>()
        .UseSqlServer(migratorConn, sql => sql.UseCompatibilityLevel(170)).Options;
    using var migrateDb = new PolDbContext(options, app.Services.GetRequiredService<ModuleAssemblies>());
    await migrateDb.Database.MigrateAsync();
    app.Logger.LogInformation("Applied pending EF migrations (Development, Migrator connection).");
}
else if (app.Environment.IsDevelopment())
{
    app.Logger.LogWarning("ConnectionStrings:Migrator not set — skipping Development auto-migrate.");
}

var adminMicrosoftTenant = app.Services.GetRequiredService<Api.Admins.AdminMicrosoftTenantSnapshot>();
if (adminMicrosoftTenant.TenantId is { } workforceTenantId)
{
    await using var tenantPinScope = app.Services.CreateAsyncScope();
    await tenantPinScope.ServiceProvider.GetRequiredService<IWorkforceTenantBindingStore>()
        .EnsureAsync(workforceTenantId, CancellationToken.None);
}

// Fail-fast: build the vault keyring now so a missing/short/invalid master key crash-loops the host at
// boot instead of surfacing only on the first reveal. ValidateOnBuild does NOT run factory-registered
// singletons, so this explicit resolve is what delivers the boot-time custody guarantee.
_ = app.Services.GetRequiredService<VaultKeyring>();
// Same guarantee for customer-payment PSP selection: read final host configuration after Build, then reject
// unknown codes before readiness instead of waiting for the first payment request.
_ = app.Services.GetRequiredService<DefaultPspSelection>();

// Outside Development, the OIDC correlation cookies must ride a persisted, shared key ring — never the
// framework's default ephemeral one (REQ-8.2). Assert it now so a misconfigured key store crash-loops at boot.
if (!app.Environment.IsDevelopment())
    AdminDataProtection.RequirePersistentDataProtection(app.Services);

// Forwarded headers FIRST so every downstream middleware (auth, and the OIDC redirect_uri builder) sees the
// browser-facing host/scheme, not this process's. The admin SPA dev server proxies /api/v1/admins/* here, so the OIDC
// redirect_uri must be the SPA origin (e.g. https://localhost:3001) to match the registered Entra redirect URI; the
// same applies to a TLS-terminating reverse proxy in prod (scheme must read https). Default trust = loopback
// only, which covers the localhost dev proxy. A containerized prod proxy connects from the (non-loopback)
// docker/private network, and .NET only honours forwarded headers from a TRUSTED peer — otherwise it silently
// ignores X-Forwarded-* and the redirect_uri keeps this process's internal host (Entra then rejects login with
// redirect_uri_mismatch). Trust the real proxy ADDITIVELY from config so the localhost dev proxy keeps working:
// ForwardedHeaders:KnownNetworks = CIDRs (e.g. the docker subnet "172.18.0.0/16"), KnownProxies = single IPs.
// Both empty (the default) = loopback only.
var forwardedHeaders = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor
        | ForwardedHeaders.XForwardedHost
        | ForwardedHeaders.XForwardedProto,
};
foreach (var cidr in app.Configuration.GetSection("ForwardedHeaders:KnownNetworks").Get<string[]>() ?? [])
{
    if (string.IsNullOrWhiteSpace(cidr)) // an unset env expands to a blank entry — skip, don't Parse("")
        continue;

    var network = System.Net.IPNetwork.Parse(cidr.Trim());
    if (network.PrefixLength == 0)
        throw new InvalidOperationException("ForwardedHeaders:KnownNetworks must not trust a wildcard network.");
    forwardedHeaders.KnownIPNetworks.Add(network);
}
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
    // Scalar reference UI over the audience documents — anonymous like the health checks. The combined v1
    // document remains available at its old URL for generated-client compatibility but stays out of the selector.
    app.MapScalarApiReference(options => options
        .WithTitle("pol-core API")
        .AddDocument(OpenApiDocuments.Merchant, "Merchant API", isDefault: true)
        .AddDocument(OpenApiDocuments.Admin, "Admin API")
        .AddDocument(OpenApiDocuments.Integration, "Integration API"));
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
var requiredMerchantQuery = new RawQueryParamMarker(
    "merchantId", "Merchant UUID required for the AdminSession branch.");
var optionalMerchantQuery = requiredMerchantQuery with
{
    Description = "Optional merchant UUID filter within the AdminSession scope.",
    Required = false,
};
var requiredOriginatorQuery = new RawQueryParamMarker(
    "originatorId", "Originator UUID required for the AdminSession branch.");
var requiredExportFromQuery = new RawQueryParamMarker(
    "from", "Inclusive UTC export-window start.");
var requiredExportToQuery = new RawQueryParamMarker(
    "to", "Inclusive UTC export-window end; maximum window is 31 days.");
api.MapGovernanceEndpoints();
api.MapAdminControlEndpoints();
api.MapApiClientEndpoints();
api.MapDeliveryEndpoints();
api.MapInboundWebhookEndpoints();
api.MapAdminMerchantIdentityEndpoints();
api.MapAdminReportingEndpoints();
api.MapPaymentCapabilityEndpoints();

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
    .ProducesProblem(StatusCodes.Status429TooManyRequests)
    .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

// T11 (rf1 big-bang): the merchant-Bearer fallback is retired, so every write endpoint gates on the single-scheme
// "merchant-user" policy + its permission unconditionally — the former MerchantUser:EnforcePermissionsOnWrites
// toggle (a transitional un-gated Bearer state) no longer has a Bearer path to fall back to, so it is deleted.

// GET /products — the document catalogue, and the ONLY product endpoint: the catalogue is read-only over HTTP
// (REQ-1.1) because the documents originate in the upstream policy system, not from a merchant filling in a form.
// It carries no merchant of its own, so the request is scoped by the merchant user's OWN saleCode (server-side,
// never a client field — REQ-4.8) and gated by the merchant-user policy; the input surface is SP guide §2 minus
// @SaleCode: paging plus the optional typed productFilters. The search runs live against the upstream procedures
// and nothing is mirrored (REQ-1.2), so 503 is a real outcome here: the upstream being unreachable is not a 500
// of ours (REQ-7.1). A merchant user with no saleCode bound is refused with 403 BEFORE the filters are parsed
// (REQ-4.9), so someone with no catalogue access cannot probe filter shapes.
api.MapGet("/products", async (HttpContext http, IActorContext actor, IMediator mediator, CancellationToken ct) =>
{
    if (string.IsNullOrEmpty(actor.SaleCode))
        return Results.Problem(statusCode: StatusCodes.Status403Forbidden,
            title: "No sale code is bound to this merchant user.",
            extensions: new Dictionary<string, object?> { ["code"] = "sale-code-missing" });
    var p = SfsQueryParser.ParsePaging(http.Request.Query);
    var result = await mediator.Send(new ListProductsQuery
    {
        Page = p.Page,
        Limit = p.Limit,
        SaleCode = actor.SaleCode,
        ProductFilters = ProductFilterDto.Parse(http.Request.Query["productFilters"]),
    }, ct);
    return Results.Ok(result);
})
    .RequireAuthorization("merchant-user").RequirePermission(Keys.PaymentView)
    .WithMetadata(new ProductQueryParamsMarker())
    .WithTags("ผลิตภัณฑ์")
    .WithName("ListProducts")
    .WithSummary("รายการผลิตภัณฑ์")
    .WithDescription("รายการเอกสารประกันแบบแบ่งหน้า ค้นสดจากระบบต้นทาง (ไม่บันทึกสำเนา) รับ page, limit และ productFilters ตาม §2 ของเอกสาร SP โดย saleCode มาจากผู้ใช้ที่ยืนยันตัวตนแล้วฝั่ง server (client กำหนดเองไม่ได้); ต้องระบุ insuranceType (Motor|NonMotor) เมื่อไม่ได้ส่ง productGroup และห้ามขัดแย้งกับ productGroup; countMode (EXACT|FAST ค่าเริ่มต้น EXACT) — FAST ให้ totalRows/totalPages เป็น null; แต่ละแถวมี soldByPlatform บอกว่าเอกสารถูกขายผ่านแพลตฟอร์มนี้แล้วหรือไม่ และเมื่อ paymentStatus เป็น UNPAID (ค่าเริ่มต้น) จะตัดเอกสารที่ขายแล้วออก; ผู้ใช้ที่ไม่มี saleCode -> 403")
    .Produces<ProductPage>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status403Forbidden)
    .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

api.MapGet("/products/documents", async (
    HttpContext http,
    IAdminScope adminScope,
    IAdminMerchantControlStore merchants,
    IMediator mediator,
    CancellationToken ct) =>
{
    var merchantId = RequireCommerceQueryGuid(http, "merchantId");
    var originatorId = RequireCommerceQueryGuid(http, "originatorId");
    var originator = await RequireCommerceOriginatorAsync(
        merchants, adminScope, merchantId, originatorId, ct);
    var paging = SfsQueryParser.ParsePaging(http.Request.Query);
    var result = await mediator.Send(new ListProductsQuery
    {
        Page = paging.Page,
        Limit = paging.Limit,
        SaleCode = originator.SaleCode!,
        ProductFilters = ProductFilterDto.Parse(http.Request.Query["productFilters"]),
    }, ct);
    return Results.Ok(AdminProductPage.From(merchantId, originatorId, result));
}).RequireAuthorization("admin").RequirePermission(Keys.TxnView)
    .WithMetadata(
        new ProductQueryParamsMarker(),
        requiredMerchantQuery,
        requiredOriginatorQuery)
    .WithTags("ผลิตภัณฑ์")
    .WithName("ListAdminProductDocuments")
    .WithSummary("รายการเอกสารประกันสำหรับผู้ดูแลระบบ")
    .WithDescription("อ่านเอกสารสดด้วย merchantId และ originatorId ที่อยู่ใน Admin scope; saleCode มาจาก Originator ฝั่ง server")
    .Produces<AdminProductPage>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status403Forbidden)
    .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

// Cart — open, add/merge lines, review, adjust, clear. Merchant comes from the principal; the commands are
// IMerchantScoped so RLS + the merchant guard confine every cart to the bound merchant.
api.MapPost("/carts", async (
    HttpContext http,
    IActorContext actor,
    IActorScope actorScope,
    IAdminScope adminScope,
    IAdminMerchantControlStore merchants,
    IAdminOperationExecutor operations,
    IMediator mediator,
    CancellationToken ct) =>
{
    if (!IsAdminCommerceRequest(http))
    {
        var id = await mediator.Send(new CreateCartCommand(actor.MerchantId, actor.SaleCode), ct);
        return Results.Ok(new CreateCartResponse(id));
    }

    var merchantId = RequireCommerceQueryGuid(http, "merchantId");
    var originatorId = RequireCommerceQueryGuid(http, "originatorId");
    var originator = await RequireCommerceOriginatorAsync(
        merchants, adminScope, merchantId, originatorId, ct);
    using var actorBinding = actorScope.Begin(merchantId);
    var result = await ExecuteAdminCommerceAsync(
        operations, adminScope, merchantId, "cart.create", IdempotencyKeys.Require(http),
        new { merchantId, originatorId }, 200,
        async token => new CreateCartResponse(await mediator.Send(
            new CreateCartCommand(merchantId, originator.SaleCode, originatorId), token)),
        value => value.CartId.ToString("D"), ct);
    return Results.Ok(result.Value);
}).RequireAuthorization(ConsoleSessionAuthentication.PolicyName)
    .RequireAudiencePermission(Keys.TxnManage, Keys.PaymentCreate).RequireAudienceCsrf()
    .WithMetadata(
        new AdminIdempotencyMutationMarker(),
        requiredMerchantQuery,
        requiredOriginatorQuery)
    .WithTags("ตะกร้าสินค้า")
    .WithName("CreateCart")
    .WithSummary("เปิดตะกร้าสินค้า")
    .WithDescription("Merchant Console เปิดตะกร้าด้วยร้านค้าและ saleCode จาก session; Admin Console ต้องส่ง merchantId และ originatorId แล้วระบบใช้ saleCode ของ Originator ฝั่ง server")
    .Produces<CreateCartResponse>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status401Unauthorized);

api.MapPost("/carts/{cartId:guid}/items", async (
    Guid cartId,
    AddItemToCartRequest body,
    HttpContext http,
    IActorContext actor,
    IActorScope actorScope,
    IAdminScope adminScope,
    IAdminCartReader adminCarts,
    IAdminMerchantControlStore merchants,
    IAdminOperationExecutor operations,
    IMediator mediator,
    IDocumentSaleProbe documentSales,
    CancellationToken ct) =>
{
    var admin = IsAdminCommerceRequest(http);
    if (!admin)
    {
        var command = await BuildAddCartItemCommandAsync(
            cartId, actor.MerchantId, actor.SaleCode, body, null, false, mediator, documentSales, ct);
        return Results.Ok(await mediator.Send(command, ct));
    }

    var merchantId = RequireCommerceQueryGuid(http, "merchantId");
    var cart = await RequireAdminCartAsync(
        adminCarts, adminScope, cartId, merchantId, mutation: true, ct);
    if (cart.OriginatorId is not { } originatorId)
        throw new ConflictException("Cart has no originator.", "state_conflict");
    var originator = await RequireCommerceOriginatorAsync(
        merchants, adminScope, merchantId, originatorId, ct);
    var adminCommand = await BuildAddCartItemCommandAsync(
        cartId, merchantId, originator.SaleCode, body, null, true, mediator, documentSales, ct);
    using var actorBinding = actorScope.Begin(merchantId);
    var result = await ExecuteAdminCommerceAsync(
        operations, adminScope, merchantId, "cart.item.add", IdempotencyKeys.Require(http),
        new { cartId, merchantId, body.ProductCode, body.VariantCode, body.Quantity }, 200,
        token => mediator.Send(adminCommand, token).AsTask(),
        _ => cartId.ToString("D"), ct);
    VersionEtags.Set(http, result.Value.Version);
    return Results.Ok(result.Value);
}).RequireAuthorization(ConsoleSessionAuthentication.PolicyName)
    .RequireAudiencePermission(Keys.TxnManage, Keys.PaymentCreate).RequireAudienceCsrf()
    .WithMetadata(
        new AdminEtagResponseMarker("200"),
        new AdminIdempotencyMutationMarker(),
        requiredMerchantQuery)
    .WithTags("ตะกร้าสินค้า")
    .WithName("AddCartItem")
    .WithSummary("เพิ่มรายการสินค้าในตะกร้า")
    .WithDescription("เพิ่มเอกสารประกันเข้าตะกร้าด้วย productCode + variantCode + quantity โดยอ่านเอกสารสดจากต้นทางเพื่อตั้งราคา ชื่อ variant และ metadata ฝั่ง server; ไม่รับราคา/metadata จาก client; ไม่พบหรือไม่พร้อมขาย -> 400, ไม่มี saleCode -> 403, ต้นทางล่ม -> 503")
    .Produces<CartView>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status403Forbidden)
    .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

api.MapGet("/carts/{cartId:guid}", async (
    Guid cartId,
    HttpContext http,
    IActorContext actor,
    IActorScope actorScope,
    IAdminScope adminScope,
    IAdminCartReader adminCarts,
    IMediator mediator,
    CancellationToken ct) =>
{
    if (!IsAdminCommerceRequest(http))
    {
        var merchantView = await mediator.Send(new GetCartQuery(cartId, actor.MerchantId), ct);
        return merchantView is null ? Results.NotFound() : Results.Ok(merchantView);
    }

    var cart = await RequireAdminCartAsync(
        adminCarts, adminScope, cartId, expectedMerchantId: null, mutation: false, ct);
    using var actorBinding = actorScope.Begin(cart.MerchantId);
    var view = await mediator.Send(new GetCartQuery(cartId, cart.MerchantId), ct);
    if (view is not null)
        VersionEtags.Set(http, view.Version);
    return view is null ? Results.NotFound() : Results.Ok(view);
}).RequireAuthorization(ConsoleSessionAuthentication.PolicyName)
    .RequireAudiencePermission(Keys.TxnView, Keys.PaymentView)
    .WithMetadata(new AdminEtagResponseMarker("200"))
    .WithTags("ตะกร้าสินค้า")
    .WithName("GetCart")
    .WithSummary("ดูตะกร้าสินค้า")
    .WithDescription("คืนตะกร้าพร้อม line ทั้งหมดและ subtotal ที่คำนวณราคาแล้ว หากไม่พบตะกร้า -> 404")
    .Produces<CartView>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

api.MapDelete("/carts/{cartId:guid}/items/{itemId:guid}", async (
    Guid cartId, Guid itemId, HttpContext http, IActorContext actor, IActorScope actorScope,
    IAdminScope adminScope, IAdminCartReader adminCarts, IAdminOperationExecutor operations,
    IMediator mediator, CancellationToken ct) =>
{
    if (!IsAdminCommerceRequest(http))
        return Results.Ok(await mediator.Send(
            new RemoveItemFromCartCommand(cartId, actor.MerchantId, itemId), ct));

    var merchantId = RequireCommerceQueryGuid(http, "merchantId");
    var cart = await RequireAdminCartAsync(adminCarts, adminScope, cartId, merchantId, true, ct);
    var expected = RequireCartVersion(http);
    using var actorBinding = actorScope.Begin(merchantId);
    var result = await ExecuteAdminCommerceAsync(
        operations, adminScope, merchantId, "cart.item.remove", IdempotencyKeys.Require(http),
        new { cartId, itemId, merchantId, expected }, 200,
        token => mediator.Send(
            new RemoveItemFromCartCommand(cartId, merchantId, itemId, expected), token).AsTask(),
        _ => cart.CartId.ToString("D"), ct);
    VersionEtags.Set(http, result.Value.Version);
    return Results.Ok(result.Value);
}).RequireAuthorization(ConsoleSessionAuthentication.PolicyName)
    .RequireAudiencePermission(Keys.TxnManage, Keys.PaymentCreate).RequireAudienceCsrf()
    .WithMetadata(
        new AdminIfMatchMutationMarker("200"),
        new AdminIdempotencyMutationMarker(),
        requiredMerchantQuery)
    .WithTags("ตะกร้าสินค้า")
    .WithName("RemoveCartItem")
    .WithSummary("ลบรายการในตะกร้า")
    .WithDescription("ลบรายการออกจากตะกร้าด้วย itemId (รหัสรายการ ไม่ใช่ productCode) แล้วคืนตะกร้าที่อัปเดตแล้ว หากไม่พบ itemId -> 404")
    .Produces<CartView>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

api.MapPut("/carts/{cartId:guid}/items/{itemId:guid}", async (
    Guid cartId, Guid itemId, SetCartItemQuantityRequest body, HttpContext http,
    IActorContext actor, IActorScope actorScope, IAdminScope adminScope,
    IAdminCartReader adminCarts, IAdminOperationExecutor operations,
    IMediator mediator, CancellationToken ct) =>
{
    if (!IsAdminCommerceRequest(http))
        return Results.Ok(await mediator.Send(
            new SetCartItemQuantityCommand(cartId, actor.MerchantId, itemId, body.Quantity), ct));

    var merchantId = RequireCommerceQueryGuid(http, "merchantId");
    var cart = await RequireAdminCartAsync(adminCarts, adminScope, cartId, merchantId, true, ct);
    var expected = RequireCartVersion(http);
    using var actorBinding = actorScope.Begin(merchantId);
    var result = await ExecuteAdminCommerceAsync(
        operations, adminScope, merchantId, "cart.item.quantity", IdempotencyKeys.Require(http),
        new { cartId, itemId, merchantId, body.Quantity, expected }, 200,
        token => mediator.Send(new SetCartItemQuantityCommand(
            cartId, merchantId, itemId, body.Quantity, expected), token).AsTask(),
        _ => cart.CartId.ToString("D"), ct);
    VersionEtags.Set(http, result.Value.Version);
    return Results.Ok(result.Value);
}).RequireAuthorization(ConsoleSessionAuthentication.PolicyName)
    .RequireAudiencePermission(Keys.TxnManage, Keys.PaymentCreate).RequireAudienceCsrf()
    .WithMetadata(
        new AdminIfMatchMutationMarker("200"),
        new AdminIdempotencyMutationMarker(),
        requiredMerchantQuery)
    .WithTags("ตะกร้าสินค้า")
    .WithName("SetCartItemQuantity")
    .WithSummary("ปรับจำนวนรายการในตะกร้า")
    .WithDescription("ปรับจำนวนของรายการด้วย itemId แล้วคืนตะกร้าที่อัปเดตแล้ว หากไม่พบ itemId -> 404")
    .Produces<CartView>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

api.MapPost("/carts/{cartId:guid}/clear", async (
    Guid cartId, HttpContext http, IActorContext actor, IActorScope actorScope,
    IAdminScope adminScope, IAdminCartReader adminCarts, IAdminOperationExecutor operations,
    IMediator mediator, CancellationToken ct) =>
{
    if (!IsAdminCommerceRequest(http))
        return Results.Ok(await mediator.Send(new ClearCartCommand(cartId, actor.MerchantId), ct));

    var merchantId = RequireCommerceQueryGuid(http, "merchantId");
    var cart = await RequireAdminCartAsync(adminCarts, adminScope, cartId, merchantId, true, ct);
    var expected = RequireCartVersion(http);
    using var actorBinding = actorScope.Begin(merchantId);
    var result = await ExecuteAdminCommerceAsync(
        operations, adminScope, merchantId, "cart.clear", IdempotencyKeys.Require(http),
        new { cartId, merchantId, expected }, 200,
        token => mediator.Send(new ClearCartCommand(cartId, merchantId, expected), token).AsTask(),
        _ => cart.CartId.ToString("D"), ct);
    VersionEtags.Set(http, result.Value.Version);
    return Results.Ok(result.Value);
}).RequireAuthorization(ConsoleSessionAuthentication.PolicyName)
    .RequireAudiencePermission(Keys.TxnManage, Keys.PaymentCreate).RequireAudienceCsrf()
    .WithMetadata(
        new AdminIfMatchMutationMarker("200"),
        new AdminIdempotencyMutationMarker(),
        requiredMerchantQuery)
    .WithTags("ตะกร้าสินค้า")
    .WithName("ClearCart")
    .WithSummary("ล้างตะกร้าสินค้า")
    .WithDescription("ลบทุก line ออกจากตะกร้า แล้วคืนตะกร้าที่ว่างแล้ว")
    .Produces<CartView>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

var createPaymentSession = api.MapPost("/payments/sessions", async (
    CreatePaymentSessionRequest body,
    HttpContext http,
    IActorContext actor,
    IActorScope actorScope,
    IAdminScope adminScope,
    IAdminOrderReader adminOrders,
    IAdminPaymentRoutingSelector routing,
    IAdminOperationExecutor operations,
    IMediator mediator,
    CancellationToken ct) =>
{
    if (!IsAdminCommerceRequest(http))
    {
        if (body.MerchantId is not null || body.Psp is null)
            throw new InvalidRequestException(
                "Merchant payment session requires psp and forbids merchantId.", "validation_failed");
        var merchantResult = await mediator.Send(new CreateSessionCommand(
            body.OrderId, actor.MerchantId, body.Method, body.Psp.Value), ct);
        return Results.Ok(new CreatePaymentSessionResponse(merchantResult.PaymentSessionId));
    }

    if (body.MerchantId is not { } merchantId || merchantId == Guid.Empty || body.Psp is not null)
        throw new InvalidRequestException(
            "Admin payment session requires merchantId and forbids psp.", "validation_failed");
    var order = await RequireAdminOrderAsync(
        adminOrders, adminScope, body.OrderId, merchantId, mutation: true, ct);
    var psp = await routing.SelectAsync(merchantId, order.OrderId, body.Method, ct);
    using var actorBinding = actorScope.Begin(merchantId);
    var result = await ExecuteAdminCommerceAsync(
        operations, adminScope, merchantId, "payment-session.create", IdempotencyKeys.Require(http),
        new { body.OrderId, body.Method, merchantId }, 200,
        async token =>
        {
            var created = await mediator.Send(
                new CreateSessionCommand(body.OrderId, merchantId, body.Method, psp), token);
            return new CreatePaymentSessionResponse(created.PaymentSessionId);
        },
        value => value.PaymentSessionId.ToString("D"), ct);
    return Results.Ok(result.Value);
});
createPaymentSession.RequireAuthorization(ConsoleSessionAuthentication.PolicyName)
    .RequireAudiencePermission(Keys.TxnManage, Keys.PaymentCreate).RequireAudienceCsrf()
    .WithMetadata(
        new AdminIdempotencyMutationMarker(),
        new AudienceRequestBodyMarker(
            typeof(MerchantCreatePaymentSessionRequest), typeof(AdminCreatePaymentSessionRequest)))
    .WithTags("การชำระเงิน")
    .WithName("CreatePaymentSession")
    .WithSummary("สร้าง payment session")
    .WithDescription("เปิด payment session โดยอ่านยอดจาก Order ฝั่ง server เท่านั้น Merchant Console ส่ง psp; Admin Console ส่ง merchantId และระบบเลือก PSP จาก routing หาก method ไม่ใช่ card/promptpay/installment -> 400, ไม่พบ Order -> 404, สถานะหรือ PSP connection ใช้งานไม่ได้ -> 409")
    .Produces<CreatePaymentSessionResponse>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status403Forbidden)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status409Conflict)
    .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

api.MapGet("/payments/sessions", async (
    HttpContext http,
    IMediator mediator,
    CancellationToken ct) =>
{
    var parsed = SfsQueryParser.Parse(http.Request.Query, maxLimit: 100);
    var result = await mediator.Send(new ListPaymentSessionsQuery
    {
        Page = parsed.Page,
        Limit = parsed.Limit,
        Filters = parsed.Filters,
        Sort = parsed.Sort,
        Search = parsed.Search,
    }, ct);
    return Results.Ok(result);
}).RequireAuthorization("merchant-user").RequirePermission(Keys.PaymentView)
    .WithMetadata(new SfsQueryParamsMarker(100))
    .WithTags("การชำระเงิน")
    .WithName("ListPaymentSessions")
    .WithSummary("รายการ payment session ของร้านค้า")
    .WithDescription("คืนรายการแบบแบ่งหน้า รองรับ filters status/method/psp และ sort createdAt/updatedAt; ค่าเริ่มต้น 25 สูงสุด 100")
    .Produces<PagedResult<PaymentSessionListItem>>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status403Forbidden)
    .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

// Claims-then-charges redirect (PLAN #11). Merchant scoping is automatic: the command is IMerchantScoped, so
// MerchantGuardBehavior + RLS resolve the session for the authenticated merchant only. Errors flow through the
// shared ProblemDetails handler (not found -> 404, illegal state / concurrent claim -> 409).
var startRedirect = api.MapPost("/payments/sessions/{paymentSessionId:guid}/redirect", async (
    Guid paymentSessionId,
    HttpContext http,
    IActorScope actorScope,
    IAdminScope adminScope,
    IAdminPaymentSessionReader adminSessions,
    IAdminOperationExecutor operations,
    IMediator mediator,
    CancellationToken ct) =>
{
    if (!IsAdminCommerceRequest(http))
    {
        var merchantResult = await mediator.Send(new StartRedirectCommand(paymentSessionId), ct);
        return Results.Ok(new StartRedirectResponse(merchantResult.RedirectUrl));
    }

    var merchantId = RequireCommerceQueryGuid(http, "merchantId");
    var resource = await adminSessions.ResolveAsync(
        paymentSessionId, adminScope.Accessible.IsUnrestricted, adminScope.Accessible.Merchants, ct)
        ?? throw new NotFoundException("Payment session was not found.");
    if (resource.MerchantId != merchantId)
        throw new AccessDeniedException(
            "Payment session does not belong to the selected merchant.", "merchant_scope_forbidden");
    var expected = VersionEtags.Require(http);
    using var actorBinding = actorScope.Begin(merchantId);
    var result = await ExecuteRecoverableAdminCommerceAsync(
        operations, adminScope, merchantId, "payment-session.redirect", IdempotencyKeys.Require(http),
        new { paymentSessionId, merchantId, expected }, 200,
        async token =>
        {
            var redirect = await mediator.Send(new StartRedirectCommand(paymentSessionId, expected), token);
            return new StartRedirectResponse(redirect.RedirectUrl);
        },
        _ => paymentSessionId.ToString("D"), ct);
    var updated = await adminSessions.ResolveAsync(
        paymentSessionId, adminScope.Accessible.IsUnrestricted, adminScope.Accessible.Merchants, ct)
        ?? throw new NotFoundException("Payment session was not found.");
    VersionEtags.Set(http, updated.Version);
    return Results.Ok(result.Value);
});
startRedirect.RequireAuthorization(ConsoleSessionAuthentication.PolicyName)
    .RequireAudiencePermission(Keys.TxnManage, Keys.PaymentRedirect).RequireAudienceCsrf()
    .WithMetadata(
        new AdminIfMatchMutationMarker("200"),
        new AdminIdempotencyMutationMarker(),
        requiredMerchantQuery)
    .WithTags("การชำระเงิน")
    .WithName("StartPaymentRedirect")
    .WithSummary("เริ่ม redirect ไปยัง PSP")
    .WithDescription("claim payment session แล้วสร้าง URL redirect ของ PSP Merchant Console ใช้สิทธิ์ payment.redirect; Admin Console ใช้ txn.manage พร้อม merchantId, If-Match และ Idempotency-Key หากไม่พบ -> 404, version หรือสถานะชนกัน -> 409")
    .Produces<StartRedirectResponse>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status409Conflict)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status403Forbidden)
    .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

// The merchant's read of one payment session (REQ-8.8). Merchant scoping is automatic (IMerchantScoped +
// the query filter), so another company's session is simply absent — and absent is 404, which is why the
// handler raises NotFoundException rather than the InvalidOperationException that used to answer 409.
var getPaymentSession = api.MapGet("/payments/sessions/{paymentSessionId:guid}", async (
    Guid paymentSessionId,
    HttpContext http,
    IActorScope actorScope,
    IAdminScope adminScope,
    IAdminPaymentSessionReader adminSessions,
    IMediator mediator,
    CancellationToken ct) =>
{
    if (!IsAdminCommerceRequest(http))
        return Results.Ok(await mediator.Send(new GetPaymentSessionQuery(paymentSessionId), ct));

    var resource = await adminSessions.ResolveAsync(
        paymentSessionId, adminScope.Accessible.IsUnrestricted, adminScope.Accessible.Merchants, ct)
        ?? throw new NotFoundException("Payment session was not found.");
    using var actorBinding = actorScope.Begin(resource.MerchantId);
    var view = await mediator.Send(new GetPaymentSessionQuery(paymentSessionId), ct);
    VersionEtags.Set(http, view.Version);
    return Results.Ok(AdminPaymentSession(view));
});
getPaymentSession.RequireAuthorization(ConsoleSessionAuthentication.PolicyName)
    .RequireAudiencePermission(Keys.TxnView, Keys.PaymentView)
    .WithMetadata(
        new AdminEtagResponseMarker("200"),
        new AudienceResponseMarker("200", typeof(PaymentSessionView), typeof(AdminPaymentSessionResponse)))
    .WithTags("การชำระเงิน")
    .WithName("GetPaymentSession")
    .WithSummary("อ่าน payment session")
    .WithDescription("Merchant Console อ่าน session ของร้านค้าที่ล็อกอิน; Admin Console อ่าน session ภายใน merchant scope และได้ ETag หากไม่พบหรือนอก scope -> 404")
    .Produces<PaymentSessionView>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

// Order summary link. The customer opens it anonymously — the opaque token IS the capability, resolved on
// a bypass proc (no merchant binding). Unknown token -> 404; expired -> 410. A merchant-user can resend (rotates
// the token + extends the TTL), which is merchant-scoped.
api.MapGet("/orders/{token}/summary", async (
    string token, HttpContext http, IOrderSummaryReader reader, IClock clock, CancellationToken ct) =>
{
    http.Response.Headers["Cache-Control"] = "no-store";
    http.Response.Headers["Referrer-Policy"] = "no-referrer";
    var summary = await reader.GetByTokenAsync(token, ct);
    if (summary is null)
        return Results.NotFound();
    if (clock.UtcNow >= summary.ExpiresAt)
        return Results.Problem(statusCode: StatusCodes.Status410Gone, title: "This link has expired.");

    // Deliberately projects neither MerchantId (the customer must not learn the merchant, REQ-8.4) nor a
    // payment-session id (REQ-8.9 — the payment state is asked for through payment-status).
    return Results.Ok(new OrderSummaryResponse(
        summary.OrderId, summary.OrderNo, summary.Amount, summary.Status,
        summary.Lines.Select(l => new OrderSummaryLineResponse(
            l.ProductCode, l.VariantCode, l.VariantName, l.Quantity, l.UnitPrice)).ToList()));
}).AllowAnonymous()
    .WithTags("คำสั่งซื้อ")
    .WithName("GetOrderSummary")
    .WithSummary("สรุปคำสั่งซื้อผ่านลิงก์")
    .WithDescription("capability link แบบสาธารณะ: opaque token จะ resolve สรุปคำสั่งซื้อพร้อม generic product/variant lines โดยไม่คืน metadata หากไม่พบ token -> 404, หมดอายุ -> 410")
    .Produces<OrderSummaryResponse>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status410Gone)
    .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

// The customer's pay button. Anonymous by design: the opaque summary token IS the capability, so there is
// no session cookie and no CSRF token to present (REQ-8.1) — which is exactly why the rate limit is per
// source IP and tight. Token lookup/lifecycle refusals below answer 404 or 409; authorization revalidation
// can answer 403 after resolving a valid Order. An invalid token is never told apart from a well-formed one
// that does not exist (REQ-8.2), and an expired one is 404 here rather than the summary read's 410, because a
// customer endpoint that distinguishes them hands out an oracle.
//
// The merchant comes from the ORDER, never from the request, and is bound before any merchant-scoped work
// exactly as the webhook does it. Resume (REQ-8.10) is not implemented here at all: create-session hands
// back the open session for the same channel and start-redirect hands back its existing hosted URL, so a
// double click or a second tab lands on the same charge instead of a second one.
api.MapPost("/orders/{token}/pay", async (
    string token,
    HttpContext http,
    IOrderSummaryReader reader,
    IActorScope actorScope,
    IMediator mediator,
    DefaultPspSelection defaultPsp,
    IClock clock,
    CancellationToken ct) =>
{
    http.Response.Headers["Cache-Control"] = "no-store";
    http.Response.Headers["Referrer-Policy"] = "no-referrer";
    var summary = await reader.GetByTokenAsync(token, ct);
    if (summary is null || clock.UtcNow >= summary.ExpiresAt)
        return Results.NotFound();

    // A cancelled order is gone as far as its link is concerned; a paid one is a state conflict the
    // customer can see the truth about on the summary itself (REQ-8.11).
    if (summary.Status == nameof(OrderStatus.Cancelled))
        return Results.NotFound();
    if (summary.Status == nameof(OrderStatus.Paid))
        return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "This order is already paid.");

    // Orders written before the checkout captured a channel have nothing to charge through. There is no
    // safe default — picking one would charge the customer down a channel nobody agreed to — so the way
    // out is the merchant cancelling and re-issuing the order.
    if (summary.PaymentChannel is not { } channel)
        return Results.Problem(
            statusCode: StatusCodes.Status409Conflict, title: "This order has no payment channel to charge through.");

    using var actorBinding = actorScope.Begin(summary.MerchantId);

    // The configured PSP is server-owned. Missing/disabled/unsupported connection state surfaces as 409.
    var session = await mediator.Send(
        new CreateSessionCommand(
            summary.OrderId,
            summary.MerchantId,
            PaymentMethods.FromOrderSnapshot(channel),
            defaultPsp.Psp), ct);
    var redirect = await mediator.Send(new StartRedirectCommand(session.PaymentSessionId), ct);

    return Results.Ok(new StartRedirectResponse(redirect.RedirectUrl));
}).AllowAnonymous().RequireRateLimiting(PaymentRateLimiting.PolicyName)
    .WithTags("คำสั่งซื้อ")
    .WithName("PayOrder")
    .WithSummary("ลูกค้าชำระเงินผ่านลิงก์")
    .WithDescription("เปิด payment session ตามช่องทางที่ร้านค้าเลือกไว้และ PSP ที่ backend config แล้วคืน URL redirect ใช้ opaque token เป็น capability ไม่ต้องมี session/CSRF เรียกซ้ำขณะ session เดิมยังเปิดอยู่จะได้ URL เดิม หาก Merchant User ผู้สร้างถูก revoke/suspend หรือไม่ได้รับอนุญาต method -> 403, ไม่พบ token/token หมดอายุ/คำสั่งซื้อถูกยกเลิก -> 404, คำสั่งซื้อชำระแล้ว/ไม่มีช่องทางชำระบันทึกไว้/Merchant, Provider หรือ Account capability ไม่พร้อม -> 409")
    .Produces<StartRedirectResponse>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status403Forbidden)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status409Conflict)
    .ProducesProblem(StatusCodes.Status429TooManyRequests)
    .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

// Where the customer lands back from 2C2P. POST, not GET, because it can SETTLE the payment: it runs the
// same fetch-to-confirm the webhook does, so a customer who returns before the webhook arrives still gets a
// true answer instead of a spinner. The response carries the status and nothing else — no session id, no
// merchant, no charge id (REQ-8.4).
api.MapPost("/orders/{token}/payment-status", async (
    string token,
    HttpContext http,
    IOrderSummaryReader reader,
    IActorScope actorScope,
    IMediator mediator,
    IClock clock,
    CancellationToken ct) =>
{
    http.Response.Headers["Cache-Control"] = "no-store";
    http.Response.Headers["Referrer-Policy"] = "no-referrer";
    var summary = await reader.GetByTokenAsync(token, ct);
    if (summary is null || clock.UtcNow >= summary.ExpiresAt)
        return Results.NotFound();

    using var actorBinding = actorScope.Begin(summary.MerchantId);

    var status = await mediator.Send(new ConfirmPaymentStatusCommand(summary.OrderId), ct);
    return Results.Ok(new PaymentStatusResponse(status.ToString().ToLowerInvariant()));
}).AllowAnonymous().RequireRateLimiting(PaymentRateLimiting.PolicyName)
    .WithTags("คำสั่งซื้อ")
    .WithName("GetOrderPaymentStatus")
    .WithSummary("สถานะการชำระเงินของลูกค้า")
    .WithDescription("ตรวจสถานะการชำระเงินของคำสั่งซื้อ: คำสั่งซื้อที่ชำระแล้ว/ยกเลิกแล้วตอบจากตัวคำสั่งซื้อเอง ส่วน session ที่ยังเปิดอยู่จะถูก verify กับ 2C2P ก่อน (เส้นเดียวกับ webhook) คืนค่า paid | failed | pending | cancelled หากไม่พบ token หรือ token หมดอายุ -> 404")
    .Produces<PaymentStatusResponse>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status429TooManyRequests)
    .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

api.MapPost("/orders/{orderId:guid}/summary/resend", async (
    Guid orderId, HttpContext http, IActorContext actor, IActorScope actorScope,
    IAdminScope adminScope, IAdminOrderReader adminOrders, IAdminOperationExecutor operations,
    IMediator mediator, CancellationToken ct) =>
{
    if (!IsAdminCommerceRequest(http))
        return Results.Ok(await mediator.Send(
            new ResendOrderSummaryCommand(orderId, actor.MerchantId), ct));

    var merchantId = RequireCommerceQueryGuid(http, "merchantId");
    var order = await RequireAdminOrderAsync(adminOrders, adminScope, orderId, merchantId, true, ct);
    var expected = VersionEtags.Require(http);
    using var actorBinding = actorScope.Begin(merchantId);
    var result = await ExecuteAdminCommerceAsync(
        operations, adminScope, merchantId, "order.summary.resend", IdempotencyKeys.Require(http),
        new { orderId, merchantId, expected }, 200,
        token => mediator.Send(new ResendOrderSummaryCommand(
            orderId, merchantId, expected), token).AsTask(),
        _ => order.OrderId.ToString("D"), ct);
    var updated = await RequireAdminOrderAsync(
        adminOrders, adminScope, orderId, merchantId, true, ct);
    VersionEtags.Set(http, updated.Version);
    return Results.Ok(result.Value);
}).RequireAuthorization(ConsoleSessionAuthentication.PolicyName)
    .RequireAudiencePermission(Keys.TxnManage, Keys.PaymentCreate).RequireAudienceCsrf()
    .WithMetadata(
        new AdminIfMatchMutationMarker("200"),
        new AdminIdempotencyMutationMarker(),
        requiredMerchantQuery)
    .WithTags("คำสั่งซื้อ")
    .WithName("ResendOrderSummary")
    .WithSummary("ส่งลิงก์สรุปคำสั่งซื้อซ้ำ")
    .WithDescription("หมุน token ของสรุปคำสั่งซื้อและต่ออายุ TTL แล้วคืนลิงก์ใหม่")
    .Produces<ResendOrderSummaryResult>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

// Cancel = the merchant's way out of an order the customer never paid for (REQ-4). Two units of work, and the
// ORDER of them is the whole safety property: the payment session must be proven dead first, because an order
// cancelled while a charge can still settle is money against an order nobody will honour. Release refuses
// (409) on a live session, a settled one, and an unreachable PSP alike, which is why the cancel below only
// runs on its success. Both halves are no-ops in their target state, so a retry finishes a half-done call.
// A session minted BETWEEN the two commands cannot slip through: the cancel re-checks for one inside its own
// transaction after taking the order row's lock, and a mint holds that same row locked while it verifies the
// order is still AwaitingPayment (REQ-3.6/4.7) — so one of the two always sees the other and answers 409.
api.MapPost("/orders/{orderId:guid}/cancel", async (
    Guid orderId, HttpContext http, IActorScope actorScope, IAdminScope adminScope,
    IAdminOrderReader adminOrders, IAdminOperationExecutor operations,
    IMediator mediator, CancellationToken ct) =>
{
    if (!IsAdminCommerceRequest(http))
    {
        await mediator.Send(new ReleaseOpenSessionCommand(orderId), ct);
        return Results.Ok(await mediator.Send(new CancelOrderCommand(orderId), ct));
    }

    var merchantId = RequireCommerceQueryGuid(http, "merchantId");
    var order = await RequireAdminOrderAsync(adminOrders, adminScope, orderId, merchantId, true, ct);
    var expected = VersionEtags.Require(http);
    using var actorBinding = actorScope.Begin(merchantId);
    var result = await ExecuteRecoverableAdminCommerceAsync(
        operations, adminScope, merchantId, "order.cancel", IdempotencyKeys.Require(http),
        new { orderId, merchantId, expected }, 200,
        async token =>
        {
            await mediator.Send(new ReleaseOpenSessionCommand(orderId), token);
            return await mediator.Send(new CancelOrderCommand(orderId, expected), token);
        },
        _ => order.OrderId.ToString("D"), ct);
    var updated = await RequireAdminOrderAsync(
        adminOrders, adminScope, orderId, merchantId, true, ct);
    VersionEtags.Set(http, updated.Version);
    return Results.Ok(result.Value);
}).RequireAuthorization(ConsoleSessionAuthentication.PolicyName)
    .RequireAudiencePermission(Keys.TxnManage, Keys.PaymentCreate).RequireAudienceCsrf()
    .WithMetadata(
        new AdminIfMatchMutationMarker("200"),
        new AdminIdempotencyMutationMarker(),
        requiredMerchantQuery)
    .WithTags("คำสั่งซื้อ")
    .WithName("CancelOrder")
    .WithSummary("ยกเลิกคำสั่งซื้อ")
    .WithDescription("ปล่อย payment session ที่ค้างอยู่ (หมดอายุ/ถูก PSP ปฏิเสธ) แล้วยกเลิกคำสั่งซื้อ เรียกซ้ำบนคำสั่งซื้อที่ยกเลิกแล้วตอบสำเร็จโดยไม่เปลี่ยนอะไร หากไม่พบคำสั่งซื้อ -> 404, คำสั่งซื้อชำระแล้ว/มี session ที่ยังจ่ายได้อยู่/ถาม PSP ไม่สำเร็จ -> 409")
    .Produces<CancelOrderResult>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status409Conflict)
    .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

// Direct Cart -> Order. Product availability checks happen before the transaction; coordinator then reloads
// the Cart and atomically writes Order + lines + notification outbox + CheckedOut state.
api.MapPost("/orders", async (
    CreateOrderFromCartRequest body,
    HttpContext http,
    IActorContext actor,
    IActorScope actorScope,
    IAdminScope adminScope,
    IAdminCartReader adminCarts,
    IAdminMerchantControlStore merchants,
    IAdminOperationExecutor operations,
    OrderCreationCoordinator coordinator,
    CancellationToken ct) =>
{
    var customer = CustomerContact.Of(body.Customer?.Name, body.Customer?.Phone, body.Customer?.Email);
    var paymentMethod = PaymentMethods.Normalize(body.PaymentMethod);
    if (!IsAdminCommerceRequest(http))
    {
        if (body.MerchantId is not null || body.OriginatorId is not null)
            throw new InvalidRequestException(
                "Merchant order creation forbids merchantId and originatorId.", "validation_failed");
        if (string.IsNullOrWhiteSpace(actor.SaleCode))
            throw new AccessDeniedException("No sale code is bound to this merchant user.", "sale-code-missing");
        var merchantUserId = actor.UserId
            ?? throw new AccessDeniedException("No Merchant User identity is bound.", "merchant-user-unbound");
        var merchantResult = await coordinator.CreateAsync(
            actor.MerchantId, body.CartId, actor.SaleCode, customer, paymentMethod,
            OrderInitiatingAudience.User, merchantUserId, ct);
        return Results.Created($"/api/v1/orders/{merchantResult.OrderId}", merchantResult);
    }

    if (body.MerchantId is not { } merchantId || merchantId == Guid.Empty
        || body.OriginatorId is not { } originatorId || originatorId == Guid.Empty)
        throw new InvalidRequestException(
            "Admin order creation requires merchantId and originatorId.", "validation_failed");
    var originator = await RequireCommerceOriginatorAsync(
        merchants, adminScope, merchantId, originatorId, ct);
    var cart = await RequireAdminCartAsync(adminCarts, adminScope, body.CartId, merchantId, true, ct);
    if (cart.OriginatorId != originatorId)
        throw new ConflictException("Cart originator does not match the request.", "state_conflict");
    using var actorBinding = actorScope.Begin(merchantId);
    var prepared = await coordinator.PrepareAsync(
        merchantId, body.CartId, originator.SaleCode!, customer, paymentMethod,
        OrderInitiatingAudience.PlatformAdmin, null, ct, originatorId);
    var result = await ExecuteAdminCommerceAsync(
        operations, adminScope, merchantId, "order.create", IdempotencyKeys.Require(http),
        new { body.CartId, merchantId, originatorId, body.Customer, paymentMethod }, 201,
        token => coordinator.CommitAsync(prepared, token),
        value => value.OrderId.ToString("D"), ct);
    return Results.Created($"/api/v1/orders/{result.Value.OrderId}", result.Value);
}).RequireAuthorization(ConsoleSessionAuthentication.PolicyName)
    .RequireAudiencePermission(Keys.TxnManage, Keys.PaymentCreate).RequireAudienceCsrf()
    .WithMetadata(new AdminIdempotencyMutationMarker())
    .WithTags("คำสั่งซื้อ")
    .WithName("CreateOrderFromCart")
    .WithSummary("สร้างคำสั่งซื้อจากตะกร้า")
    .WithDescription("สร้าง Order สถานะ Pending จาก Cart ที่ยังเปิด โดยตรวจสินค้าและสถานะขายกับต้นทางอีกครั้ง ราคาและ metadata มาจาก server เท่านั้น แล้ว commit Order, items, notification และ Cart state ใน transaction เดียว")
    .Produces<DirectOrderResult>(StatusCodes.Status201Created)
    .ProducesProblem(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status403Forbidden)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status409Conflict)
    .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

// Merchant-authenticated paged order list. Generic lines omit metadata; detail is audited before metadata reveal.
// SFS allowlists orderNo (eq/contains), status (eq/in), paymentChannel (eq/in), and sort
// createdAt/orderNo. Unknown fields/operators are dropped by the shared SFS contract.
api.MapGet("/orders", async (
    HttpContext http, IActorContext actor, IAdminScope adminScope,
    IAdminOrderReader adminOrders, IMediator mediator, CancellationToken ct) =>
{
    var parsed = SfsQueryParser.Parse(http.Request.Query, maxLimit: 100);
    if (!IsAdminCommerceRequest(http))
    {
        var merchantResult = await mediator.Send(new GetOrdersQuery(actor.MerchantId)
        {
            Page = parsed.Page,
            Limit = parsed.Limit,
            Filters = parsed.Filters,
            Sort = parsed.Sort,
            Search = parsed.Search,
        }, ct);
        return Results.Ok(merchantResult);
    }

    Guid? merchantId = null;
    if (http.Request.Query.TryGetValue("merchantId", out var rawMerchant)
        && !string.IsNullOrWhiteSpace(rawMerchant))
    {
        if (!Guid.TryParse(rawMerchant, out var parsedMerchant) || parsedMerchant == Guid.Empty)
            throw new InvalidRequestException("merchantId must be a non-empty UUID.", "invalid_filter");
        merchantId = parsedMerchant;
    }
    var result = await adminOrders.ListAsync(new AdminOrderQuery(
        merchantId, CommerceOrderAccess(adminScope))
    {
        Page = parsed.Page,
        Limit = parsed.Limit,
        Filters = parsed.Filters,
        Sort = parsed.Sort,
        Search = parsed.Search,
    }, ct);
    return Results.Ok(new PagedResult<AdminOrderListResponse>(
        result.Items.Select(AdminOrderList).ToArray(), result.Page, result.Limit, result.Total));
}).RequireAuthorization(ConsoleSessionAuthentication.PolicyName)
    .RequireAudiencePermission(Keys.TxnView, Keys.PaymentView)
    .WithMetadata(
        new SfsQueryParamsMarker(100),
        optionalMerchantQuery,
        new AudienceResponseMarker("200", typeof(PagedResult<OrderListItem>),
            typeof(PagedResult<AdminOrderListResponse>)))
    .WithTags("คำสั่งซื้อ")
    .WithName("ListOrders")
    .WithSummary("รายการคำสั่งซื้อ")
    .WithDescription("Merchant Console เห็นเฉพาะร้านค้าที่ผูกกับ session; Admin Console เห็นร้านค้าใน scope และกรอง merchantId ได้ คืนรายการแบบแบ่งหน้าโดยไม่คืน metadata รองรับ sort createdAt/orderNo และ filter orderNo, status, paymentChannel")
    .Produces<PagedResult<OrderListItem>>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

api.MapGet("/orders/export", async (
    HttpContext http,
    IAdminScope adminScope,
    IAdminOrderReader adminOrders,
    CancellationToken ct) =>
{
    var window = RequireExportWindow(http);
    var parsed = SfsQueryParser.Parse(http.Request.Query, maxLimit: 100);
    Guid? merchantId = null;
    if (http.Request.Query.TryGetValue("merchantId", out var rawMerchant)
        && !string.IsNullOrWhiteSpace(rawMerchant))
    {
        if (!Guid.TryParse(rawMerchant, out var parsedMerchant) || parsedMerchant == Guid.Empty)
            throw new InvalidRequestException("merchantId must be a non-empty UUID.", "invalid_filter");
        merchantId = parsedMerchant;
    }
    var filters = parsed.Filters.Concat(
    [
        new FilterOption("createdAt", FilterOperator.GreaterThanOrEqual,
            JsonSerializer.SerializeToElement(window.From.ToString("O"))),
        new FilterOption("createdAt", FilterOperator.LessThanOrEqual,
            JsonSerializer.SerializeToElement(window.To.ToString("O"))),
    ]).ToArray();
    var result = await adminOrders.ListAsync(new AdminOrderQuery(
        merchantId, CommerceOrderAccess(adminScope))
    {
        Page = 1,
        Limit = 10_001,
        Filters = filters,
        Sort = parsed.Sort,
        Search = parsed.Search,
    }, ct);
    if (result.Total > 10_000)
        return Results.Problem(
            statusCode: StatusCodes.Status422UnprocessableEntity,
            title: "Export contains too many rows.",
            extensions: new Dictionary<string, object?> { ["code"] = "export_too_large" });

    var csv = new StringBuilder();
    csv.AppendLine("orderId,orderNo,merchantId,originatorId,amount,currency,status,itemCount,createdAt,updatedAt");
    foreach (var item in result.Items)
    {
        csv.AppendLine(string.Join(',', new[]
        {
            CsvCell(item.OrderId.ToString("D")), CsvCell(item.OrderNo),
            CsvCell(item.MerchantId.ToString("D")), CsvCell(item.OriginatorId?.ToString("D")),
            CsvCell(FixedMoney(item.Amount.Amount)), CsvCell(item.Amount.Currency), CsvCell(item.Status),
            CsvCell(item.Lines.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            CsvCell(item.CreatedAt.ToUniversalTime().ToString("O")),
            CsvCell(item.UpdatedAt.ToUniversalTime().ToString("O")),
        }));
    }
    return Results.File(Encoding.UTF8.GetBytes(csv.ToString()), "text/csv; charset=utf-8",
        $"orders-{window.From:yyyyMMdd}-{window.To:yyyyMMdd}.csv");
}).RequireAuthorization("admin").RequirePermission(Keys.TxnExport)
    .WithMetadata(
        new SfsQueryParamsMarker(100),
        requiredExportFromQuery,
        requiredExportToQuery,
        optionalMerchantQuery)
    .WithTags("คำสั่งซื้อ")
    .WithName("ExportOrders")
    .WithSummary("ส่งออกรายการคำสั่งซื้อ")
    .WithDescription("ต้องระบุ from/to ช่วงไม่เกิน 31 วัน ใช้ filters/sort/search ชุดเดียวกับรายการ และส่งออกได้สูงสุด 10,000 แถว")
    .Produces(StatusCodes.Status200OK, contentType: "text/csv")
    .ProducesProblem(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status403Forbidden)
    .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

// Merchant-authenticated detail: one RevealAudit row per metadata-bearing line, fail-closed before response.
api.MapGet("/orders/{orderId:guid}", async (
    Guid orderId,
    HttpContext http,
    IActorContext actor,
    IActorScope actorScope,
    IAdminScope adminScope,
    IAdminOrderReader adminOrders,
    IClock clock,
    IMediator mediator,
    CancellationToken ct) =>
{
    if (!IsAdminCommerceRequest(http))
    {
        var merchantResult = await mediator.Send(
            new GetOrderDetailCommand(actor.MerchantId, orderId, "merchant-user", actor.UserId!.Value.ToString()), ct);
        return Results.Ok(merchantResult);
    }

    var resource = await RequireAdminOrderAsync(
        adminOrders, adminScope, orderId, expectedMerchantId: null, mutation: false, ct);
    using var actorBinding = actorScope.Begin(resource.MerchantId);
    var result = await mediator.Send(new GetOrderDetailCommand(
        resource.MerchantId, orderId, "admin", adminScope.Current.AdminId.ToString("D")), ct);
    PaymentSessionView? session = null;
    if (result.PaymentSessionId is { } sessionId)
        session = await mediator.Send(new GetPaymentSessionQuery(sessionId), ct);
    VersionEtags.Set(http, result.Version);
    var lifecycle = new List<CommerceLifecycleResponse>
    {
        new("created", result.CreatedAt),
    };
    if (result.UpdatedAt != result.CreatedAt)
        lifecycle.Add(new(result.Status.ToLowerInvariant(), result.UpdatedAt));
    return Results.Ok(new AdminOrderDetailResponse(
        result.OrderId, result.OrderNo, result.MerchantId, result.OriginatorId,
        AdminMoney(result.Amount), result.Status, result.Lines.Count, result.PaymentChannel,
        result.PaymentSessionId, result.CreatedAt, result.UpdatedAt,
        result.Lines.Select(line => new AdminOrderLineResponse(
            line.ProductCode, line.VariantCode, line.VariantName, line.Quantity,
            AdminMoney(line.UnitPrice), AdminMoney(line.Discount), line.Metadata)).ToArray(),
        MaskCustomerReference(result.CustomerEmail, result.CustomerPhone),
        clock.UtcNow >= result.SummaryTokenExpiresAt ? "expired" : "active",
        session is null ? null : AdminPaymentSession(session), lifecycle,
        OrderCapabilities(result.Status), result.Version));
}).RequireAuthorization(ConsoleSessionAuthentication.PolicyName)
    .RequireAudiencePermission(Keys.TxnView, Keys.PaymentView)
    .WithMetadata(
        new AdminEtagResponseMarker("200"),
        new AudienceResponseMarker("200", typeof(OrderDetailView), typeof(AdminOrderDetailResponse)))
    .WithTags("คำสั่งซื้อ")
    .WithName("GetOrderDetail")
    .WithSummary("อ่านคำสั่งซื้อแบบเต็มพร้อม audit trail")
    .WithDescription("คืน generic product/variant line พร้อม server-owned metadata และเขียน reveal-audit หนึ่งแถวต่อ line ก่อนตอบ ถ้า audit ไม่สำเร็จจะ fail closed")
    .Produces<OrderDetailView>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

// Reconciliation report: the bound merchant's orders grouped by status + currency (count + total).
api.MapGet("/reports/reconciliation", async (
    HttpContext http,
    IActorContext actor,
    IAdminScope adminScope,
    IAdminOrderReader adminOrders,
    IMediator mediator,
    CancellationToken ct) =>
{
    if (!IsAdminCommerceRequest(http))
    {
        var merchantView = await mediator.Send(new GetReconciliationSummaryQuery(actor.MerchantId), ct);
        return Results.Ok(merchantView);
    }

    Guid? merchantId = null;
    if (http.Request.Query.TryGetValue("merchantId", out var rawMerchant)
        && !string.IsNullOrWhiteSpace(rawMerchant))
    {
        if (!Guid.TryParse(rawMerchant, out var parsedMerchant) || parsedMerchant == Guid.Empty)
            throw new InvalidRequestException("merchantId must be a non-empty UUID.", "invalid_filter");
        merchantId = parsedMerchant;
    }
    var totals = await adminOrders.ReconciliationAsync(
        CommerceOrderAccess(adminScope), merchantId, ct);
    return Results.Ok(new ReconciliationView(totals.Select(x => new ReconciliationLine(
        x.Status.ToString(), x.Currency, x.Count, x.Total)).ToArray()));
}).RequireAuthorization(ConsoleSessionAuthentication.PolicyName)
    .RequireAudiencePermission(Keys.TxnView, Keys.PaymentView)
    .WithMetadata(optionalMerchantQuery)
    .WithTags("คำสั่งซื้อ")
    .WithName("GetReconciliationReport")
    .WithSummary("รายงาน reconciliation")
    .WithDescription("จัดกลุ่มคำสั่งซื้อตามสถานะและสกุลเงิน พร้อมจำนวนและยอดรวม Merchant Console เห็นร้านค้าที่ผูกกับ session; Admin Console เห็นร้านค้าใน scope และกรอง merchantId ได้")
    .Produces<ReconciliationView>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

// --- Admin BFF (/api/v1/admins route group, REQ-1/7/10) ---
// One group binds the CSRF double-submit filter ONCE for the whole admin surface (the credentialed admin CORS
// policy is applied to /api/v1/admins/* by PolCorsPolicyProvider). Per-endpoint authorization stays explicit: login
// is anonymous; every other route gates on the Session "admin" policy. The CSRF filter exempts safe methods,
// so the login/callback GETs pass untouched.
var admin = api.MapGroup("/admins").RequireCsrf();

// Top-level browser navigation (AllowAnonymous, rate-limited): validate the post-login returnTo against the
// allowlist, then hand off to the Microsoft OIDC handler, which builds the Authorization Code + PKCE + state
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
        OidcAuthentication.CreateLoginProperties(returnTo, provider),
        [scheme]);
})
.AllowAnonymous()
.RequireRateLimiting(AuthRateLimiting.PolicyName)
    .WithTags("การเข้าสู่ระบบ")
    .WithName("AdminLogin")
    .WithSummary("เริ่มเข้าสู่ระบบผู้ดูแลระบบ")
    .WithDescription("ตรวจสอบ returnTo กับ allowlist แล้ว redirect ไปยัง Microsoft workforce OIDC (Authorization Code + PKCE) callback จะเป็นตัวสร้าง session cookie หาก provider ไม่รู้จักหรือยังไม่ได้ตั้งค่า -> 404")
    .Produces(StatusCodes.Status302Found)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status429TooManyRequests);

// Logout = revoke the CURRENT session family (this device only); other devices stay signed in (REQ-6.1). The
// presented cookie identifies the family. CSRF protection remains mandatory for this authenticated mutation.
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
            audit.Append(AuthAudit.For(AuthEventType.Logout, http.TraceIdentifier, clock.UtcNow, session.AdminUserId));
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
    // Merchant metadata binds through an explicit allowlist contract; non-secret PSP config still rides
    // alongside "psp"/"secrets" and is captured via JsonExtensionData (reference 2.4).
    var t = body.Merchant ?? throw new ArgumentException("The 'merchant' object is required.");

    // The caller's CURRENT AuthorizationVersion, read fresh right before dispatch (task 8.5.4) — this IS the
    // "pinned at the request boundary" snapshot the provisioning UoW re-verifies in-transaction under lock.
    var caller = await adminUsers.GetByIdAsync(adminScope.Current.AdminId, ct)
        ?? throw new InvalidOperationException("The authenticated admin no longer exists.");

    var command = new ProvisionMerchantCommand(
        new MerchantSpec(t.Code, t.Name, t.Note, t.Country, t.Currency,
            t.EnabledChannels ?? [], new MerchantMetadata(t.Branding, t.Routing, t.Session, t.Timezone, t.Locale)),
        [.. (body.PspConnections ?? []).Select(p =>
        {
            // A secret-looking field captured as readable config (a typo putting it beside, not inside,
            // "secrets") would persist + echo plaintext outside the vault -> reject it (400).
            ProvisioningGuards.RejectSecretsInConfig(p.Config);
            return new PspConnectionSpec(
                p.Psp, p.EnabledMethods ?? [], p.MerchantId,
                p.Secrets ?? new Dictionary<string, string>(), ToElement(p.Config));
        })],
        $"admin:{adminScope.Current.AdminId:D}",
        http.TraceIdentifier,
        adminScope.Current.AdminId,
        caller.AuthorizationVersion);

    var result = await mediator.Send(command, ct);
    return Results.Created($"/api/v1/merchants/{t.Code}", result);

    // Re-pack allowlisted PSP config fields into one JSON element for storage.
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
    .ProducesProblem(StatusCodes.Status403Forbidden)
    .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

// Cross-merchant read routed through the IAdminQuery seam: a Scoped admin sees only its assigned merchants, a
// Super is unrestricted (REQ-8.5 / 7.1). Out-of-scope or unknown -> 404 (no existence leak). {code} stays
// unconstrained (REQ-6.5) — adding a route constraint here would itself be a behavior change.
api.MapGet("/merchants/{code}", async (
    string code,
    HttpContext http,
    IAdminQuery adminQuery,
    CancellationToken ct) =>
{
    var view = await adminQuery.GetMerchantByCodeAsync(code, ct);
    if (view is not null)
        VersionEtags.Set(http, view.Version);
    return view is null
        ? Results.Problem(statusCode: StatusCodes.Status404NotFound)
        : Results.Ok(view);
}).RequireCsrf().RequireAuthorization("admin").RequirePermission(Keys.MerchantView) // GET is CSRF-exempt by design; attached for REQ-7.1
    .WithMetadata(new EtagResponseMarker("200"))
    .WithTags("ร้านค้า (ผู้ดูแลระบบ)")
    .WithName("GetMerchant")
    .WithSummary("อ่านข้อมูลร้านค้าตามรหัส")
    .WithDescription("admin แบบ Scoped เห็นเฉพาะร้านค้าที่ถูก assign ให้; Super เห็นได้ไม่จำกัด นอก scope หรือไม่พบ -> 404 (ไม่รั่วว่ามีอยู่จริงหรือไม่)")
    .Produces<MerchantView>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

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
    .WithDescription("ตรวจสอบ returnTo กับ allowlist แล้ว redirect ไปยัง provider (microsoft; OIDC Authorization Code + PKCE) callback จะสร้าง session cookie ให้ merchant-user ที่ Active หรือ redirect ผู้สมัครไป /register พร้อม ticket ที่เซ็นแล้ว หาก provider ไม่รู้จักหรือยังไม่ได้ตั้งค่า -> 404")
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
    IActorScope actorScope,
    HttpContext http,
    CancellationToken ct) =>
{
    var opts = registrationOptions.Value;

    // Bound the body BEFORE reading the multipart so an oversized upload is aborted mid-read, never buffered whole
    // then measured (DoS guard, N3). Photo cap + headroom for the text fields.
    var sizeFeature = http.Features.Get<IHttpMaxRequestBodySizeFeature>();
    if (sizeFeature is { IsReadOnly: false })
        sizeFeature.MaxRequestBodySize = (2 * opts.PhotoMaxBytes) + 64 * 1024;

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
            title: "The registration ticket is missing, invalid, or expired.",
            extensions: new Dictionary<string, object?> { ["code"] = "registration-link-invalid" });

    // Required photo: validate type + magic bytes + size BEFORE it is stored; store nothing on reject.
    byte[]? photoBytes = null;
    string? photoContentType = null;
    var file = form.Files["photo"];
    if (file is not { Length: > 0 })
        return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "photo is required.");
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

    byte[]? kycPhotoBytes = null;
    string? kycPhotoContentType = null;
    var kycFile = form.Files["kycPhoto"];
    if (kycFile is { Length: > 0 })
    {
        if (kycFile.Length > opts.PhotoMaxBytes)
            return Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge,
                title: "The KYC photo exceeds the size limit.");
        var buffer = new byte[kycFile.Length];
        await using (var stream = kycFile.OpenReadStream())
            await stream.ReadExactlyAsync(buffer, ct);
        var validation = PhotoValidation.Validate(
            kycFile.ContentType, buffer.AsSpan(0, Math.Min(16, buffer.Length)), buffer.Length, opts.PhotoMaxBytes);
        if (!validation.IsValid)
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: validation.Error);
        kycPhotoBytes = buffer;
        kycPhotoContentType = validation.ContentType;
    }

    var formModel = UserRegistrationForm.From(form);
    if (string.IsNullOrWhiteSpace(formModel.FirstName) || string.IsNullOrWhiteSpace(formModel.LastName)
        || string.IsNullOrWhiteSpace(formModel.IdentityNumber)
        || string.IsNullOrWhiteSpace(formModel.SaleCode)
        || string.IsNullOrWhiteSpace(formModel.Phone))
        return Results.Problem(statusCode: StatusCodes.Status400BadRequest,
            title: "firstName, lastName, idNumber, producerCode, phone, and photo are required.");

    IDisposable? invitationBinding = null;
    if (ticket.InvitationId is { } invitationId)
    {
        var invitation = await mediator.Send(new ResolveInvitationByIdQuery(invitationId), ct);
        if (invitation is null || !string.Equals(invitation.NormalizedEmail,
                MerchantUserInvitation.NormalizeEmail(ticket.Email), StringComparison.Ordinal))
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest,
                title: "Invitation is invalid or expired.",
                extensions: new Dictionary<string, object?> { ["code"] = "invitation-invalid" });
        invitationBinding = actorScope.Begin(invitation.MerchantId);
    }

    SubmitRegistrationResult result;
    try
    {
        result = await mediator.Send(new SubmitRegistrationCommand(
            ticket.Subject, ticket.Email, ticket.HostedDomain, ticket.Purpose,
            formModel, photoBytes, photoContentType, http.TraceIdentifier, ticket.Provider,
            kycPhotoBytes, kycPhotoContentType, ticket.OperationId, ticket.InvitationId), ct);
    }
    finally
    {
        invitationBinding?.Dispose();
    }

    return Results.Created($"/api/v1/merchants/users/{result.UserId}",
        new UserRegisterResponse(result.UserId, result.Status.ToString()));
})
    .AllowAnonymous()
    .DisableAntiforgery()
    .RequireRateLimiting(UserAuthRateLimiting.PolicyName)
    .WithTags("การเข้าสู่ระบบ (ผู้ใช้ร้านค้า)")
    .WithName("MerchantUserRegister")
    .WithSummary("ส่งคำขอลงทะเบียนผู้ใช้ร้านค้า")
    .WithDescription("ส่งข้อมูลแบบ multipart โดยไม่ต้องยืนยันตัวตน แต่ต้องมี ticket, firstName, lastName, personType, idNumber, producerCode, phone และ photo; kycPhoto ไม่บังคับ สร้าง MerchantUser สถานะ PendingApproval แล้ว enqueue registration event หาก ticket ไม่ถูกต้อง/หมดอายุ -> 400 registration-link-invalid; ส่งซ้ำ/replay -> 409; ไฟล์ใหญ่เกินไป -> 413")
    .Accepts<UserRegistrationMultipartRequest>("multipart/form-data")
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
            audit.Append(MerchantAuthAudit.For(MerchantAuthEventType.Logout, http.TraceIdentifier, clock.UtcNow, session.UserId));
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
    var userId = scope.Current.UserId;
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
    var roleCodes = await roles.ListActiveRoleCodesForUserAsync(me.UserId, me.MerchantId, ct);
    return Results.Ok(new MerchantUserMeResponse(me.UserId, me.Email, me.MerchantId, roleCodes, me.Permissions));
}).RequireAuthorization("merchant-user")
    .WithTags("การเข้าสู่ระบบ (ผู้ใช้ร้านค้า)")
    .WithName("GetMerchantUserMe")
    .WithSummary("อ่านข้อมูลผู้ใช้ร้านค้าปัจจุบัน")
    .WithDescription("คืน merchantUserId, email, merchantId, active role codes และ effective permissions จาก session ปัจจุบัน บัญชีที่ยังไม่ผูกกับร้านค้าหรือไม่ Active ใช้งาน endpoint นี้ไม่ได้")
    .Produces<MerchantUserMeResponse>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status403Forbidden)
    .ProducesProblem(StatusCodes.Status401Unauthorized);

merchantUsers.MapGet("", async (
    HttpContext http, IUserScope scope, IAdminScope adminScope, IMediator mediator, CancellationToken ct) =>
{
    var parsed = SfsQueryParser.Parse(http.Request.Query, maxLimit: 100);
    var adminRead = http.Features.Get<SelectedConsoleAudience>()?.Value == ConsoleAudience.Admin;
    var merchantId = adminRead ? Guid.Empty : scope.Current.MerchantId;
    if (adminRead && http.Request.Query.TryGetValue("merchantId", out var selectedMerchant)
        && !string.IsNullOrWhiteSpace(selectedMerchant))
    {
        if (!Guid.TryParse(selectedMerchant, out merchantId))
            throw new InvalidRequestException("merchantId must be a UUID.", "invalid_filter");
        if (!adminScope.Accessible.Allows(merchantId))
            return Results.NotFound();
    }
    var result = await mediator.Send(new ListMerchantUsersQuery(
        merchantId, adminRead, adminRead && adminScope.Accessible.IsUnrestricted,
        adminRead ? adminScope.Accessible.Merchants : null)
    {
        Page = parsed.Page,
        Limit = parsed.Limit,
        Filters = parsed.Filters,
        Sort = parsed.Sort,
        Search = parsed.Search,
    }, ct);
    return Results.Ok(result);
}).RequireAuthorization(ConsoleSessionAuthentication.PolicyName)
    .RequireAudiencePermission(Keys.MerchantUserView, Keys.UsersView)
    .WithMetadata(new SfsQueryParamsMarker(100))
    .WithTags("ผู้ใช้ร้านค้า")
    .WithName("ListMerchantUsers")
    .WithSummary("รายการผู้ใช้ร้านค้า")
    .WithDescription("คืนรายการแบบแบ่งหน้า รองรับ SFS Merchant Console เห็นเฉพาะร้านค้าจาก session; Admin Console เห็นร้านค้าใน scope และเลือก merchantId ได้")
    .Produces<PagedResult<MerchantUserListItem>>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status403Forbidden);

merchantUsers.MapGet("/{merchantUserId:guid}", async (
    Guid merchantUserId, HttpContext http, IUserScope scope, IAdminScope adminScope,
    IMediator mediator, CancellationToken ct) =>
{
    var adminRead = http.Features.Get<SelectedConsoleAudience>()?.Value == ConsoleAudience.Admin;
    var result = await mediator.Send(new GetMerchantUserQuery(
        merchantUserId, adminRead ? Guid.Empty : scope.Current.MerchantId,
        adminRead, adminRead && adminScope.Accessible.IsUnrestricted,
        adminRead ? adminScope.Accessible.Merchants : null), ct);
    if (result is null)
        return Results.NotFound();
    VersionEtags.Set(http, result.Version);
    return Results.Ok(result);
}).RequireAuthorization(ConsoleSessionAuthentication.PolicyName)
    .RequireAudiencePermission(Keys.MerchantUserView, Keys.UsersView)
    .WithMetadata(new EtagResponseMarker("200"))
    .WithTags("ผู้ใช้ร้านค้า")
    .WithName("GetMerchantUser")
    .WithSummary("อ่านผู้ใช้ร้านค้า")
    .WithDescription("คืน profile ที่ mask ข้อมูลอ่อนไหว พร้อม role codes, effective permissions และ ETag หากไม่พบหรือนอก merchant scope -> 404")
    .Produces<MerchantUserDetail>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status403Forbidden);

merchantUsers.MapGet("/{merchantUserId:guid}/edit", async (
    Guid merchantUserId, IUserScope scope, HttpContext http, IMediator mediator, CancellationToken ct) =>
{
    var me = scope.Current;
    var result = await mediator.Send(new GetMerchantUserEditQuery(
        merchantUserId, me.MerchantId, me.UserId, http.TraceIdentifier), ct);
    return result is null ? Results.NotFound() : Results.Ok(result);
}).RequireAuthorization("merchant-user").RequirePermission(Keys.UsersManage)
    .WithTags("ผู้ใช้ร้านค้า")
    .WithName("GetMerchantUserEdit")
    .WithSummary("อ่านข้อมูลผู้ใช้ร้านค้าสำหรับแก้ไข")
    .WithDescription("คืนเฉพาะ firstName, lastName, producerCode, licenseNumber และ phone ของผู้ใช้ในร้านค้าเดียวกัน หากไม่พบ -> 404")
    .Produces<MerchantUserEditView>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status403Forbidden);

merchantUsers.MapPost("/invitations", async (
    CreateMerchantUserInvitationRequest body, IUserScope scope, HttpContext http,
    IOptions<UserInvitationOptions> options, IMediator mediator, CancellationToken ct) =>
{
    var me = scope.Current;
    var result = await mediator.Send(new CreateMerchantUserInvitationCommand(
        body.Email, me.MerchantId, me.UserId, http.TraceIdentifier, options.Value.TtlHours), ct);
    return Results.Created($"/api/v1/merchants/users/invitations/{result.InvitationId}", result);
}).RequireAuthorization("merchant-user").RequirePermission(Keys.UsersManage)
    .WithTags("ผู้ใช้ร้านค้า")
    .WithName("CreateMerchantUserInvitation")
    .WithSummary("เชิญผู้ใช้เข้าร้านค้า")
    .WithDescription("สร้าง invitation แบบ tenant-bound สำหรับอีเมลที่ระบุ และ enqueue การส่งลิงก์ลงทะเบียน ไม่คืน raw token หากอีเมลมี invitation ที่ยังใช้ได้ -> 409")
    .Produces<CreateMerchantUserInvitationResult>(StatusCodes.Status201Created)
    .ProducesProblem(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status409Conflict)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status403Forbidden);

merchantUsers.MapDelete("/invitations/{invitationId:guid}", async (
    Guid invitationId, IUserScope scope, HttpContext http, IMediator mediator, CancellationToken ct) =>
{
    var me = scope.Current;
    await mediator.Send(new RevokeMerchantUserInvitationCommand(
        invitationId, me.MerchantId, me.UserId, http.TraceIdentifier), ct);
    return Results.NoContent();
}).RequireAuthorization("merchant-user").RequirePermission(Keys.UsersManage)
    .WithTags("ผู้ใช้ร้านค้า")
    .WithName("RevokeMerchantUserInvitation")
    .WithSummary("เพิกถอนคำเชิญผู้ใช้ร้านค้า")
    .WithDescription("เพิกถอน invitation ที่ยังไม่ถูกใช้ของร้านค้าปัจจุบัน หากไม่พบ -> 404, ใช้แล้วหรือหมดอายุ -> 409")
    .Produces(StatusCodes.Status204NoContent)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status409Conflict)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status403Forbidden);

merchantUsers.MapPut("/{merchantUserId:guid}", async (
    Guid merchantUserId, UpdateMerchantUserRequest body, IUserScope scope, HttpContext http,
    IMediator mediator, CancellationToken ct) =>
{
    var me = scope.Current;
    await mediator.Send(new UpdateMerchantUserCommand(merchantUserId, me.MerchantId, me.UserId,
        body.FirstName, body.LastName, body.ProducerCode, body.LicenseNumber, body.Phone,
        http.TraceIdentifier), ct);
    return Results.NoContent();
}).RequireAuthorization("merchant-user").RequirePermission(Keys.UsersManage)
    .WithTags("ผู้ใช้ร้านค้า")
    .WithName("UpdateMerchantUser")
    .WithSummary("แก้ไขข้อมูลผู้ใช้ร้านค้า")
    .WithDescription("แก้เฉพาะ firstName, lastName, producerCode, licenseNumber และ phone ของผู้ใช้ในร้านค้าเดียวกัน หากไม่พบ -> 404, สถานะไม่อนุญาต -> 409")
    .Produces(StatusCodes.Status204NoContent)
    .ProducesProblem(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status409Conflict)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status403Forbidden);

static RouteHandlerBuilder MapMerchantUserLifecycle(
    RouteGroupBuilder group, string route, string operationName, MerchantUserLifecycleAction action) =>
    group.MapPost(route, async (Guid merchantUserId, IUserScope scope, HttpContext http,
        IMediator mediator, CancellationToken ct) =>
    {
        var me = scope.Current;
        await mediator.Send(new ChangeMerchantUserLifecycleCommand(
            merchantUserId, me.MerchantId, me.UserId, action, http.TraceIdentifier), ct);
        return Results.NoContent();
    }).RequireAuthorization("merchant-user").RequirePermission(Keys.UsersManage)
      .WithTags("ผู้ใช้ร้านค้า").WithName(operationName)
      .WithSummary(action switch
      {
          MerchantUserLifecycleAction.Approve => "อนุมัติผู้ใช้ร้านค้า",
          MerchantUserLifecycleAction.Reject => "ปฏิเสธผู้ใช้ร้านค้า",
          MerchantUserLifecycleAction.Suspend => "ระงับผู้ใช้ร้านค้า",
          _ => "เปิดใช้งานผู้ใช้ร้านค้าอีกครั้ง",
      })
      .WithDescription("เปลี่ยน lifecycle ของผู้ใช้ภายในร้านค้าเดียวกันและเพิกถอน session เมื่อสถานะกำหนด หากไม่พบ -> 404, transition ใช้ไม่ได้ -> 409")
      .Produces(StatusCodes.Status204NoContent)
      .ProducesProblem(StatusCodes.Status404NotFound)
      .ProducesProblem(StatusCodes.Status409Conflict)
      .ProducesProblem(StatusCodes.Status401Unauthorized)
      .ProducesProblem(StatusCodes.Status403Forbidden);

MapMerchantUserLifecycle(merchantUsers, "/{merchantUserId:guid}/approve", "ApproveMerchantUserByManager", MerchantUserLifecycleAction.Approve);
MapMerchantUserLifecycle(merchantUsers, "/{merchantUserId:guid}/reject", "RejectMerchantUserByManager", MerchantUserLifecycleAction.Reject);
MapMerchantUserLifecycle(merchantUsers, "/{merchantUserId:guid}/suspend", "SuspendMerchantUser", MerchantUserLifecycleAction.Suspend);
MapMerchantUserLifecycle(merchantUsers, "/{merchantUserId:guid}/reactivate", "ReactivateMerchantUser", MerchantUserLifecycleAction.Reactivate);

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
        catalog.Groups.Select(g => new MerchantUserPermissionGroupResponse(g.Key, g.Name)).ToArray(),
        catalog.Permissions.Select(p => new MerchantUserPermissionItemResponse(p.Key, p.Name, p.Resource)).ToArray()));
}).RequireAuthorization("merchant-user").RequirePermission(Keys.RolesView)
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
    .RequireAuthorization("merchant-user").RequirePermission(Keys.RolesView)
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
}).RequireAuthorization("merchant-user").RequirePermission(Keys.RolesView)
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
    Guid merchantUserId, SetMerchantUserRolesRequest body, IUserScope scope, HttpContext http,
    IMediator mediator, CancellationToken ct) =>
{
    var me = scope.Current;
    await mediator.Send(new MerchantSetRolesCommand(
        merchantUserId, body.RoleCodes ?? [], me.MerchantId, me.UserId, http.TraceIdentifier), ct);
    return Results.NoContent();
}).RequireAuthorization("merchant-user").RequirePermission(Keys.UsersRoles)
    .WithTags("บทบาท (ผู้ใช้ร้านค้า)")
    .WithName("SetMerchantUserUserRoles")
    .WithSummary("กำหนดบทบาทของผู้ใช้ร้านค้า")
    .WithDescription("ต้องมีสิทธิ์ users.roles แทนที่บทบาทของผู้ใช้ร้านค้าด้วยชุดที่ระบุมาทั้งหมด จำกัดเฉพาะร้านค้าของคุณ หากไม่รู้จัก role code -> 400; เป้าหมายไม่อยู่ร้านค้าคุณ -> 404")
    .Produces(StatusCodes.Status204NoContent)
    .ProducesProblem(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status403Forbidden);

// --- Admin approves/rejects a merchant-user (cross-plane, merchant-user-google-sso REQ-6/18) ---
// The Admin permission (merchant-user.approve/reject) + the accessible-merchant floor (IAdminQuery) run HERE, at the host,
// before crossing into the MerchantUser module (critique B3) — the dispatched command receives an already-validated
// merchant id and carries no Admin import. On the admin group, so the admin CSRF filter + Session policy apply.
// Route contract is the INTERNAL id (microsoft-oidc-ciam-alignment REQ-4.7/R1): Entra oids are GUIDs, so a
// subject-or-id dual dispatch would eat a Microsoft subject as an internal id -> 404. A non-GUID value now
// 404s at the route constraint; the admin SPA sends merchantUserId (rollout phase 1 done before this shipped).
admin.MapPost("/merchants/users/{merchantUserId:guid}/approve", async (
    Guid merchantUserId, ApproveMerchantUserRequest body, IAdminScope scope, IAdminQuery adminQuery,
    HttpContext http, IActorScope actorScope, IMediator mediator, CancellationToken ct) =>
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

    using var binding = actorScope.Begin(merchant.Id, scope.Current.AdminId);
    var result = await mediator.Send(new ApproveCommand(
        merchantUserId, merchant.Id, body.RoleCodes ?? [],
        $"admin:{scope.Current.AdminId:D}", scope.Current.AdminId, http.TraceIdentifier,
        VersionEtags.Require(http), IdempotencyKeys.Require(http)), ct);
    VersionEtags.Set(http, result.Version);
    return Results.Ok(new ApproveMerchantUserResponse(result.UserId, result.Status.ToString(), result.AlreadyActive));
}).RequireAuthorization("admin").RequirePermission(Keys.MerchantUserApprove)
    .WithMetadata(new IfMatchMutationMarker("200"), new IdempotencyMutationMarker())
    .WithTags("ผู้ใช้ร้านค้า (ผู้ดูแลระบบ)")
    .WithName("ApproveMerchantUser")
    .WithSummary("อนุมัติผู้ใช้ร้านค้าเข้าร้านค้าหนึ่ง")
    .WithDescription("ต้องมีสิทธิ์ merchants.users.approve ผูกผู้ใช้ร้านค้าเข้ากับร้านค้าที่อยู่ใน accessible set ของ admin + กำหนดบทบาท + เปิดใช้งาน ในทรานแซกชันเดียว หาก Active อยู่แล้ว -> idempotent 200; ไม่พบเป้าหมาย -> 404; ร้านค้าไม่ active/นอก scope -> 409/404; role ไม่รู้จัก/ไม่ active หรือเป้าหมายไม่ใช่ Pending -> 409")
    .Produces<ApproveMerchantUserResponse>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status409Conflict)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status403Forbidden);

admin.MapPost("/merchants/users/{merchantUserId:guid}/reject", async (
    Guid merchantUserId, RejectMerchantUserRequest body, IAdminScope scope, HttpContext http,
    IMediator mediator, CancellationToken ct) =>
{
    var result = await mediator.Send(new RejectCommand(
        merchantUserId, body.Reason, $"admin:{scope.Current.AdminId:D}", http.TraceIdentifier,
        scope.Current.AdminId, VersionEtags.Require(http), IdempotencyKeys.Require(http)), ct);
    VersionEtags.Set(http, result.Version);
    return Results.Ok(new RejectMerchantUserResponse(result.UserId, result.Status.ToString()));
}).RequireAuthorization("admin").RequirePermission(Keys.MerchantUserReject)
    .WithMetadata(new IfMatchMutationMarker("200"), new IdempotencyMutationMarker())
    .WithTags("ผู้ใช้ร้านค้า (ผู้ดูแลระบบ)")
    .WithName("RejectMerchantUser")
    .WithSummary("ปฏิเสธผู้ใช้ร้านค้าที่รอดำเนินการ")
    .WithDescription("ต้องมีสิทธิ์ merchants.users.reject ตั้งสถานะผู้ใช้ร้านค้าเป็น Rejected และเพิกถอน session ที่ยัง live อยู่ ไม่พบเป้าหมาย -> 404; เป้าหมายไม่ใช่ Pending -> 409")
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
admin.MapGet("/merchants/users/{merchantUserId:guid}/registrations", async (
    Guid merchantUserId, HttpContext http, IAdminScope scope, IMediator mediator, CancellationToken ct,
    bool reveal = false) =>
{
    // Accessible-merchant floor (REQ-2.7): threaded as primitives —
    // a merchant-bound target outside the admin's scope reads as 404 inside the handler (no existence leak).
    var result = await mediator.Send(new GetRegistrationHistoryQuery(
        merchantUserId, reveal, $"admin:{scope.Current.AdminId:D}", scope.Current.AdminId, http.TraceIdentifier,
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
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

// Super creates a pre-bound Scoped Microsoft admin from an approved Entra object ID. This is
// the admins-area ROOT (POST /api/v1/admins): mapped on `api` with AdminCsrfFilter applied per-endpoint — a group's
// empty-string root pattern would render the trailing-slash "/api/v1/admins/" (REQ-1.4). Same CSRF + auth as the group.
api.MapPost("/admins", async (
    CreateAdminRequest body, IAdminScope scope, HttpContext http, IMediator mediator, CancellationToken ct) =>
{
    var result = await mediator.Send(new CreateScopedCommand(
        body.ObjectId, body.Email, body.IdentityApprovalReference, scope.Current.AdminId, http.TraceIdentifier), ct);
    return Results.Created($"/api/v1/admins/{result.AdminId}", result);
}).RequireCsrf().RequireAuthorization("admin").RequirePlatformUserTier(Tier.Super)
    .WithTags("ผู้ดูแลระบบ")
    .WithName("CreateScopedAdmin")
    .WithSummary("สร้าง Scoped Microsoft admin แบบ pre-bound")
    .WithDescription("เฉพาะ Super ใช้ ObjectId จาก Entra export ที่ตรวจสอบแล้วและ approval reference; อีเมลเป็นข้อมูลติดต่อที่ไม่บังคับ")
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
    new(a.AdminId, a.Email, TierToWire(a.Tier), AccountStatusToWire(a.Status), a.CreatedAt, a.SubjectBound, a.Version);
static PlatformUserSessionResponse SessionToWire(SessionView v) =>
    new(v.SessionId, v.FamilyId, SessionStatusToWire(v.Status), v.IssuedAt, v.IdleExpiresAt, v.AbsoluteExpiresAt,
        v.IpAddress, v.UserAgent, v.IsLive);

// The admin directory (REQ-1). Mapped on `api` (not the admins group): a group empty-string root pattern would
// render the forbidden trailing slash "/api/v1/admins/", same as POST /admins. Gated user.view — reads use the
// permission axis (a user.roles holder needs the directory to assign roles; see the role-composition note).
api.MapGet("/admins", async (HttpContext http, IMediator mediator, CancellationToken ct) =>
{
    var p = SfsQueryParser.Parse(http.Request.Query);
    var result = await mediator.Send(new ListAdminsQuery
    {
        Page = p.Page,
        Limit = p.Limit,
        Filters = p.Filters,
        Sort = p.Sort,
        Search = p.Search,
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
admin.MapGet("/{id:guid}", async (
    Guid id, HttpContext http, IAdminMerchantDirectory merchants, IMediator mediator, CancellationToken ct) =>
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

    var response = new AdminDetailResponse(
        detail.AdminId, detail.Email, TierToWire(detail.Tier), AccountStatusToWire(detail.Status),
        detail.CreatedAt, detail.SubjectBound, accessible, detail.RoleCodes, detail.Version);
    VersionEtags.Set(http, detail.Version);
    return Results.Ok(response);
}).RequireAuthorization("admin").RequirePermission(Keys.UserView)
    .WithMetadata(new EtagResponseMarker("200"))
    .WithTags("ผู้ดูแลระบบ")
    .WithName("GetAdmin")
    .WithSummary("อ่านบัญชีผู้ดูแลระบบ")
    .WithDescription("ต้องมีสิทธิ์ user.view คืน tier, status, ร้านค้าที่เข้าถึงได้ (ไม่จำกัดสำหรับ Super) และ role code ที่กำหนดให้ทั้งหมด หากไม่พบ id -> 404")
    .Produces<AdminDetailResponse>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status403Forbidden)
    .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

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
    var result = await mediator.Send(new AssignMerchantCommand(
        id, body.MerchantId, scope.Current.AdminId, http.TraceIdentifier, VersionEtags.Require(http)), ct);
    VersionEtags.Set(http, result.Version);
    return Results.Ok(result);
}).RequireAuthorization("admin").RequirePlatformUserTier(Tier.Super)
    .WithMetadata(new IfMatchMutationMarker("200"))
    .WithTags("ผู้ดูแลระบบ")
    .WithName("AssignMerchantToAdmin")
    .WithSummary("มอบสิทธิ์ร้านค้าให้ผู้ดูแลระบบ")
    .WithDescription("เฉพาะ Super ให้สิทธิ์ Scoped admin เข้าถึงร้านค้าหนึ่ง ร้านค้าไม่ active/ไม่รู้จัก หรือซ้ำ -> 409")
    .Produces<AssignMerchantResult>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status409Conflict)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status403Forbidden);

// Super unassigns a merchant — a hard delete of the assignment row (REQ-4.2). Unknown assignment -> 404.
admin.MapDelete("/{id:guid}/merchants/{merchantId:guid}", async (
    Guid id, Guid merchantId, IAdminScope scope, HttpContext http, IMediator mediator, CancellationToken ct) =>
{
    var result = await mediator.Send(new UnassignMerchantCommand(
        id, merchantId, scope.Current.AdminId, http.TraceIdentifier, VersionEtags.Require(http)), ct);
    VersionEtags.Set(http, result.Version);
    return Results.NoContent();
}).RequireAuthorization("admin").RequirePlatformUserTier(Tier.Super)
    .WithMetadata(new IfMatchMutationMarker("204"))
    .WithTags("ผู้ดูแลระบบ")
    .WithName("UnassignMerchantFromAdmin")
    .WithSummary("ถอนสิทธิ์ร้านค้าจากผู้ดูแลระบบ")
    .WithDescription("เฉพาะ Super ลบแถว merchant assignment แบบถาวร ไม่พบ assignment -> 404")
    .Produces(StatusCodes.Status204NoContent)
    .ProducesProblem(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status409Conflict)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status403Forbidden);

// Super suspends another admin; suspending your OWN account is rejected so oversight is never locked out (REQ-8.2).
admin.MapPost("/{id:guid}/suspend", async (
    Guid id, IAdminScope scope, HttpContext http, IMediator mediator, CancellationToken ct) =>
{
    if (id == scope.Current.AdminId)
        return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "An admin cannot suspend their own account.");
    var result = await mediator.Send(new SuspendCommand(
        id, scope.Current.AdminId, http.TraceIdentifier, VersionEtags.Require(http)), ct);
    VersionEtags.Set(http, result.Version);
    return Results.NoContent();
}).RequireAuthorization("admin").RequirePlatformUserTier(Tier.Super)
    .WithMetadata(new IfMatchMutationMarker("204"))
    .WithTags("ผู้ดูแลระบบ")
    .WithName("SuspendAdmin")
    .WithSummary("ระงับใช้งานผู้ดูแลระบบ")
    .WithDescription("เฉพาะ Super ระงับใช้งานผู้ดูแลระบบคนอื่น ระงับบัญชีตัวเองไม่ได้ (403) เพื่อไม่ให้ oversight ถูกล็อกออก")
    .Produces(StatusCodes.Status204NoContent)
    .ProducesProblem(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status409Conflict)
    .ProducesProblem(StatusCodes.Status403Forbidden)
    .ProducesProblem(StatusCodes.Status401Unauthorized);

// Super reactivates a suspended admin (REQ-3). Idempotent 204; unknown id -> 404. On the Suspended->Active
// transition the target's sessions are revoked (a fresh login is required); the already-Active case revokes nothing.
admin.MapPost("/{id:guid}/reactivate", async (
    Guid id, IAdminScope scope, HttpContext http, IMediator mediator, CancellationToken ct) =>
{
    var result = await mediator.Send(new ReactivateCommand(
        id, scope.Current.AdminId, http.TraceIdentifier, VersionEtags.Require(http)), ct);
    VersionEtags.Set(http, result.Version);
    return Results.NoContent();
}).RequireAuthorization("admin").RequirePlatformUserTier(Tier.Super)
    .WithMetadata(new IfMatchMutationMarker("204"))
    .WithTags("ผู้ดูแลระบบ")
    .WithName("ReactivateAdmin")
    .WithSummary("เปิดใช้งานผู้ดูแลระบบที่ถูกระงับ")
    .WithDescription("เฉพาะ Super คืนสถานะผู้ดูแลระบบที่ถูกระงับกลับเป็น Active และเพิกถอน session เดิม (ต้อง login ใหม่) idempotent ถ้า Active อยู่แล้ว หากไม่พบ id -> 404")
    .Produces(StatusCodes.Status204NoContent)
    .ProducesProblem(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status409Conflict)
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
    var result = await mediator.Send(new ChangeAdminTierCommand(
        id, newTier, scope.Current.AdminId, http.TraceIdentifier, VersionEtags.Require(http)), ct);
    VersionEtags.Set(http, result.Version);
    return Results.Ok(result);
}).RequireAuthorization("admin").RequirePlatformUserTier(Tier.Super)
    .WithMetadata(new IfMatchMutationMarker("200"))
    .WithTags("ผู้ดูแลระบบ")
    .WithName("ChangeAdminTier")
    .WithSummary("เลื่อนหรือลด tier ของผู้ดูแลระบบ")
    .WithDescription("เฉพาะ Super เปลี่ยน tier ผู้ดูแลระบบระหว่าง scoped กับ super เปลี่ยน tier ตัวเองไม่ได้ (403) เพื่อไม่ให้ oversight ค้าง idempotent ถ้า tier ตรงกับที่ขออยู่แล้ว หากไม่พบ id -> 404")
    .Produces<ChangeAdminTierResult>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status403Forbidden);

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
        new RevokeSessionCommand(
            id, sessionId, scope.Current.AdminId, http.TraceIdentifier, IdempotencyKeys.Require(http)), ct);
    // Security-log the specifics the append-only audit table has no column for (REQ-5.2), keyed by correlation id.
    loggerFactory.CreateLogger("Admin.SessionManagement").LogInformation(
        "Admin session family revoked: sessionId={SessionId} familyId={FamilyId} targetAdminId={TargetAdminId} correlationId={CorrelationId}",
        result.SessionId, result.FamilyId, result.AdminId, http.TraceIdentifier);
    return Results.NoContent();
}).RequireAuthorization("admin").RequirePlatformUserTier(Tier.Super)
    .WithMetadata(new IdempotencyMutationMarker())
    .WithTags("ผู้ดูแลระบบ")
    .WithName("RevokePlatformUserSession")
    .WithSummary("เพิกถอน session ของผู้ดูแลระบบ")
    .WithDescription("เฉพาะ Super เพิกถอนทั้ง rotation family ของ session ไม่พบ session หรือ session เป็นของผู้ดูแลระบบคนอื่น -> 404 idempotent (เพิกถอนไปแล้ว -> 204)")
    .Produces(StatusCodes.Status204NoContent)
    .ProducesProblem(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status409Conflict)
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
    r.PermissionKeys, r.UserCount, r.Version);
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
        catalog.Groups.Select(g => new PermissionGroupResponse(g.Key, g.Name)).ToArray(),
        catalog.Permissions.Select(p => new PermissionItemResponse(p.Key, p.Name, p.Resource)).ToArray()));
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
        Page = p.Page,
        Limit = p.Limit,
        Filters = p.Filters,
        Sort = p.Sort,
        Search = p.Search,
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

admin.MapGet("/roles/{code}", async (
    string code, HttpContext http, IAdminScope scope, IMediator mediator, CancellationToken ct) =>
{
    var role = await mediator.Send(new GetRoleQuery(RoleSideContextResolver.ForAdmin(scope), code), ct);
    if (role is null)
        return Results.Problem(statusCode: StatusCodes.Status404NotFound);
    VersionEtags.Set(http, role.Version);
    return Results.Ok(RoleToWire(role));
}).RequireAuthorization("admin")
    .WithMetadata(new EtagResponseMarker("200"))
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
    VersionEtags.Set(http, result.Version);
    return Results.Created($"/api/v1/admins/roles/{result.Code}", RoleToWire(result));
}).RequireAuthorization("admin").RequirePermission(Keys.UserRoles)
    .WithMetadata(new EtagResponseMarker("201"))
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
        ParseRoleStatus(body.Status), body.Permissions ?? [], http.TraceIdentifier, VersionEtags.Require(http)), ct);
    VersionEtags.Set(http, result.Version);
    return Results.Ok(RoleToWire(result));
}).RequireAuthorization("admin").RequirePermission(Keys.UserRoles)
    .WithMetadata(new IfMatchMutationMarker("200"))
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
    await mediator.Send(new DeleteRoleCommand(
        RoleSideContextResolver.ForAdmin(scope), code, http.TraceIdentifier, VersionEtags.Require(http)), ct);
    return Results.NoContent();
}).RequireAuthorization("admin").RequirePermission(Keys.UserRoles)
    .WithMetadata(new IfMatchMutationMarker("204", EmitsEtag: false))
    .WithTags("บทบาท (ผู้ดูแลระบบ)")
    .WithName("DeleteRole")
    .WithSummary("ลบบทบาท")
    .WithDescription("ต้องมีสิทธิ์ user.roles บทบาทที่ยังมีผู้ใช้ผูกอยู่ลบไม่ได้ -> 409")
    .Produces(StatusCodes.Status204NoContent)
    .ProducesProblem(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status409Conflict)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status403Forbidden);

// Set an admin's roles to exactly the given set (REQ-4.2). Unknown role code -> 400; unknown admin -> 404.
admin.MapPut("/{id:guid}/roles", async (
    Guid id, SetAdminRolesRequest body, IAdminScope scope, HttpContext http, IMediator mediator, CancellationToken ct) =>
{
    var result = await mediator.Send(new SetRolesCommand(
        id, body.RoleCodes ?? [], scope.Current.AdminId, http.TraceIdentifier, VersionEtags.Require(http)), ct);
    VersionEtags.Set(http, result.Version);
    return Results.NoContent();
}).RequireAuthorization("admin").RequirePermission(Keys.UserRoles)
    .WithMetadata(new IfMatchMutationMarker("204"))
    .WithTags("ผู้ดูแลระบบ")
    .WithName("SetAdminRoles")
    .WithSummary("กำหนดบทบาทของผู้ดูแลระบบ")
    .WithDescription("ต้องมีสิทธิ์ user.roles แทนที่บทบาทของผู้ดูแลระบบด้วยชุดที่ระบุมาทั้งหมด หากไม่รู้จัก role code -> 400; ไม่พบผู้ดูแลระบบ -> 404")
    .Produces(StatusCodes.Status204NoContent)
    .ProducesProblem(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status409Conflict)
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

static bool IsAdminCommerceRequest(HttpContext http) =>
    http.Features.Get<SelectedConsoleAudience>()?.Value == ConsoleAudience.Admin;

static Guid RequireCommerceQueryGuid(HttpContext http, string name)
{
    var raw = http.Request.Query[name].ToString();
    if (!Guid.TryParse(raw, out var value) || value == Guid.Empty)
        throw new InvalidRequestException($"{name} must be a non-empty UUID.", "invalid_filter");
    return value;
}

static int RequireCartVersion(HttpContext http)
{
    var version = VersionEtags.Require(http);
    if (version > int.MaxValue)
        throw new InvalidRequestException("Cart ETag version is out of range.", "invalid_etag");
    return (int)version;
}

static AdminMerchantAccess CommerceMerchantAccess(IAdminScope scope) => new(
    scope.Current.AdminId, scope.Accessible.IsUnrestricted, scope.Accessible.Merchants);

static AdminOrderAccess CommerceOrderAccess(IAdminScope scope) => new(
    scope.Accessible.IsUnrestricted, scope.Accessible.Merchants);

static void RequireCommerceMerchantAccess(IAdminScope scope, Guid merchantId)
{
    if (!scope.Accessible.Allows(merchantId))
        throw new AccessDeniedException(
            "Merchant is outside the current admin scope.", "merchant_scope_forbidden");
}

static async Task<OriginatorView> RequireCommerceOriginatorAsync(
    IAdminMerchantControlStore store,
    IAdminScope scope,
    Guid merchantId,
    Guid originatorId,
    CancellationToken ct)
{
    RequireCommerceMerchantAccess(scope, merchantId);
    var originator = await store.GetOriginatorAsync(
        originatorId, merchantId, CommerceMerchantAccess(scope), ct);
    if (originator is null
        || !string.Equals(originator.Status, "active", StringComparison.Ordinal)
        || string.IsNullOrWhiteSpace(originator.SaleCode))
        throw new AccessDeniedException(
            "Originator is unavailable for this merchant.", "originator_scope_forbidden");
    return originator;
}

static async Task<AdminCartResource> RequireAdminCartAsync(
    IAdminCartReader carts,
    IAdminScope scope,
    Guid cartId,
    Guid? expectedMerchantId,
    bool mutation,
    CancellationToken ct)
{
    var cart = await carts.ResolveAsync(
        cartId, scope.Accessible.IsUnrestricted, scope.Accessible.Merchants, ct);
    if (cart is null)
        throw new NotFoundException("Cart was not found.");
    if (expectedMerchantId is { } merchantId && merchantId != cart.MerchantId)
    {
        if (mutation)
            throw new AccessDeniedException(
                "Cart does not belong to the selected merchant.", "merchant_scope_forbidden");
        throw new NotFoundException("Cart was not found.");
    }
    return cart;
}

static async Task<AdminOrderResource> RequireAdminOrderAsync(
    IAdminOrderReader orders,
    IAdminScope scope,
    Guid orderId,
    Guid? expectedMerchantId,
    bool mutation,
    CancellationToken ct)
{
    var order = await orders.ResolveAsync(orderId, CommerceOrderAccess(scope), ct);
    if (order is null)
        throw new NotFoundException("Order was not found.");
    if (expectedMerchantId is { } merchantId && merchantId != order.MerchantId)
    {
        if (mutation)
            throw new AccessDeniedException(
                "Order does not belong to the selected merchant.", "merchant_scope_forbidden");
        throw new NotFoundException("Order was not found.");
    }
    return order;
}

static async Task<AddItemToCartCommand> BuildAddCartItemCommandAsync(
    Guid cartId,
    Guid merchantId,
    string? saleCode,
    AddItemToCartRequest body,
    int? expectedVersion,
    bool admin,
    IMediator mediator,
    IDocumentSaleProbe documentSales,
    CancellationToken ct)
{
    if (string.IsNullOrWhiteSpace(saleCode))
        throw new AccessDeniedException(
            "No sale code is bound to this commerce actor.", "sale-code-missing");
    if (body.Quantity <= 0)
        throw new InvalidRequestException("Quantity must be positive.", "validation_failed");
    if (!Enum.GetNames<ProductGroup>().Contains(body.VariantCode, StringComparer.Ordinal)
        || !Enum.TryParse<ProductGroup>(body.VariantCode, out var productGroup))
        throw new InvalidRequestException("Unsupported variant code.", "validation_failed");

    var document = await mediator.Send(
        new LookupDocumentQuery(body.ProductCode, productGroup, saleCode), ct);
    if (document is null || document.PaymentStatus == PaymentStatus.PAID)
    {
        if (admin)
            throw new ConflictException("The document is not available for sale.", "product_unpayable");
        throw new InvalidRequestException("The document is not available for sale.", "product_unpayable");
    }
    var statuses = await documentSales.ProbeAsync(
        [new DocumentKey(document.DocumentNo, document.ProductGroup.ToString())], ct);
    if (statuses.Count > 0)
    {
        if (admin)
            throw new ConflictException("The document is not available for sale.", "product_unpayable");
        throw new InvalidRequestException("The document is not available for sale.", "product_unpayable");
    }

    var variantCode = document.ProductGroup.ToString();
    var metadata = new CommerceItemMetadata(
        CommerceItemMetadataCodec.InsuranceDocumentSource,
        document.DocumentType.ToString(),
        document.PolicyNumber,
        document.StartDate is { } start ? DateOnly.FromDateTime(start) : null,
        document.EndDate is { } end ? DateOnly.FromDateTime(end) : null);
    return new AddItemToCartCommand(
        cartId, merchantId, document.DocumentNo, document.SaleCode, variantCode,
        string.IsNullOrWhiteSpace(document.ShowName) ? variantCode : document.ShowName,
        body.Quantity, Money.Of(document.TotalPremium, "THB"), metadata, expectedVersion);
}

static Task<AdminOperationResult<T>> ExecuteAdminCommerceAsync<T>(
    IAdminOperationExecutor operations,
    IAdminScope scope,
    Guid merchantId,
    string operation,
    string idempotencyKey,
    object intent,
    int status,
    Func<CancellationToken, Task<T>> action,
    Func<T, string?> resourceId,
    CancellationToken ct) =>
    operations.ExecuteAsync(
        new AdminOperationRequest(
            merchantId, scope.Current.AdminId, operation, idempotencyKey,
            JsonSerializer.Serialize(intent), status),
        action, resourceId, ct);

static Task<AdminOperationResult<T>> ExecuteRecoverableAdminCommerceAsync<T>(
    IAdminOperationExecutor operations,
    IAdminScope scope,
    Guid merchantId,
    string operation,
    string idempotencyKey,
    object intent,
    int status,
    Func<CancellationToken, Task<T>> action,
    Func<T, string?> resourceId,
    CancellationToken ct) =>
    operations.ExecuteRecoverableAsync(
        new AdminOperationRequest(
            merchantId, scope.Current.AdminId, operation, idempotencyKey,
            JsonSerializer.Serialize(intent), status),
        action, resourceId, ct);

static string FixedMoney(decimal value) =>
    value.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture);

static AdminMoneyResponse AdminMoney(Money value) => new(FixedMoney(value.Amount), value.Currency);

static AdminOrderListResponse AdminOrderList(OrderListItem item) => new(
    item.OrderId, item.OrderNo, item.MerchantId, item.OriginatorId,
    AdminMoney(item.Amount), item.Status, item.Lines.Count,
    item.PaymentChannel, item.PaymentSessionId, item.CreatedAt, item.UpdatedAt);

static AdminPaymentSessionResponse AdminPaymentSession(PaymentSessionView session) => new(
    session.PaymentSessionId, session.OrderId, session.MerchantId, AdminMoney(session.Amount),
    session.Method, session.Psp.ToCode(), session.Status.ToString(), session.RedirectUrl,
    session.CreatedAt, session.UpdatedAt, session.Version);

static string MaskCustomerReference(string? email, string phone)
{
    if (!string.IsNullOrWhiteSpace(email))
    {
        var at = email.IndexOf('@');
        return at > 0 ? $"{email[0]}***{email[at..]}" : "***";
    }
    var digits = new string(phone.Where(char.IsDigit).ToArray());
    return digits.Length >= 4 ? $"***{digits[^4..]}" : "***";
}

static (DateTime From, DateTime To) RequireExportWindow(HttpContext http)
{
    static DateTime Parse(string value, string name)
    {
        if (!DateTime.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal
                | System.Globalization.DateTimeStyles.AdjustToUniversal, out var parsed))
            throw new InvalidRequestException($"{name} must be an ISO-8601 instant.", "invalid_filter");
        return parsed;
    }

    var from = Parse(http.Request.Query["from"].ToString(), "from");
    var to = Parse(http.Request.Query["to"].ToString(), "to");
    if (to < from || to - from > TimeSpan.FromDays(31))
        throw new InvalidRequestException(
            "Export range must be ordered and no longer than 31 days.", "invalid_filter");
    return (from, to);
}

static string CsvCell(string? value)
{
    value ??= string.Empty;
    if (value.Length > 0 && "=+-@".Contains(value[0]))
        value = "'" + value;
    return $"\"{value.Replace("\"", "\"\"")}\"";
}

static IReadOnlyList<CommerceCapabilityResponse> OrderCapabilities(string status) =>
[
    new("cancel", string.Equals(status, nameof(OrderStatus.Pending), StringComparison.OrdinalIgnoreCase), false,
        string.Equals(status, nameof(OrderStatus.Pending), StringComparison.OrdinalIgnoreCase) ? null : "state_conflict"),
    new("resend_link", string.Equals(status, nameof(OrderStatus.Pending), StringComparison.OrdinalIgnoreCase), false,
        string.Equals(status, nameof(OrderStatus.Pending), StringComparison.OrdinalIgnoreCase) ? null : "state_conflict"),
    new("extend_link", false, false, "capability_unavailable"),
    new("cancel_link", false, false, "capability_unavailable"),
    new("capture", false, false, "capability_unavailable"),
    new("void", false, false, "capability_unavailable"),
    new("refund", false, true, "capability_unavailable"),
    new("receipt", false, false, "capability_unavailable"),
];

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
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record CreateMerchantUserInvitationRequest(string Email);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record UpdateMerchantUserRequest(
    string FirstName, string LastName, string? ProducerCode, string? LicenseNumber, string? Phone);
// `Roles` = the merchant-user's ACTIVE role codes (the multi-role model's read of REQ-17.5's `role`).
internal sealed record MerchantUserMeResponse(
    Guid UserId, string Email, Guid MerchantId, IReadOnlyList<string> Roles, IReadOnlySet<string> Permissions);
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
internal sealed record ApproveMerchantUserResponse(Guid UserId, string Status, bool AlreadyActive);
internal sealed record RejectMerchantUserResponse(Guid UserId, string Status);

// No Amount: the charge is priced from the order row server-side (a body that still sends "amount" is
// simply ignored — the platform never mints a charge the order does not back).
internal sealed record CreatePaymentSessionRequest(
    Guid OrderId, string Method, Code? Psp, Guid? MerchantId);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record MerchantCreatePaymentSessionRequest(Guid OrderId, string Method, Code Psp);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record AdminCreatePaymentSessionRequest(Guid OrderId, string Method, Guid MerchantId);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record AddItemToCartRequest(string ProductCode, string VariantCode, int Quantity);
internal sealed record SetCartItemQuantityRequest(int Quantity);
internal sealed record CreateCartResponse(Guid CartId);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record CreateOrderCustomerRequest(string? Name, string? Phone, string? Email);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record CreateOrderFromCartRequest(
    Guid CartId, CreateOrderCustomerRequest? Customer, string PaymentMethod,
    Guid? MerchantId, Guid? OriginatorId);
internal sealed record OrderSummaryLineResponse(
    string ProductCode, string VariantCode, string? VariantName, int Quantity, Money UnitPrice);
internal sealed record OrderSummaryResponse(
    Guid OrderId, string OrderNo, Money Amount, string Status,
    IReadOnlyList<OrderSummaryLineResponse> Lines);
/// <summary>The customer's payment state, lowercase on the wire: paid | failed | pending | cancelled.</summary>
internal sealed record PaymentStatusResponse(string Status);

// Admin provisioning request body (reference 2.4): { "merchant": { ... }, "pspConnections": [ ... ] }.
// AdminSubject + correlation id are NOT in the body — the host sets them from the authenticated request.
internal sealed record ProvisionMerchantRequest(
    ProvisionMerchantBody? Merchant,
    IReadOnlyList<ProvisionPspConnectionRequest>? PspConnections);

// Merchant scalars are first-class columns; metadata uses an explicit typed allowlist. Unknown and
// secret-shaped fields fail JSON binding instead of flowing into native JSON storage.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class ProvisionMerchantBody
{
    public string Code { get; init; } = default!;
    public string Name { get; init; } = default!;
    public string? Note { get; init; }
    public string Country { get; init; } = default!;
    public string Currency { get; init; } = default!;
    public IReadOnlyList<string>? EnabledChannels { get; init; }
    public MerchantBrandingMetadata? Branding { get; init; }
    public MerchantRoutingMetadata? Routing { get; init; }
    public MerchantSessionMetadata? Session { get; init; }
    public string? Timezone { get; init; }
    public string? Locale { get; init; }
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

    /// <summary>Production guard for the fixed Microsoft workforce Admin provider. Microsoft is the only
    /// supported provider, so enabling any other one is a deployment error rather than a fallback.</summary>
    public static void RequireWorkforceAdminProvider(IConfiguration configuration)
    {
        var graphBaseUrl = configuration["AdminAuth:GraphBaseUrl"] ?? "https://graph.microsoft.com";
        if (!string.Equals(graphBaseUrl, "https://graph.microsoft.com", StringComparison.Ordinal))
            throw new InvalidOperationException(
                "AdminAuth:GraphBaseUrl must be https://graph.microsoft.com in Production.");

        var providers = configuration.GetSection("AdminAuth:Providers").GetChildren().ToArray();
        var unsupported = providers.FirstOrDefault(provider =>
            !string.Equals(provider.Key, "Microsoft", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(provider["ClientId"]));
        if (unsupported is not null)
            throw new InvalidOperationException(
                $"AdminAuth:Providers:{unsupported.Key} is not supported. Microsoft is the only Admin provider — "
                + "leave the other provider's ClientId blank and configure the Microsoft workforce provider.");

        var microsoft = providers.Where(provider =>
            string.Equals(provider.Key, "Microsoft", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (microsoft.Length != 1)
            throw new InvalidOperationException(
                "AdminAuth:Providers:Microsoft must be configured exactly once in Production.");

        var provider = microsoft[0];
        var clientId = provider["ClientId"];
        if (string.IsNullOrWhiteSpace(clientId) || clientId.StartsWith("REPLACE_WITH_", StringComparison.Ordinal))
            throw new InvalidOperationException(
                "AdminAuth:Providers:Microsoft:ClientId is required in Production. Set "
                + "AdminAuth__Providers__Microsoft__ClientId.");

        var clientSecret = provider["ClientSecret"];
        if (string.IsNullOrWhiteSpace(clientSecret) || clientSecret.StartsWith("REPLACE_WITH_", StringComparison.Ordinal))
            throw new InvalidOperationException(
                "AdminAuth:Providers:Microsoft:ClientSecret is required in Production. Set "
                + "AdminAuth__Providers__Microsoft__ClientSecret.");

        var callbackPath = provider["CallbackPath"];
        if (!string.Equals(callbackPath, "/api/v1/admins/auth/microsoft/callback", StringComparison.Ordinal))
            throw new InvalidOperationException(
                "AdminAuth:Providers:Microsoft:CallbackPath must be "
                + "/api/v1/admins/auth/microsoft/callback in Production.");

        try
        {
            // Parse enforces HTTPS public-cloud Authority with exactly one workforce tenant UUID and /v2.0.
            _ = Api.Admins.AdminMicrosoftTenantSnapshot.Parse(clientId, provider["Authority"]);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException(
                "AdminAuth:Providers:Microsoft:Authority must pin the workforce tenant UUID in Production.", ex);
        }
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
            // Microsoft is the only supported provider on either side (Google was retired). A configured
            // non-Microsoft provider would register no scheme, so its login would 404 at runtime — fail at boot
            // instead, where the misconfiguration is visible.
            if (!string.Equals(provider.Key, "Microsoft", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"{sectionName}:Providers:{provider.Key} is not a supported provider — Microsoft is the only " +
                    $"one. Leave {sectionName}__Providers__{provider.Key}__ClientId blank or remove the provider.");
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

            // NO plane may accept EVERY Entra tenant: issuer validation is the framework default (iss == the
            // tenant-pinned Authority's metadata issuer), so a multi-tenant Authority (common/organizations/
            // consumers) would admit any tenant — AllowedTenants is only an OPTIONAL extra gate, not a substitute.
            if (string.Equals(provider.Key, "Microsoft", StringComparison.OrdinalIgnoreCase)
                && (authority.Contains("/common", StringComparison.OrdinalIgnoreCase)
                    || authority.Contains("/organizations", StringComparison.OrdinalIgnoreCase)
                    || authority.Contains("/consumers", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException(
                    $"{sectionName}:Providers:Microsoft:Authority is multi-tenant (common/organizations/consumers) — " +
                    "not allowed: issuer validation pins the tenant via the Authority's discovery metadata. Pin it to " +
                    $"a tenant, e.g. {sectionName}__Providers__Microsoft__Authority=https://login.microsoftonline.com/<tenant-id>/v2.0.");

            anyConfigured = true;
        }

        if (requireAtLeastOne && !anyConfigured)
            throw new InvalidOperationException(
                $"{sectionName}:Providers requires at least one provider with a configured ClientId — the login " +
                $"cannot build an authorization request without one. Set {sectionName}__Providers__Microsoft__ClientId.");
    }
}

// Admin identity foundation request bodies (REQ-3/4). ActingAdminId + correlation id are NOT in the body —
// the host sets them from the resolved IAdminScope + the authenticated request.
internal sealed record CreateAdminRequest(
    Guid ObjectId, string IdentityApprovalReference, string? Email = null);
internal sealed record AssignMerchantRequest(Guid MerchantId);
internal sealed record ChangeAdminTierRequest(string Tier);

internal sealed record CreatePaymentSessionResponse(Guid PaymentSessionId);

internal sealed record AdminMoneyResponse(string Amount, string Currency);
internal sealed record AdminOrderListResponse(
    Guid OrderId, string OrderNo, Guid MerchantId, Guid? OriginatorId,
    AdminMoneyResponse Amount, string Status, int ItemCount, string? PaymentChannel,
    Guid? PaymentSessionId, DateTime CreatedAt, DateTime UpdatedAt);
internal sealed record AdminOrderLineResponse(
    string ProductCode, string VariantCode, string? VariantName, int Quantity,
    AdminMoneyResponse UnitPrice, AdminMoneyResponse Discount, JsonElement? Metadata);
internal sealed record CommerceCapabilityResponse(
    string Code, bool Available, bool RequiresApproval, string? ReasonCode);
internal sealed record CommerceLifecycleResponse(string Status, DateTime At);
internal sealed record AdminPaymentSessionResponse(
    Guid PaymentSessionId, Guid OrderId, Guid MerchantId, AdminMoneyResponse Amount,
    string Method, string Psp, string Status, string? RedirectUrl,
    DateTime CreatedAt, DateTime UpdatedAt, long Version);
internal sealed record AdminOrderDetailResponse(
    Guid OrderId, string OrderNo, Guid MerchantId, Guid? OriginatorId,
    AdminMoneyResponse Amount, string Status, int ItemCount, string? PaymentChannel,
    Guid? PaymentSessionId, DateTime CreatedAt, DateTime UpdatedAt,
    IReadOnlyList<AdminOrderLineResponse> Lines, string CustomerReference,
    string SummaryLinkState,
    [property: JsonPropertyName("paymentSession")] AdminPaymentSessionResponse? Session,
    IReadOnlyList<CommerceLifecycleResponse> Lifecycle,
    IReadOnlyList<CommerceCapabilityResponse> Capabilities, long Version);

internal sealed record AdminProductListItem(
    string ProductGroup, string DocumentType, string DocumentNo, string? PolicyYear,
    string? ReferenceBranch, string? ReferencePre, string? PolicySequenceNo, string? ReferenceYear,
    string? ReferenceNo, string? PolicyBranch, string? PolicyType, string SaleCode,
    string? SaleFullName, string? BrokerCode, string? BrokerName, string? PolicyNumber,
    string? ApplicationNumber, string? PreviousPolicyNumber, string? EndorsementNumber,
    DateTime? StartDate, DateTime? EndDate, string? ShowName,
    string? NetPremium, string? Stamp, string? TaxVat, string TotalPremium,
    string? CommissionPercent, string? CommissionAmount, DateTime? PaidDate,
    string? LicensePlateNumber, string PaymentStatus, bool SoldByPlatform,
    string ProductCode, string VariantCode, string InsuranceType)
{
    public static AdminProductListItem From(ProductListItem item) => new(
        item.ProductGroup.ToString(), item.DocumentType.ToString(), item.DocumentNo, item.PolicyYear,
        item.ReferenceBranch, item.ReferencePre, item.PolicySequenceNo, item.ReferenceYear,
        item.ReferenceNo, item.PolicyBranch, item.PolicyType, item.SaleCode, item.SaleFullName,
        item.BrokerCode, item.BrokerName, item.PolicyNumber, item.ApplicationNumber,
        item.PreviousPolicyNumber, item.EndorsementNumber, item.StartDate, item.EndDate,
        item.ShowName, item.NetPremium is { } net ? Fixed(net) : null,
        item.Stamp is { } stamp ? Fixed(stamp) : null,
        item.TaxVat is { } tax ? Fixed(tax) : null, Fixed(item.TotalPremium),
        item.CommissionPercent is { } percent ? Fixed(percent) : null,
        item.CommissionAmount is { } commission ? Fixed(commission) : null,
        item.PaidDate, item.LicensePlateNumber, item.PaymentStatus.ToString(), item.SoldByPlatform,
        item.ProductCode, item.VariantCode, item.InsuranceType.ToString());

    private static string Fixed(decimal value) =>
        value.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture);
}

internal sealed record AdminProductPage(
    Guid MerchantId, Guid OriginatorId, IReadOnlyList<AdminProductListItem> Items,
    long? TotalRows, long? TotalPages, int PageNo, int PageSize,
    bool HasNextPage, bool HasPreviousPage, string CountMode, int SearchWindowMonths)
{
    public static AdminProductPage From(Guid merchantId, Guid originatorId, ProductPage page) => new(
        merchantId, originatorId, page.Items.Select(AdminProductListItem.From).ToArray(),
        page.TotalRows, page.TotalPages, page.PageNo, page.PageSize,
        page.HasNextPage, page.HasPreviousPage, page.CountMode, page.SearchWindowMonths);
}
internal sealed record StartRedirectResponse(string RedirectUrl);
internal sealed record WebhookResponse(string Outcome);

// Admin read responses — named records (not anonymous objects) so the OpenAPI doc carries a response schema
// Scalar can render. Wire shape matches the previous anonymous objects (camelCase via the web JSON defaults).
internal sealed record AdminMeResponse(
    Guid AdminId, string? Email, string Tier, AdminAccessibleResponse AccessibleMerchants,
    IReadOnlySet<string> Permissions);
internal sealed record AdminAccessibleResponse(
    bool IsUnrestricted,
    // Omitted entirely (not null) for a Super, matching the prior shape.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyCollection<AdminAccessibleMerchantResponse>? Merchants);
internal sealed record AdminAccessibleMerchantResponse(Guid Id, string? Code);
// admin-account-management REQ-1.2/1.6: one admin directory row; tier/status are lowercase wire strings.
internal sealed record AdminListItemResponse(
    Guid AdminId, string? Email, string Tier, string Status, DateTime CreatedAt, bool SubjectBound, long Version);
// admin-account-management REQ-2.1: full detail. The accessible-merchants field is named AccessibleMerchants to match
// GET /me's AdminMeResponse exactly (same nested DTO AND same JSON key), so a client can share one renderer.
internal sealed record AdminDetailResponse(
    Guid AdminId, string? Email, string Tier, string Status, DateTime CreatedAt, bool SubjectBound,
    AdminAccessibleResponse AccessibleMerchants, IReadOnlyList<string> RoleCodes, long Version);
// admin-account-management REQ-4.2: one session row; status is a lowercase wire string; NO token material.
internal sealed record PlatformUserSessionResponse(
    Guid SessionId, Guid FamilyId, string Status, DateTime IssuedAt, DateTime IdleExpiresAt,
    DateTime AbsoluteExpiresAt, string? IpAddress, string? UserAgent, bool IsLive);
internal sealed record PermissionCatalogResponse(
    IReadOnlyCollection<PermissionGroupResponse> Groups, IReadOnlyCollection<PermissionItemResponse> Permissions);
internal sealed record PermissionGroupResponse(string Key, string Label);
internal sealed record PermissionItemResponse(string Key, string Label, string Resource);
internal sealed record RoleResponse(
    string Code, string Name, string? Description, string? Color, string Status,
    IReadOnlyList<string> Permissions, int UserCount, long Version);

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

// SQL Server datetime2 does not preserve DateTime.Kind. Every persisted timestamp in this system is UTC,
// so normalize at the single public JSON boundary instead of making each endpoint repair EF materialization.
internal sealed class UtcDateTimeJsonConverter : JsonConverter<DateTime>
{
    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.GetDateTime();

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
    {
        var utc = value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };
        writer.WriteStringValue(utc);
    }
}

/// <summary>Exposed so <c>WebApplicationFactory&lt;Program&gt;</c> can boot the host in tests.</summary>
public partial class Program
{
    // Strip a route constraint from a path segment ("{cartId:guid}" -> "{cartId}") so an ApiDescription's
    // RelativePath matches the OpenAPI document's path key. Source-generated so the pattern compiles once.
    [GeneratedRegex(@"\{([^:}]+):[^}]+\}")]
    private static partial Regex RouteConstraintRegex();
}
