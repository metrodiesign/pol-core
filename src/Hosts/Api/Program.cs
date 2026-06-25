using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.OpenApi;
using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure;
using BuildingBlocks.Infrastructure.Persistence;
using BuildingBlocks.Infrastructure.Vault;
using BuildingBlocks.Web;
using Admin.Application;
using Admin.Application.AssignTenant;
using Admin.Application.CreateRole;
using Admin.Application.CreateScopedAdmin;
using Admin.Application.DeleteRole;
using Admin.Application.RoleQueries;
using Admin.Application.SetAdminRoles;
using Admin.Application.SuspendAdmin;
using Admin.Application.UnassignTenant;
using Admin.Application.UpdateRole;
using Admin.Domain;
using Admin.Infrastructure;
using Cart.Application;
using Cart.Infrastructure;
using Checkout.Application;
using Checkout.Infrastructure;
using Mediator;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Orders.Application;
using Orders.Infrastructure;
using Payments.Application.CreatePaymentSession;
using Payments.Application.HandlePspWebhook;
using Payments.Application.StartRedirect;
using Payments.Domain;
using Payments.Infrastructure;
using Payments.Infrastructure.Psp;
using Products.Application;
using Products.Infrastructure;
using Tenant.Application.GetTenant;
using Tenant.Application.ProvisionTenant;
using Tenant.Infrastructure;
using Api;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.AddJsonConsoleLogging();

// Fail fast on captive scope/lifetime mistakes (TenantGuardBehavior + ITenantContext are Scoped, PLAN #7).
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
builder.Services.AddScoped(typeof(IPipelineBehavior<,>), typeof(TenantGuardBehavior<,>));

builder.Services.AddBuildingBlocksInfrastructure();

// The producer DbContext. The RLS session-context interceptor sets SESSION_CONTEXT('TenantId') at open.
var producerConnString = builder.Configuration.GetConnectionString("Producer");
builder.Services.AddDbContext<ProducerDbContext>((sp, opt) =>
    opt.UseSqlServer(producerConnString)
       .AddInterceptors(sp.GetRequiredService<SessionContextConnectionInterceptor>()));

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
builder.Services.AddTenantModule();

// Tenant provisioning runs cross-tenant under pol_admin (RLS bypass) via a SEPARATE keyed connection —
// the pol_app connection is RLS-blocked from writing another tenant's rows. Fail fast at boot if it is
// not configured: the whole admin provisioning surface depends on it.
var adminConnString = builder.Configuration.GetConnectionString("Admin")
    ?? throw new InvalidOperationException(
        "ConnectionStrings:Admin (pol_admin) is required for tenant provisioning. Set ConnectionStrings__Admin.");
// The committed appsettings.json ships an Admin string with a BLANK password (the real secret is injected
// at runtime). Outside Development, fail fast if that injection did not happen — otherwise the host boots
// and only the first /admin request discovers the missing credential. Development may use integrated auth.
if (!builder.Environment.IsDevelopment())
{
    ProvisioningGuards.RequireInjectedCredential(adminConnString, "Admin");
    // The admin BFF login is a confidential OIDC client: the client id + secret must be injected outside
    // Development (the committed config ships blanks/placeholders). A blank/placeholder secret means the
    // runtime secret was never injected — fail fast at boot rather than on the first login (REQ-8.1/8.2).
    ProvisioningGuards.RequireConfidentialClientId(builder.Configuration["Google:Oidc:ClientId"]);
    ProvisioningGuards.RequireConfidentialClientSecret(builder.Configuration["Google:Oidc:ClientSecret"]);
}
builder.Services.AddTenantAdminScope(adminConnString);

// Admin identity (control plane: AdminAccounts/assignments/audit). Its EF configs live in the producer
// schema but are control-plane (pol_admin only); resolution/provisioning run cross-tenant, so the seams
// bind to the same pol_admin keyed scope.
builder.Services.AddAdminModule();
builder.Services.AddAdminIdentity();

// Data Protection key ring for the admin OIDC handler (correlation/state/nonce cookies), persisted to the
// control-plane DataProtectionKeys table via the keyed pol_admin context (REQ-8, Tech #5). Lazy — no SQL at boot.
builder.Services.AddAdminDataProtection();

// Admin BFF session lifetime + cookie posture (REQ-3/5/7).
builder.Services.Configure<AdminSessionOptions>(builder.Configuration.GetSection(AdminSessionOptions.SectionName));

// Tenant identity from the authenticated principal (never from the URL — PLAN #4).
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ITenantContext, HttpTenantContext>();

// Real Google ID-token validation (issuer/audience/lifetime/email_verified/hosted-domain + RS256 against
// Google's JWKS via Authority). Google:Audiences maps each SPA's client id to its role: both SPAs
// authenticate here, and the validated audience becomes a role claim that the per-role authorization
// policies ("tenant"/"admin") gate on. See GoogleAuthenticationExtensions.
builder.Services.AddGoogleIdTokenAuthentication(builder.Configuration, builder.Environment);

// Admin BFF: confidential Google OIDC client (Authorization Code + PKCE) for the server-side admin login.
// Adds the "Google" OIDC + "oidc-noop" sign-in schemes WITHOUT changing the default (JwtBearer stays for tenant).
builder.Services.Configure<AdminOidcOptions>(builder.Configuration.GetSection(AdminOidcOptions.SectionName));
builder.Services.AddAdminOidcAuthentication(builder.Configuration, builder.Environment);

// Admin BFF session scheme: authenticate every /admin/* request via the __Host-adm_session cookie and
// REDEFINE the "admin" authorization policy to pin it — retiring the Bearer "admin" audience (REQ-4/5/9/10).
builder.Services.AddAdminSessionScheme();

// Background sweep: delete sessions past their absolute expiry so the store does not grow unbounded (REQ-11.5).
builder.Services.AddHostedService<AdminSessionPruneService>();

// CORS for the separate browser SPA frontends (both allowlisted origins from Cors:AllowedOrigins).
builder.Services.AddPolCors(builder.Configuration);

// OpenAPI document so the SPA teams have a machine-readable contract (served in Development only). The
// document also declares the two auth schemes (tenant Bearer + admin session cookie) and tags each
// operation with the scheme its authorization policy requires, so other teams can authenticate straight
// from the Scalar reference UI.
builder.Services.AddOpenApi(options =>
{
    options.AddSchemaTransformer((schema, context, _) =>
    {
        // PspCode has a custom JsonConverter the schema generator can't introspect, so it would emit an
        // empty schema. Describe the real wire shape: the stable string codes from the PspCodes mapping.
        if (context.JsonTypeInfo.Type == typeof(PspCode))
        {
            schema.Type = JsonSchemaType.String;
            schema.Enum = Enum.GetValues<PspCode>().Select(p => (JsonNode)JsonValue.Create(p.ToCode())).ToList();
        }
        return Task.CompletedTask;
    });

    // Document-level: title/description + the security schemes other teams pick from Scalar's auth dropdown,
    // plus the per-operation security requirement each route's authorization policy implies.
    options.AddDocumentTransformer((document, context, _) =>
    {
        document.Info.Title = "pol-core API";
        document.Info.Version = "v1";
        document.Info.Description =
            "Captive payment orchestration (redirect-only, PCI SAQ A). Tenant storefront surface + Admin BFF.";

        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes["Bearer"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            In = ParameterLocation.Header,
            Description = "Google id-token of the authenticated tenant principal. Send `Authorization: Bearer <id_token>`.",
        };
        document.Components.SecuritySchemes["AdminSession"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey,
            In = ParameterLocation.Cookie,
            // Scalar/OpenAPI serve in Development only, where the default host is dev HTTP and the handler
            // writes the non-__Host cookie. Document that name, not the prod one, so admins testing in /scalar
            // see the cookie they actually have.
            Name = AdminSessionCookies.SessionCookieNameDevHttp,
            Description = "Admin BFF session cookie issued by the OIDC login flow (GET /admin/auth/login). "
                + "Set automatically in the browser. Production (HTTPS) uses the `__Host-adm_session` name.",
        };

        // Per-operation: attach the scheme each route's authorization policy requires so Scalar shows the right
        // auth on the right endpoint (tenant -> Bearer, admin -> AdminSession). The host document is passed so
        // the requirement serialises as a $ref into components.securitySchemes. Anonymous routes (order summary
        // link, admin login, webhook) carry no requirement.
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

// tenant routes gate on the "tenant" policy (Google Bearer); admin routes on "admin" (session cookie).
// AllowAnonymous endpoints and the unauthenticated webhook get no security requirement in the doc.
// Assumption: the only IAuthorizeData on an endpoint is the named policy from .RequireAuthorization(...).
// RequireAdminTier/RequirePermission use endpoint filters + WithMetadata (AdminHostWiring.cs), NOT
// IAuthorizeData, so exactly one non-empty policy is present and LastOrDefault is unambiguous.
static string? SecuritySchemeForEndpoint(IEnumerable<object> metadata)
{
    if (metadata.OfType<IAllowAnonymous>().Any())
        return null;
    var policy = metadata.OfType<IAuthorizeData>()
        .Select(a => a.Policy)
        .LastOrDefault(p => !string.IsNullOrEmpty(p));
    return policy switch
    {
        "tenant" => "Bearer",
        "admin" => "AdminSession",
        _ => null,
    };
}

// PspCode crosses the wire as its stable code ("2c2p"/"omise") via the domain's PspCodes mapping —
// not as an int or the C# member name. An unknown code fails body binding -> 400.
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new PspCodeJsonConverter()));

// Cross-cutting HTTP hardening: RFC7807 errors, split liveness/readiness probes, webhook flood protection.
builder.Services.AddProblemDetailsHandling();
builder.Services.AddReadinessHealthChecks();
builder.Services.AddWebhookRateLimiter();
builder.Services.AddAdminAuthRateLimiter();

var app = builder.Build();

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
// browser-facing host/scheme, not this process's. The admin SPA dev server proxies /admin/* here, so the OIDC
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

// Order matters: correlation id OUTERMOST so the logging scope is still active when the exception handler
// logs a failure (the scope is popped as the exception unwinds, so it must wrap UseExceptionHandler); the
// exception handler then wraps auth + the endpoints; rate limiter before the mapped endpoints run.
app.UseCorrelationId();
app.UseExceptionHandler();
// Render framework-generated bare status codes (401/403, unmatched-route 404) as RFC7807 ProblemDetails
// too, so every error shares one body shape. AddProblemDetails() above supplies the writer.
app.UseStatusCodePages();

// CORS before auth so a browser preflight (OPTIONS) is answered without an auth challenge. The per-request
// policy (admin-credentialed on /admin/*, tenant default elsewhere) is chosen by PolCorsPolicyProvider.
app.UsePolCors();

app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

// TODO(producer): the platform tenant-user resolver (was TenantUserResolutionMiddleware in the Identity
// module) is removed pending the Producer module. Until it returns, tenant-SPA callers get no ambient tenant
// binding in production (the Development tenant_id shim still works) and no tenant_role claim.

// Admin resolution is no longer a middleware: AdminSessionAuthenticationHandler authenticates the
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
    // No preferred scheme: Scalar auto-selects each operation's own security (Bearer for tenant routes,
    // AdminSession for admin routes) instead of defaulting every endpoint to one.
    app.MapScalarApiReference(options => options.WithTitle("pol-core API"));
}

// Webhook = source of truth. Routed by the trusted PSP connection id (NOT tenant/PSP parsed from the
// URL before the signature is verified — security rules). The raw body + signature header are handed
// to the handler, which verifies -> claims idempotency -> fetches-to-confirm -> transitions -> enqueues
// PaymentPaid, all inside one transaction.
app.MapPost("/webhooks/{pspConnectionId:guid}", async (
    Guid pspConnectionId,
    HttpRequest request,
    IWebhookTenantResolver tenantResolver,
    ITenantScope tenantScope,
    IMediator mediator,
    CancellationToken ct) =>
{
    using var reader = new StreamReader(request.Body);
    var rawPayload = await reader.ReadToEndAsync(ct);
    var signature = request.Headers["X-Signature"].ToString();

    // Resolve the tenant from the trusted connection id BEFORE any tenant-scoped work, then bind it so
    // every query in the handler runs under the right RLS SESSION_CONTEXT. Unknown id -> 404 (no leak).
    var tenantId = await tenantResolver.ResolveTenantAsync(pspConnectionId, ct);
    if (tenantId is null)
        return Results.Problem(statusCode: StatusCodes.Status404NotFound);

    using var tenantBinding = tenantScope.Begin(tenantId.Value);

    var result = await mediator.Send(new HandlePspWebhookCommand(pspConnectionId, rawPayload, signature), ct);
    return result.Outcome == WebhookOutcome.Rejected
        ? Results.Problem(statusCode: StatusCodes.Status401Unauthorized)
        : Results.Ok(new WebhookResponse(result.Outcome.ToString()));
}).RequireRateLimiting(WebhookRateLimiting.PolicyName)
    .WithTags("Webhooks")
    .WithName("HandlePspWebhook")
    .WithSummary("PSP webhook callback")
    .WithDescription("Verify the PSP signature, claim idempotency, confirm the payment and emit PaymentPaid. Routed by the trusted connection id; unknown id -> 404, bad signature -> 401.")
    .Produces<WebhookResponse>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status429TooManyRequests);

// Tenant-facing convenience endpoints (tenant comes from the authenticated principal via ITenantContext).
app.MapPost("/products", async (
    CreateProductRequest body,
    ITenantContext tenant,
    IMediator mediator,
    CancellationToken ct) =>
{
    var id = await mediator.Send(
        new CreateProductCommand(tenant.TenantId, body.Name, body.PriceMinorUnits, body.Currency), ct);
    return TypedResults.Ok(new CreateProductResponse(id));
})
    // TODO(producer): re-add .RequireTenantRole(Finance, TenantAdmin) once the Producer role model returns (REQ-7.3)
    .RequireAuthorization("tenant")
    .WithTags("Products")
    .WithName("CreateProduct")
    .WithSummary("Create a product")
    .WithDescription("Create a catalog product for the authenticated tenant.")
    .Produces<CreateProductResponse>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status401Unauthorized);

// Cart — open, add/merge lines, review, adjust, clear. Tenant comes from the principal; the commands are
// ITenantScoped so RLS + the tenant guard confine every cart to the bound tenant.
app.MapPost("/carts", async (ITenantContext tenant, IMediator mediator, CancellationToken ct) =>
{
    var id = await mediator.Send(new CreateCartCommand(tenant.TenantId), ct);
    return TypedResults.Ok(new CreateCartResponse(id));
}).RequireAuthorization("tenant")
    .WithTags("Cart")
    .WithName("CreateCart")
    .WithSummary("Open a cart")
    .WithDescription("Open a new empty cart for the authenticated tenant.")
    .Produces<CreateCartResponse>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status401Unauthorized);

app.MapPost("/carts/{cartId:guid}/items", async (
    Guid cartId, AddItemToCartRequest body, ITenantContext tenant, IMediator mediator, CancellationToken ct) =>
{
    // The unit price is the catalog's, NEVER the client's: look the product up first and price the line
    // from it (the cart is "selected plans + quote", reference 2.4). Unknown/inactive product -> 400.
    var product = await mediator.Send(new GetProductByIdQuery(tenant.TenantId, body.ProductId), ct);
    if (product is null || !product.IsActive)
        return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Unknown or inactive product.");

    var result = await mediator.Send(new AddItemToCartCommand(
        cartId, tenant.TenantId, body.ProductId, body.Quantity, product.Price.MinorUnits, product.Price.Currency), ct);
    return Results.Ok(result);
}).RequireAuthorization("tenant")
    .WithTags("Cart")
    .WithName("AddCartItem")
    .WithSummary("Add an item to a cart")
    .WithDescription("Add a catalog product line to the cart, priced from the catalog. Unknown or inactive product -> 400.")
    .Produces<AddItemResult>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status401Unauthorized);

app.MapGet("/carts/{cartId:guid}", async (
    Guid cartId, ITenantContext tenant, IMediator mediator, CancellationToken ct) =>
{
    var view = await mediator.Send(new GetCartQuery(cartId, tenant.TenantId), ct);
    return view is null ? Results.NotFound() : Results.Ok(view);
}).RequireAuthorization("tenant")
    .WithTags("Cart")
    .WithName("GetCart")
    .WithSummary("Review a cart")
    .WithDescription("Return the cart with its lines and quoted subtotal. Unknown cart -> 404.")
    .Produces<CartView>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status401Unauthorized);

app.MapDelete("/carts/{cartId:guid}/items/{productId:guid}", async (
    Guid cartId, Guid productId, ITenantContext tenant, IMediator mediator, CancellationToken ct) =>
{
    var view = await mediator.Send(new RemoveItemFromCartCommand(cartId, tenant.TenantId, productId), ct);
    return TypedResults.Ok(view);
}).RequireAuthorization("tenant")
    .WithTags("Cart")
    .WithName("RemoveCartItem")
    .WithSummary("Remove a cart line")
    .WithDescription("Remove a product line from the cart and return the updated cart.")
    .Produces<CartView>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status401Unauthorized);

app.MapPut("/carts/{cartId:guid}/items/{productId:guid}", async (
    Guid cartId, Guid productId, SetCartItemQuantityRequest body, ITenantContext tenant, IMediator mediator, CancellationToken ct) =>
{
    var view = await mediator.Send(new SetCartItemQuantityCommand(cartId, tenant.TenantId, productId, body.Quantity), ct);
    return TypedResults.Ok(view);
}).RequireAuthorization("tenant")
    .WithTags("Cart")
    .WithName("SetCartItemQuantity")
    .WithSummary("Set a cart line quantity")
    .WithDescription("Adjust the quantity of a product line and return the updated cart.")
    .Produces<CartView>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status401Unauthorized);

app.MapPost("/carts/{cartId:guid}/clear", async (
    Guid cartId, ITenantContext tenant, IMediator mediator, CancellationToken ct) =>
{
    var view = await mediator.Send(new ClearCartCommand(cartId, tenant.TenantId), ct);
    return TypedResults.Ok(view);
}).RequireAuthorization("tenant")
    .WithTags("Cart")
    .WithName("ClearCart")
    .WithSummary("Clear a cart")
    .WithDescription("Remove every line from the cart and return the emptied cart.")
    .Produces<CartView>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status401Unauthorized);

// Checkout. Start prices the checkout from the CART's subtotal (never a client-supplied amount), captures
// an optional notification recipient, then Confirm emits CheckoutConfirmed -> Orders opens the order.
app.MapPost("/checkout", async (
    StartCheckoutRequest body, ITenantContext tenant, IMediator mediator, CancellationToken ct) =>
{
    var cart = await mediator.Send(new GetCartQuery(body.CartId, tenant.TenantId), ct);
    if (cart is null)
        return Results.NotFound();
    if (cart.SubtotalMinorUnits is not { } minorUnits || cart.SubtotalCurrency is not { } currency)
        return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Cannot check out an empty cart.");

    var result = await mediator.Send(
        new StartCheckoutCommand(tenant.TenantId, body.CartId, minorUnits, currency, body.Recipient), ct);
    return Results.Ok(result);
}).RequireAuthorization("tenant")
    .WithTags("Checkout")
    .WithName("StartCheckout")
    .WithSummary("Start checkout")
    .WithDescription("Price a checkout from the cart subtotal (never a client amount). Unknown cart -> 404, empty cart -> 400.")
    .Produces<StartCheckoutResult>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status401Unauthorized);

app.MapPost("/checkout/{checkoutSessionId:guid}/confirm", async (
    Guid checkoutSessionId, ITenantContext tenant, IMediator mediator, CancellationToken ct) =>
{
    var result = await mediator.Send(new ConfirmCheckoutCommand(checkoutSessionId, tenant.TenantId), ct);
    return Results.Ok(result);
}).RequireAuthorization("tenant")
    .WithTags("Checkout")
    .WithName("ConfirmCheckout")
    .WithSummary("Confirm checkout")
    .WithDescription("Confirm the checkout session, emitting CheckoutConfirmed so Orders opens the order.")
    .Produces<ConfirmCheckoutResult>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status401Unauthorized);

app.MapPost("/payment-sessions", async (
    CreatePaymentSessionRequest body,
    ITenantContext tenant,
    IMediator mediator,
    CancellationToken ct) =>
{
    var result = await mediator.Send(new CreatePaymentSessionCommand(
        body.OrderId, tenant.TenantId, body.AmountMinorUnits, body.Currency, body.Method, body.Psp), ct);
    return TypedResults.Ok(new CreatePaymentSessionResponse(result.PaymentSessionId));
})
    // TODO(producer): re-add .RequireTenantRole(Finance, TenantAdmin) once the Producer role model returns (REQ-7.3)
    .RequireAuthorization("tenant")
    .WithTags("Payments")
    .WithName("CreatePaymentSession")
    .WithSummary("Create a payment session")
    .WithDescription("Open a payment session for an order against the chosen method/PSP.")
    .Produces<CreatePaymentSessionResponse>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status401Unauthorized);

// Claims-then-charges redirect (PLAN #11). Tenant scoping is automatic: the command is ITenantScoped, so
// TenantGuardBehavior + RLS resolve the session for the authenticated tenant only. Errors flow through the
// shared ProblemDetails handler (not found -> 404, illegal state / concurrent claim -> 409).
app.MapPost("/payment-sessions/{paymentSessionId:guid}/redirect", async (
    Guid paymentSessionId,
    IMediator mediator,
    CancellationToken ct) =>
{
    var result = await mediator.Send(new StartRedirectCommand(paymentSessionId), ct);
    return TypedResults.Ok(new StartRedirectResponse(result.RedirectUrl));
})
    // TODO(producer): re-add .RequireTenantRole(Finance, TenantAdmin) once the Producer role model returns (REQ-7.3)
    .RequireAuthorization("tenant")
    .WithTags("Payments")
    .WithName("StartPaymentRedirect")
    .WithSummary("Start the PSP redirect")
    .WithDescription("Claim then charge: return the PSP redirect URL for the payment session. Not found -> 404, illegal/concurrent state -> 409.")
    .Produces<StartRedirectResponse>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status409Conflict)
    .ProducesProblem(StatusCodes.Status401Unauthorized);

// Order summary link. The customer opens it anonymously — the opaque token IS the capability, resolved on
// a bypass proc (no tenant binding). Unknown token -> 404; expired -> 410. A producer can resend (rotates
// the token + extends the TTL), which is tenant-scoped.
app.MapGet("/orders/{token}/summary", async (
    string token, IOrderSummaryReader reader, IClock clock, CancellationToken ct) =>
{
    var summary = await reader.GetByTokenAsync(token, ct);
    if (summary is null)
        return Results.NotFound();
    if (clock.UtcNow >= summary.ExpiresAt)
        return Results.Problem(statusCode: StatusCodes.Status410Gone, title: "This link has expired.");

    return Results.Ok(new OrderSummaryResponse(
        summary.OrderId, summary.AmountMinorUnits, summary.Currency, summary.Status, summary.PaymentSessionId));
}).AllowAnonymous()
    .WithTags("Orders")
    .WithName("GetOrderSummary")
    .WithSummary("Order summary by link")
    .WithDescription("Public capability link: the opaque token resolves the order summary anonymously. Unknown token -> 404, expired -> 410.")
    .Produces<OrderSummaryResponse>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status410Gone);

app.MapPost("/orders/{orderId:guid}/summary/resend", async (
    Guid orderId, ITenantContext tenant, IMediator mediator, CancellationToken ct) =>
{
    var result = await mediator.Send(new ResendOrderSummaryCommand(orderId, tenant.TenantId), ct);
    return Results.Ok(result);
}).RequireAuthorization("tenant")
    .WithTags("Orders")
    .WithName("ResendOrderSummary")
    .WithSummary("Resend the order summary link")
    .WithDescription("Rotate the order summary token and extend its TTL, returning the fresh link.")
    .Produces<ResendOrderSummaryResult>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status401Unauthorized);

// Reconciliation report: the bound tenant's orders grouped by status + currency (count + total).
app.MapGet("/reports/reconciliation", async (ITenantContext tenant, IMediator mediator, CancellationToken ct) =>
{
    var view = await mediator.Send(new GetReconciliationSummaryQuery(tenant.TenantId), ct);
    return TypedResults.Ok(view);
}).RequireAuthorization("tenant")
    .WithTags("Orders")
    .WithName("GetReconciliationReport")
    .WithSummary("Reconciliation report")
    .WithDescription("The bound tenant's orders grouped by status and currency (count + total).")
    .Produces<ReconciliationView>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status401Unauthorized);

// --- Admin BFF (/admin route group, REQ-1/7/10) ---
// One group binds the CSRF double-submit filter ONCE for the whole admin surface (the credentialed admin CORS
// policy is applied to /admin/* by PolCorsPolicyProvider). Per-endpoint authorization stays explicit: login is
// anonymous; every other route gates on the AdminSession "admin" policy. The CSRF filter exempts safe methods,
// so the login/callback GETs pass untouched.
var admin = app.MapGroup("/admin").AddEndpointFilter<AdminCsrfFilter>();

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
        [AdminOidcAuthentication.Scheme]);
})
.AllowAnonymous()
.RequireRateLimiting(AdminAuthRateLimiting.PolicyName)
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
    HttpContext http, IAdminSessionStore sessions, AdminSessionCookies cookies,
    IAdminAuthAuditWriter audit, IClock clock, CancellationToken ct) =>
{
    var token = cookies.ReadSessionToken(http);
    if (token is not null)
    {
        var session = await sessions.FindByTokenHashAsync(AdminSessionTokens.Hash(token), ct);
        if (session is not null)
        {
            await sessions.RevokeFamilyAsync(session.FamilyId, ct);
            audit.Append(AdminAuthAudit.For(AdminAuthEventType.Logout, http.TraceIdentifier, clock.UtcNow, session.AdminAccountId));
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
    HttpContext http, IAdminScope scope, IAdminSessionStore sessions, AdminSessionCookies cookies,
    IAdminAuthAuditWriter audit, IClock clock, CancellationToken ct) =>
{
    var adminId = scope.Current.AdminId;
    await sessions.RevokeAllForAdminAsync(adminId, ct);
    audit.Append(AdminAuthAudit.For(AdminAuthEventType.LogoutAll, http.TraceIdentifier, clock.UtcNow, adminId));
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

// Admin provisioning (reference 2.4). Cross-tenant, so NOT ITenantScoped — runs under pol_admin via the
// keyed admin scope. AdminSubject (sub claim) + correlation id (TraceIdentifier) are taken server-side,
// never from the body. Duplicate code -> ConflictException -> 409; bad input -> ArgumentException -> 400.
admin.MapPost("/tenants", async (
    ProvisionTenantRequest body,
    HttpContext http,
    IMediator mediator,
    CancellationToken ct) =>
{
    // The documented 2.4 body wraps tenant fields under "tenant"; non-secret PSP config rides alongside
    // "psp"/"secrets" and is captured verbatim via JsonExtensionData (reference 2.4 — config stored as-is).
    var t = body.Tenant ?? throw new ArgumentException("The 'tenant' object is required.");

    var command = new ProvisionTenantCommand(
        new TenantSpec(t.Code, t.DisplayName, t.LegalEntityId, t.Country, t.Currency,
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
        http.TraceIdentifier);

    var result = await mediator.Send(command, ct);
    return Results.Created($"/admin/tenants/{t.Code}", result);

    // Re-pack the captured overflow fields into a single JSON element for verbatim storage.
    static JsonElement? ToElement(IDictionary<string, JsonElement>? extra) =>
        extra is null || extra.Count == 0 ? null : JsonSerializer.SerializeToElement(extra);
})
    .RequireAuthorization("admin").RequireAdminTier(AdminTier.Super) // provisioning is Super-only (REQ-8.4)
    .WithTags("Admin Tenants")
    .WithName("ProvisionTenant")
    .WithSummary("Provision a tenant")
    .WithDescription("Super-only. Create a tenant with its PSP connections (secrets vaulted, config stored verbatim). Duplicate code -> 409, bad input -> 400.")
    .Produces<ProvisionTenantResult>(StatusCodes.Status201Created)
    .ProducesProblem(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status409Conflict)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status403Forbidden);

// Cross-tenant read routed through the IAdminQuery seam: a Scoped admin sees only its assigned tenants, a
// Super is unrestricted (REQ-8.5 / 7.1). Out-of-scope or unknown -> 404 (no existence leak).
admin.MapGet("/tenants/{code}", async (
    string code,
    IAdminQuery adminQuery,
    CancellationToken ct) =>
{
    var view = await adminQuery.GetTenantByCodeAsync(code, ct);
    return view is null
        ? Results.Problem(statusCode: StatusCodes.Status404NotFound)
        : Results.Ok(view);
}).RequireAuthorization("admin")
    .WithTags("Admin Tenants")
    .WithName("GetTenant")
    .WithSummary("Read a tenant by code")
    .WithDescription("Scoped admins see only assigned tenants; Super is unrestricted. Out-of-scope or unknown -> 404 (no existence leak).")
    .Produces<TenantView>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status401Unauthorized);

// TODO(producer): self-service registration (/me/registration, /registrations/complete) + admin approval
// (/admin/tenant-users/{subject}/approve) lived in the Identity module and are removed pending the Producer
// module rebuild. The admin approve endpoint's scoped-accessible check (REQ-8.5) returns with it.

// --- Admin identity foundation management (REQ-3..10) + SPA bootstrap (REQ-13) ---

// The Admin SPA reads its own resolved identity to render the right scope/navigation (REQ-13). adminId/tier/
// accessible come from the per-request IAdminScope the middleware materialized; a Super returns an
// unrestricted flag (never the full tenant list), a Scoped admin gets its assigned {id, code} pairs.
admin.MapGet("/me", async (IAdminScope scope, IAdminTenantDirectory tenants, CancellationToken ct) =>
{
    if (!scope.IsBound)
        return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Your admin account is not active.");

    var me = scope.Current;
    object accessible;
    if (me.Accessible.IsUnrestricted)
    {
        accessible = new { isUnrestricted = true };
    }
    else
    {
        var codes = await tenants.GetCodesByIdsAsync(me.Accessible.Tenants, ct);
        accessible = new
        {
            isUnrestricted = false,
            tenants = me.Accessible.Tenants.Select(id => new { id, code = codes.GetValueOrDefault(id) }).ToArray(),
        };
    }

    return Results.Ok(new
    {
        adminId = me.AdminId,
        email = me.Email,
        tier = me.Tier.ToString(),
        accessibleTenants = accessible,
        permissions = me.Permissions, // effective action permissions (admin-role-rbac REQ-9.1)
    });
}).RequireAuthorization("admin")
    .WithTags("Admin Auth")
    .WithName("GetAdminMe")
    .WithSummary("Resolve the current admin")
    .WithDescription("The SPA reads its own identity: tier, accessible tenants (or unrestricted), and effective permissions. Inactive account -> 403.")
    .Produces(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status403Forbidden)
    .ProducesProblem(StatusCodes.Status401Unauthorized);

// Super invites a Scoped admin by verified email; the subject binds on the invitee's first login (REQ-3.4).
admin.MapPost("/admins", async (
    CreateAdminRequest body, IAdminScope scope, HttpContext http, IMediator mediator, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(body.Email))
        throw new ArgumentException("Email is required.");
    var result = await mediator.Send(new CreateScopedAdminCommand(body.Email, scope.Current.AdminId, http.TraceIdentifier), ct);
    return Results.Created($"/admin/admins/{result.AdminId}", result);
}).RequireAuthorization("admin").RequireAdminTier(AdminTier.Super)
    .WithTags("Admin Admins")
    .WithName("CreateScopedAdmin")
    .WithSummary("Invite a scoped admin")
    .WithDescription("Super-only. Invite a Scoped admin by verified email; the subject binds on their first login. Missing email -> 400.")
    .Produces<CreateScopedAdminResult>(StatusCodes.Status201Created)
    .ProducesProblem(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status403Forbidden);

// Super assigns a tenant to a Scoped admin (REQ-4.1). Inactive/unknown tenant or duplicate -> 409.
admin.MapPost("/admins/{id:guid}/tenants", async (
    Guid id, AssignTenantRequest body, IAdminScope scope, HttpContext http, IMediator mediator, CancellationToken ct) =>
{
    var result = await mediator.Send(new AssignTenantCommand(id, body.TenantId, scope.Current.AdminId, http.TraceIdentifier), ct);
    return Results.Ok(result);
}).RequireAuthorization("admin").RequireAdminTier(AdminTier.Super)
    .WithTags("Admin Admins")
    .WithName("AssignTenantToAdmin")
    .WithSummary("Assign a tenant to an admin")
    .WithDescription("Super-only. Grant a Scoped admin access to a tenant. Inactive/unknown tenant or duplicate -> 409.")
    .Produces<AssignTenantResult>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status409Conflict)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status403Forbidden);

// Super unassigns a tenant — a hard delete of the assignment row (REQ-4.2). Unknown assignment -> 404.
admin.MapDelete("/admins/{id:guid}/tenants/{tenantId:guid}", async (
    Guid id, Guid tenantId, IAdminScope scope, HttpContext http, IMediator mediator, CancellationToken ct) =>
{
    await mediator.Send(new UnassignTenantCommand(id, tenantId, scope.Current.AdminId, http.TraceIdentifier), ct);
    return Results.NoContent();
}).RequireAuthorization("admin").RequireAdminTier(AdminTier.Super)
    .WithTags("Admin Admins")
    .WithName("UnassignTenantFromAdmin")
    .WithSummary("Unassign a tenant from an admin")
    .WithDescription("Super-only. Hard-delete the tenant assignment row. Unknown assignment -> 404.")
    .Produces(StatusCodes.Status204NoContent)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status403Forbidden);

// Super suspends another admin; suspending your OWN account is rejected so oversight is never locked out (REQ-8.2).
admin.MapPost("/admins/{id:guid}/suspend", async (
    Guid id, IAdminScope scope, HttpContext http, IMediator mediator, CancellationToken ct) =>
{
    if (id == scope.Current.AdminId)
        return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "An admin cannot suspend their own account.");
    await mediator.Send(new SuspendAdminCommand(id, scope.Current.AdminId, http.TraceIdentifier), ct);
    return Results.NoContent();
}).RequireAuthorization("admin").RequireAdminTier(AdminTier.Super)
    .WithTags("Admin Admins")
    .WithName("SuspendAdmin")
    .WithSummary("Suspend an admin")
    .WithDescription("Super-only. Suspend another admin; suspending your own account is rejected (403) so oversight is never locked out.")
    .Produces(StatusCodes.Status204NoContent)
    .ProducesProblem(StatusCodes.Status403Forbidden)
    .ProducesProblem(StatusCodes.Status401Unauthorized);

// --- Admin Role RBAC (admin-role-rbac) ---
// Orthogonal to AdminTier: roles grant ACTIONS. Reads need only an authenticated admin (REQ-6.4); mutations are
// gated on the user.roles permission, dogfooding RequirePermission (REQ-6.3). status crosses the wire as
// "active"/"inactive" via explicit projection — there is no global string-enum converter (B2).
static object RoleToWire(AdminRoleListItem r) => new
{
    code = r.Code,
    name = r.Name,
    description = r.Description,
    color = r.Color,
    status = r.Status == AdminRoleStatus.Active ? "active" : "inactive",
    permissions = r.PermissionKeys,
    userCount = r.UserCount,
};
// Strict: an unrecognized value (typo, blank, null) is a 400 — never a silent default to Active (B2).
static AdminRoleStatus ParseRoleStatus(string? status) => status?.ToLowerInvariant() switch
{
    "active" => AdminRoleStatus.Active,
    "inactive" => AdminRoleStatus.Inactive,
    _ => throw new ArgumentException($"Invalid role status '{status}'. Expected 'active' or 'inactive'."),
};

// Permission catalog for the matrix (REQ-1.5): resource = the permission's group key.
admin.MapGet("/permissions", async (IMediator mediator, CancellationToken ct) =>
{
    var catalog = await mediator.Send(new ListPermissionsQuery(), ct);
    return Results.Ok(new
    {
        groups = catalog.Groups.Select(g => new { key = g.Key, label = g.LabelTh }),
        permissions = catalog.Permissions.Select(p => new { key = p.Key, label = p.LabelTh, resource = p.Resource }),
    });
}).RequireAuthorization("admin")
    .WithTags("Admin Roles")
    .WithName("ListPermissions")
    .WithSummary("Permission catalog")
    .WithDescription("The permission/group catalog backing the role matrix (resource = the permission's group key).")
    .Produces(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status401Unauthorized);

admin.MapGet("/roles", async (IMediator mediator, CancellationToken ct) =>
    Results.Ok((await mediator.Send(new ListRolesQuery(), ct)).Select(RoleToWire)))
    .RequireAuthorization("admin")
    .WithTags("Admin Roles")
    .WithName("ListRoles")
    .WithSummary("List roles")
    .WithDescription("All admin roles with their permissions and bound-user counts.")
    .Produces(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status401Unauthorized);

admin.MapGet("/roles/{code}", async (string code, IMediator mediator, CancellationToken ct) =>
{
    var role = await mediator.Send(new GetRoleQuery(code), ct);
    return role is null ? Results.Problem(statusCode: StatusCodes.Status404NotFound) : Results.Ok(RoleToWire(role));
}).RequireAuthorization("admin")
    .WithTags("Admin Roles")
    .WithName("GetRole")
    .WithSummary("Read a role by code")
    .WithDescription("Return a single role with its permissions. Unknown code -> 404.")
    .Produces(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status401Unauthorized);

// Create: duplicate code -> 409; permission key outside catalog -> 400 (REQ-2.3/3.3).
admin.MapPost("/roles", async (
    CreateRoleRequest body, IAdminScope scope, HttpContext http, IMediator mediator, CancellationToken ct) =>
{
    var result = await mediator.Send(new CreateRoleCommand(
        body.Code ?? "", body.Name ?? "", body.Description, body.Color, ParseRoleStatus(body.Status),
        body.Permissions ?? [], scope.Current.AdminId, http.TraceIdentifier), ct);
    return Results.Created($"/admin/roles/{result.Code}", RoleToWire(result));
}).RequireAuthorization("admin").RequirePermission(AdminPermissions.UserRoles)
    .WithTags("Admin Roles")
    .WithName("CreateRole")
    .WithSummary("Create a role")
    .WithDescription("Requires the user.roles permission. Duplicate code -> 409; permission key outside the catalog -> 400.")
    .Produces(StatusCodes.Status201Created)
    .ProducesProblem(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status409Conflict)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status403Forbidden);

// Update: code is immutable (taken from the route, never the body); deactivating super_admin -> 409 (REQ-2.4/8.3).
admin.MapPut("/roles/{code}", async (
    string code, UpdateRoleRequest body, IAdminScope scope, HttpContext http, IMediator mediator, CancellationToken ct) =>
{
    var result = await mediator.Send(new UpdateRoleCommand(
        code, body.Name ?? "", body.Description, body.Color, ParseRoleStatus(body.Status),
        body.Permissions ?? [], scope.Current.AdminId, http.TraceIdentifier), ct);
    return Results.Ok(RoleToWire(result));
}).RequireAuthorization("admin").RequirePermission(AdminPermissions.UserRoles)
    .WithTags("Admin Roles")
    .WithName("UpdateRole")
    .WithSummary("Update a role")
    .WithDescription("Requires the user.roles permission. Code is immutable (from the route); deactivating super_admin -> 409.")
    .Produces(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status409Conflict)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status403Forbidden);

// Delete: a role with bound users is undeletable (409, REQ-4.4).
admin.MapDelete("/roles/{code}", async (
    string code, IAdminScope scope, HttpContext http, IMediator mediator, CancellationToken ct) =>
{
    await mediator.Send(new DeleteRoleCommand(code, scope.Current.AdminId, http.TraceIdentifier), ct);
    return Results.NoContent();
}).RequireAuthorization("admin").RequirePermission(AdminPermissions.UserRoles)
    .WithTags("Admin Roles")
    .WithName("DeleteRole")
    .WithSummary("Delete a role")
    .WithDescription("Requires the user.roles permission. A role with bound users is undeletable -> 409.")
    .Produces(StatusCodes.Status204NoContent)
    .ProducesProblem(StatusCodes.Status409Conflict)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status403Forbidden);

// Set an admin's roles to exactly the given set (REQ-4.2). Unknown role code -> 400; unknown admin -> 404.
admin.MapPut("/admins/{id:guid}/roles", async (
    Guid id, SetAdminRolesRequest body, IAdminScope scope, HttpContext http, IMediator mediator, CancellationToken ct) =>
{
    await mediator.Send(new SetAdminRolesCommand(id, body.RoleCodes ?? [], scope.Current.AdminId, http.TraceIdentifier), ct);
    return Results.NoContent();
}).RequireAuthorization("admin").RequirePermission(AdminPermissions.UserRoles)
    .WithTags("Admin Admins")
    .WithName("SetAdminRoles")
    .WithSummary("Set an admin's roles")
    .WithDescription("Requires the user.roles permission. Replace an admin's roles with exactly the given set. Unknown role code -> 400; unknown admin -> 404.")
    .Produces(StatusCodes.Status204NoContent)
    .ProducesProblem(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status403Forbidden);

// admin-role-rbac REQ-11: fail fast at boot if any RequirePermission gate references a key absent from the catalog.
AdminPermissionParity.Assert(app.Services);

app.Run();

internal sealed record CreateRoleRequest(
    string? Code, string? Name, string? Description, string? Color, string? Status, IReadOnlyList<string>? Permissions);
internal sealed record UpdateRoleRequest(
    string? Name, string? Description, string? Color, string? Status, IReadOnlyList<string>? Permissions);
internal sealed record SetAdminRolesRequest(IReadOnlyList<string>? RoleCodes);

internal sealed record CreateProductRequest(string Name, long PriceMinorUnits, string Currency);
internal sealed record CreatePaymentSessionRequest(
    Guid OrderId, long AmountMinorUnits, string Currency, string Method, PspCode Psp);
internal sealed record AddItemToCartRequest(Guid ProductId, int Quantity);
internal sealed record SetCartItemQuantityRequest(int Quantity);
internal sealed record CreateCartResponse(Guid CartId);
internal sealed record StartCheckoutRequest(Guid CartId, string? Recipient);
internal sealed record OrderSummaryResponse(
    Guid OrderId, long AmountMinorUnits, string Currency, string Status, Guid? PaymentSessionId);

// Admin provisioning request body (reference 2.4): { "tenant": { ... }, "pspConnections": [ ... ] }.
// AdminSubject + correlation id are NOT in the body — the host sets them from the authenticated request.
internal sealed record ProvisionTenantRequest(
    ProvisionTenantBody? Tenant,
    IReadOnlyList<ProvisionPspConnectionRequest>? PspConnections);

// Tenant scalars are first-class columns; every other key under "tenant" (branding/routing/session/
// timezone/locale/...) is captured by JsonExtensionData and stored verbatim in the tenant Metadata.
internal sealed class ProvisionTenantBody
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
}

// Admin identity foundation request bodies (REQ-3/4). ActingAdminId + correlation id are NOT in the body —
// the host sets them from the resolved IAdminScope + the authenticated request.
internal sealed record CreateAdminRequest(string Email);
internal sealed record AssignTenantRequest(Guid TenantId);

internal sealed record CreateProductResponse(Guid ProductId);
internal sealed record CreatePaymentSessionResponse(Guid PaymentSessionId);
internal sealed record StartRedirectResponse(string RedirectUrl);
internal sealed record WebhookResponse(string Outcome);

// Bridges PspCode <-> its stable wire code via the domain's single-source-of-truth PspCodes mapping,
// so the host owns the serialization concern and the domain enum stays attribute-free.
internal sealed class PspCodeJsonConverter : JsonConverter<PspCode>
{
    public override PspCode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var code = reader.GetString() ?? throw new JsonException("psp must be a string code.");
        try { return PspCodes.FromCode(code); }
        catch (ArgumentException ex) { throw new JsonException(ex.Message); } // unknown code -> 400, not 500
    }

    public override void Write(Utf8JsonWriter writer, PspCode value, JsonSerializerOptions options) =>
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
