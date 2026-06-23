using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.OpenApi;
using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure;
using BuildingBlocks.Infrastructure.Persistence;
using BuildingBlocks.Infrastructure.Vault;
using BuildingBlocks.Web;
using Admin.Application;
using Admin.Application.AssignTenant;
using Admin.Application.CreateScopedAdmin;
using Admin.Application.SuspendAdmin;
using Admin.Application.UnassignTenant;
using Admin.Domain;
using Admin.Infrastructure;
using Cart.Application;
using Cart.Infrastructure;
using Checkout.Application;
using Checkout.Infrastructure;
using Mediator;
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
// Same fail-fast for the admin SPA audience: the /admin routes gate on the "admin" authorization policy,
// which GoogleAuthenticationExtensions registers ONLY when Google:Audiences:admin is mapped — without it an
// admin request hits a missing policy (500) instead of 401/403.
if (!builder.Environment.IsDevelopment())
{
    ProvisioningGuards.RequireInjectedCredential(adminConnString, "Admin");
    ProvisioningGuards.RequireAdminAudience(builder.Configuration["Google:Audiences:admin"]);
}
builder.Services.AddTenantAdminScope(adminConnString);

// Admin identity (control plane: AdminAccounts/assignments/audit). Its EF configs live in the producer
// schema but are control-plane (pol_admin only); resolution/provisioning run cross-tenant, so the seams
// bind to the same pol_admin keyed scope.
builder.Services.AddAdminModule();
builder.Services.AddAdminIdentity();

// Tenant identity from the authenticated principal (never from the URL — PLAN #4).
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ITenantContext, HttpTenantContext>();

// Real Google ID-token validation (issuer/audience/lifetime/email_verified/hosted-domain + RS256 against
// Google's JWKS via Authority). Google:Audiences maps each SPA's client id to its role: both SPAs
// authenticate here, and the validated audience becomes a role claim that the per-role authorization
// policies ("tenant"/"admin") gate on. See GoogleAuthenticationExtensions.
builder.Services.AddGoogleIdTokenAuthentication(builder.Configuration, builder.Environment);

// CORS for the separate browser SPA frontends (both allowlisted origins from Cors:AllowedOrigins).
builder.Services.AddPolCors(builder.Configuration);

// OpenAPI document so the SPA teams have a machine-readable contract (served in Development only).
builder.Services.AddOpenApi(options => options.AddSchemaTransformer((schema, context, _) =>
{
    // PspCode has a custom JsonConverter the schema generator can't introspect, so it would emit an
    // empty schema. Describe the real wire shape: the stable string codes from the PspCodes mapping.
    if (context.JsonTypeInfo.Type == typeof(PspCode))
    {
        schema.Type = JsonSchemaType.String;
        schema.Enum = Enum.GetValues<PspCode>().Select(p => (JsonNode)JsonValue.Create(p.ToCode())).ToList();
    }
    return Task.CompletedTask;
}));

// PspCode crosses the wire as its stable code ("2c2p"/"omise") via the domain's PspCodes mapping —
// not as an int or the C# member name. An unknown code fails body binding -> 400.
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new PspCodeJsonConverter()));

// Cross-cutting HTTP hardening: RFC7807 errors, split liveness/readiness probes, webhook flood protection.
builder.Services.AddProblemDetailsHandling();
builder.Services.AddReadinessHealthChecks();
builder.Services.AddWebhookRateLimiter();

var app = builder.Build();

// Fail-fast: build the vault keyring now so a missing/short/invalid master key crash-loops the host at
// boot instead of surfacing only on the first reveal. ValidateOnBuild does NOT run factory-registered
// singletons, so this explicit resolve is what delivers the boot-time custody guarantee.
_ = app.Services.GetRequiredService<VaultKeyring>();

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

// Order matters: correlation id OUTERMOST so the logging scope is still active when the exception handler
// logs a failure (the scope is popped as the exception unwinds, so it must wrap UseExceptionHandler); the
// exception handler then wraps auth + the endpoints; rate limiter before the mapped endpoints run.
app.UseCorrelationId();
app.UseExceptionHandler();
// Render framework-generated bare status codes (401/403, unmatched-route 404) as RFC7807 ProblemDetails
// too, so every error shares one body shape. AddProblemDetails() above supplies the writer.
app.UseStatusCodePages();

// CORS before auth so a browser preflight (OPTIONS) is answered without an auth challenge.
app.UsePolCors();

app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

// TODO(producer): the platform tenant-user resolver (was TenantUserResolutionMiddleware in the Identity
// module) is removed pending the Producer module. Until it returns, tenant-SPA callers get no ambient tenant
// binding in production (the Development tenant_id shim still works) and no tenant_role claim.

// Resolve the platform admin for an admin-SPA caller (REQ-5/6): bootstrap/bind on first login, materialize
// the accessible-tenant set + admin_tier claim into IAdminScope, deny (403) an unresolvable or suspended
// admin. Runs alongside the tenant resolver (the two are exclusive by the "admin"/"tenant" role claim).
app.UseMiddleware<AdminResolutionMiddleware>();

// Liveness (process only) + readiness (DB + vault), anonymous, minimal body — no topology leak.
app.MapPolHealthChecks();

// The API contract for the SPA teams. Development only — the prod contract is not published publicly.
if (app.Environment.IsDevelopment())
    app.MapOpenApi();

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
}).RequireRateLimiting(WebhookRateLimiting.PolicyName);

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
}).RequireAuthorization("tenant"); // TODO(producer): re-add .RequireTenantRole(Finance, TenantAdmin) once the Producer role model returns (REQ-7.3)

// Cart — open, add/merge lines, review, adjust, clear. Tenant comes from the principal; the commands are
// ITenantScoped so RLS + the tenant guard confine every cart to the bound tenant.
app.MapPost("/carts", async (ITenantContext tenant, IMediator mediator, CancellationToken ct) =>
{
    var id = await mediator.Send(new CreateCartCommand(tenant.TenantId), ct);
    return TypedResults.Ok(new CreateCartResponse(id));
}).RequireAuthorization("tenant");

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
}).RequireAuthorization("tenant");

app.MapGet("/carts/{cartId:guid}", async (
    Guid cartId, ITenantContext tenant, IMediator mediator, CancellationToken ct) =>
{
    var view = await mediator.Send(new GetCartQuery(cartId, tenant.TenantId), ct);
    return view is null ? Results.NotFound() : Results.Ok(view);
}).RequireAuthorization("tenant");

app.MapDelete("/carts/{cartId:guid}/items/{productId:guid}", async (
    Guid cartId, Guid productId, ITenantContext tenant, IMediator mediator, CancellationToken ct) =>
{
    var view = await mediator.Send(new RemoveItemFromCartCommand(cartId, tenant.TenantId, productId), ct);
    return TypedResults.Ok(view);
}).RequireAuthorization("tenant");

app.MapPut("/carts/{cartId:guid}/items/{productId:guid}", async (
    Guid cartId, Guid productId, SetCartItemQuantityRequest body, ITenantContext tenant, IMediator mediator, CancellationToken ct) =>
{
    var view = await mediator.Send(new SetCartItemQuantityCommand(cartId, tenant.TenantId, productId, body.Quantity), ct);
    return TypedResults.Ok(view);
}).RequireAuthorization("tenant");

app.MapPost("/carts/{cartId:guid}/clear", async (
    Guid cartId, ITenantContext tenant, IMediator mediator, CancellationToken ct) =>
{
    var view = await mediator.Send(new ClearCartCommand(cartId, tenant.TenantId), ct);
    return TypedResults.Ok(view);
}).RequireAuthorization("tenant");

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
}).RequireAuthorization("tenant");

app.MapPost("/checkout/{checkoutSessionId:guid}/confirm", async (
    Guid checkoutSessionId, ITenantContext tenant, IMediator mediator, CancellationToken ct) =>
{
    var result = await mediator.Send(new ConfirmCheckoutCommand(checkoutSessionId, tenant.TenantId), ct);
    return Results.Ok(result);
}).RequireAuthorization("tenant");

app.MapPost("/payment-sessions", async (
    CreatePaymentSessionRequest body,
    ITenantContext tenant,
    IMediator mediator,
    CancellationToken ct) =>
{
    var result = await mediator.Send(new CreatePaymentSessionCommand(
        body.OrderId, tenant.TenantId, body.AmountMinorUnits, body.Currency, body.Method, body.Psp), ct);
    return TypedResults.Ok(new CreatePaymentSessionResponse(result.PaymentSessionId));
}).RequireAuthorization("tenant"); // TODO(producer): re-add .RequireTenantRole(Finance, TenantAdmin) once the Producer role model returns (REQ-7.3)

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
}).RequireAuthorization("tenant"); // TODO(producer): re-add .RequireTenantRole(Finance, TenantAdmin) once the Producer role model returns (REQ-7.3)

// Order summary link. The customer opens it anonymously — the opaque token IS the capability, resolved on
// a bypass proc (no tenant binding). Unknown token -> 404; expired -> 410. A producer can resend (rotates
// the token + extends the TTL), which is tenant-scoped.
app.MapGet("/orders/{token}/summary", async (
    string token, IOrderSummaryReader reader, IClock clock, CancellationToken ct) =>
{
    var summary = await reader.GetByTokenAsync(token, ct);
    if (summary is null)
        return Results.NotFound();
    if (clock.UtcNow >= summary.ExpiresAtUtc)
        return Results.Problem(statusCode: StatusCodes.Status410Gone, title: "This link has expired.");

    return Results.Ok(new OrderSummaryResponse(
        summary.OrderId, summary.AmountMinorUnits, summary.Currency, summary.Status, summary.PaymentSessionId));
}).AllowAnonymous();

app.MapPost("/orders/{orderId:guid}/summary/resend", async (
    Guid orderId, ITenantContext tenant, IMediator mediator, CancellationToken ct) =>
{
    var result = await mediator.Send(new ResendOrderSummaryCommand(orderId, tenant.TenantId), ct);
    return Results.Ok(result);
}).RequireAuthorization("tenant");

// Reconciliation report: the bound tenant's orders grouped by status + currency (count + total).
app.MapGet("/reports/reconciliation", async (ITenantContext tenant, IMediator mediator, CancellationToken ct) =>
{
    var view = await mediator.Send(new GetReconciliationSummaryQuery(tenant.TenantId), ct);
    return TypedResults.Ok(view);
}).RequireAuthorization("tenant");

// Admin provisioning (reference 2.4). Cross-tenant, so NOT ITenantScoped — runs under pol_admin via the
// keyed admin scope. AdminSubject (sub claim) + correlation id (TraceIdentifier) are taken server-side,
// never from the body. Duplicate code -> ConflictException -> 409; bad input -> ArgumentException -> 400.
app.MapPost("/admin/tenants", async (
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
}).RequireAuthorization("admin").RequireAdminTier(AdminTier.Super); // provisioning is Super-only (REQ-8.4)

// Cross-tenant read routed through the IAdminQuery seam: a Scoped admin sees only its assigned tenants, a
// Super is unrestricted (REQ-8.5 / 7.1). Out-of-scope or unknown -> 404 (no existence leak).
app.MapGet("/admin/tenants/{code}", async (
    string code,
    IAdminQuery adminQuery,
    CancellationToken ct) =>
{
    var view = await adminQuery.GetTenantByCodeAsync(code, ct);
    return view is null
        ? Results.Problem(statusCode: StatusCodes.Status404NotFound)
        : Results.Ok(view);
}).RequireAuthorization("admin");

// TODO(producer): self-service registration (/me/registration, /registrations/complete) + admin approval
// (/admin/tenant-users/{subject}/approve) lived in the Identity module and are removed pending the Producer
// module rebuild. The admin approve endpoint's scoped-accessible check (REQ-8.5) returns with it.

// --- Admin identity foundation management (REQ-3..10) + SPA bootstrap (REQ-13) ---

// The Admin SPA reads its own resolved identity to render the right scope/navigation (REQ-13). adminId/tier/
// accessible come from the per-request IAdminScope the middleware materialized; a Super returns an
// unrestricted flag (never the full tenant list), a Scoped admin gets its assigned {id, code} pairs.
app.MapGet("/admin/me", async (IAdminScope scope, IAdminTenantDirectory tenants, CancellationToken ct) =>
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
    });
}).RequireAuthorization("admin");

// Super invites a Scoped admin by verified email; the subject binds on the invitee's first login (REQ-3.4).
app.MapPost("/admin/admins", async (
    CreateAdminRequest body, IAdminScope scope, HttpContext http, IMediator mediator, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(body.Email))
        throw new ArgumentException("Email is required.");
    var result = await mediator.Send(new CreateScopedAdminCommand(body.Email, scope.Current.AdminId, http.TraceIdentifier), ct);
    return Results.Created($"/admin/admins/{result.AdminId}", result);
}).RequireAuthorization("admin").RequireAdminTier(AdminTier.Super);

// Super assigns a tenant to a Scoped admin (REQ-4.1). Inactive/unknown tenant or duplicate -> 409.
app.MapPost("/admin/admins/{id:guid}/tenants", async (
    Guid id, AssignTenantRequest body, IAdminScope scope, HttpContext http, IMediator mediator, CancellationToken ct) =>
{
    var result = await mediator.Send(new AssignTenantCommand(id, body.TenantId, scope.Current.AdminId, http.TraceIdentifier), ct);
    return Results.Ok(result);
}).RequireAuthorization("admin").RequireAdminTier(AdminTier.Super);

// Super unassigns a tenant — a hard delete of the assignment row (REQ-4.2). Unknown assignment -> 404.
app.MapDelete("/admin/admins/{id:guid}/tenants/{tenantId:guid}", async (
    Guid id, Guid tenantId, IAdminScope scope, HttpContext http, IMediator mediator, CancellationToken ct) =>
{
    await mediator.Send(new UnassignTenantCommand(id, tenantId, scope.Current.AdminId, http.TraceIdentifier), ct);
    return Results.NoContent();
}).RequireAuthorization("admin").RequireAdminTier(AdminTier.Super);

// Super suspends another admin; suspending your OWN account is rejected so oversight is never locked out (REQ-8.2).
app.MapPost("/admin/admins/{id:guid}/suspend", async (
    Guid id, IAdminScope scope, HttpContext http, IMediator mediator, CancellationToken ct) =>
{
    if (id == scope.Current.AdminId)
        return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "An admin cannot suspend their own account.");
    await mediator.Send(new SuspendAdminCommand(id, scope.Current.AdminId, http.TraceIdentifier), ct);
    return Results.NoContent();
}).RequireAuthorization("admin").RequireAdminTier(AdminTier.Super);

app.Run();

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

    /// <summary>Fails fast when the admin SPA audience is unmapped. The /admin routes gate on the "admin"
    /// authorization policy, which is registered only for a mapped audience — without it those routes would
    /// 500 on a missing policy instead of returning 401/403.</summary>
    public static void RequireAdminAudience(string? adminAudienceClientId)
    {
        if (string.IsNullOrWhiteSpace(adminAudienceClientId))
            throw new InvalidOperationException(
                "Google:Audiences:admin is required — the /admin routes gate on the \"admin\" policy. " +
                "Map it via Google__Audiences__admin.");
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
public partial class Program;
