using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.OpenApi;
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
}).RequireAuthorization("tenant"); // tenant-SPA audience only (admin-SPA tokens get a different role)

app.MapPost("/payment-sessions", async (
    CreatePaymentSessionRequest body,
    ITenantContext tenant,
    IMediator mediator,
    CancellationToken ct) =>
{
    var result = await mediator.Send(new CreatePaymentSessionCommand(
        body.OrderId, tenant.TenantId, body.AmountMinorUnits, body.Currency, body.Method, body.Psp), ct);
    return TypedResults.Ok(new CreatePaymentSessionResponse(result.PaymentSessionId));
}).RequireAuthorization("tenant"); // tenant-SPA audience only (admin-SPA tokens get a different role)

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
}).RequireAuthorization("tenant"); // tenant-SPA audience only (admin-SPA tokens get a different role)

app.Run();

internal sealed record CreateProductRequest(string Name, long PriceMinorUnits, string Currency);
internal sealed record CreatePaymentSessionRequest(
    Guid OrderId, long AmountMinorUnits, string Currency, string Method, PspCode Psp);

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
