using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure;
using BuildingBlocks.Infrastructure.Persistence;
using BuildingBlocks.Infrastructure.Vault;
using BuildingBlocks.Web;
using Cart.Infrastructure;
using Checkout.Infrastructure;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Orders.Infrastructure;
using Payments.Application.CreatePaymentSession;
using Payments.Application.HandlePspWebhook;
using Payments.Application.StartRedirect;
using Payments.Domain;
using Payments.Infrastructure;
using Payments.Infrastructure.Psp;
using Products.Application;
using Products.Infrastructure;
using TenantConsole;

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

// Shared DbContexts. The RLS session-context interceptor sets SESSION_CONTEXT('TenantId') at open.
var producerConnString = builder.Configuration.GetConnectionString("Producer");
var adminConnString = builder.Configuration.GetConnectionString("Admin");
builder.Services.AddDbContext<ProducerDbContext>((sp, opt) =>
    opt.UseSqlServer(producerConnString)
       .AddInterceptors(sp.GetRequiredService<SessionContextConnectionInterceptor>()));
builder.Services.AddDbContext<AdminDbContext>((sp, opt) =>
    opt.UseSqlServer(adminConnString)
       .AddInterceptors(sp.GetRequiredService<SessionContextConnectionInterceptor>()));

// Module entity configurations are discovered from these assemblies at model-build time.
builder.Services.AddSingleton(new ModuleAssemblies(HostModuleAssemblies.All, HostModuleAssemblies.Admin));

builder.Services.Configure<VaultOptions>(builder.Configuration.GetSection(VaultOptions.SectionName));
// Non-secret PSP endpoint/environment config for the real 2C2P + Omise adapters (UseSandbox defaults true).
builder.Services.Configure<PspOptions>(builder.Configuration.GetSection(PspOptions.SectionName));

builder.Services.AddProductsModule();
builder.Services.AddCartModule();
builder.Services.AddCheckoutModule();
builder.Services.AddOrdersModule();
builder.Services.AddPaymentsModule();

// Tenant identity from the authenticated principal (never from the URL — PLAN #4).
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ITenantContext, HttpTenantContext>();

// Real Google ID-token validation (issuer/audience/lifetime/email_verified/hosted-domain + RS256 against
// Google's JWKS via Authority), shared with AdminConsole. See BuildingBlocks.Web.GoogleAuthenticationExtensions.
builder.Services.AddGoogleIdTokenAuthentication(builder.Configuration, builder.Environment);

// Cross-cutting HTTP hardening: RFC7807 errors, split liveness/readiness probes, webhook flood protection.
builder.Services.AddProblemDetailsHandling();
builder.Services.AddReadinessHealthChecks();
builder.Services.AddWebhookRateLimiter();

var app = builder.Build();

// Order matters: correlation id OUTERMOST so the logging scope is still active when the exception handler
// logs a failure (the scope is popped as the exception unwinds, so it must wrap UseExceptionHandler); the
// exception handler then wraps auth + the endpoints; rate limiter before the mapped endpoints run.
app.UseCorrelationId();
app.UseExceptionHandler();

app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

// Liveness (process only) + readiness (DB + vault), anonymous, minimal body — no topology leak.
app.MapPolHealthChecks();

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
        return Results.NotFound();

    using var tenantBinding = tenantScope.Begin(tenantId.Value);

    var result = await mediator.Send(new HandlePspWebhookCommand(pspConnectionId, rawPayload, signature), ct);
    return result.Outcome == WebhookOutcome.Rejected
        ? Results.Unauthorized()
        : Results.Ok(new { outcome = result.Outcome.ToString() });
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
    return Results.Ok(new { productId = id });
}).RequireAuthorization();

app.MapPost("/payment-sessions", async (
    CreatePaymentSessionRequest body,
    ITenantContext tenant,
    IMediator mediator,
    CancellationToken ct) =>
{
    var result = await mediator.Send(new CreatePaymentSessionCommand(
        body.OrderId, tenant.TenantId, body.AmountMinorUnits, body.Currency, body.Method, body.Psp), ct);
    return Results.Ok(new { paymentSessionId = result.PaymentSessionId });
}).RequireAuthorization();

// Claims-then-charges redirect (PLAN #11). Tenant scoping is automatic: the command is ITenantScoped, so
// TenantGuardBehavior + RLS resolve the session for the authenticated tenant only. Errors flow through the
// shared ProblemDetails handler (not found -> 404, illegal state / concurrent claim -> 409).
app.MapPost("/payment-sessions/{paymentSessionId:guid}/redirect", async (
    Guid paymentSessionId,
    IMediator mediator,
    CancellationToken ct) =>
{
    var result = await mediator.Send(new StartRedirectCommand(paymentSessionId), ct);
    return Results.Ok(new { redirectUrl = result.RedirectUrl });
}).RequireAuthorization();

app.Run();

internal sealed record CreateProductRequest(string Name, long PriceMinorUnits, string Currency);

internal sealed record CreatePaymentSessionRequest(
    Guid OrderId, long AmountMinorUnits, string Currency, string Method, PspCode Psp);

/// <summary>Exposed so <c>WebApplicationFactory&lt;Program&gt;</c> can boot the host in tests.</summary>
public partial class Program;
