using AdminConsole;
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
using Payments.Infrastructure;
using Products.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.AddJsonConsoleLogging();

// Fail fast on captive scope/lifetime mistakes (Scoped ITenantContext + pipeline behavior, PLAN #7).
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

// Shared DbContexts. The RLS session-context interceptor sets SESSION_CONTEXT at open; the admin
// principal is permitted to cross tenant boundaries (PLAN #3).
var producerConnString = builder.Configuration.GetConnectionString("Producer");
var adminConnString = builder.Configuration.GetConnectionString("Admin");
builder.Services.AddDbContext<ProducerDbContext>((sp, opt) =>
    opt.UseSqlServer(producerConnString)
       .AddInterceptors(sp.GetRequiredService<SessionContextConnectionInterceptor>()));
builder.Services.AddDbContext<AdminDbContext>((sp, opt) =>
    opt.UseSqlServer(adminConnString)
       .AddInterceptors(sp.GetRequiredService<SessionContextConnectionInterceptor>()));

builder.Services.AddSingleton(new ModuleAssemblies(HostModuleAssemblies.All, HostModuleAssemblies.Admin));

builder.Services.Configure<VaultOptions>(builder.Configuration.GetSection(VaultOptions.SectionName));

builder.Services.AddProductsModule();
builder.Services.AddCartModule();
builder.Services.AddCheckoutModule();
builder.Services.AddOrdersModule();
builder.Services.AddPaymentsModule();

// Admin runs under a cross-tenant principal — no single bound tenant (PLAN #3).
builder.Services.AddScoped<ITenantContext, AdminTenantContext>();

// Real Google ID-token validation (distinct audience per console via this host's own Google:ClientId),
// shared with TenantConsole. See BuildingBlocks.Web.GoogleAuthenticationExtensions.
builder.Services.AddGoogleIdTokenAuthentication(builder.Configuration, builder.Environment);

// Cross-cutting HTTP hardening: RFC7807 errors + split liveness/readiness probes.
builder.Services.AddProblemDetailsHandling();
builder.Services.AddReadinessHealthChecks();

var app = builder.Build();

// Fail-fast: build the vault keyring now so a missing/short/invalid master key crash-loops the host at
// boot instead of surfacing only on the first reveal. ValidateOnBuild does NOT run factory-registered
// singletons, so this explicit resolve is what delivers the boot-time custody guarantee.
_ = app.Services.GetRequiredService<VaultKeyring>();

// Correlation id outermost so the logging scope is still active when the exception handler logs a failure.
app.UseCorrelationId();
app.UseExceptionHandler();

app.UseAuthentication();
app.UseAuthorization();

app.MapPolHealthChecks();

// ponytail: placeholder admin surface — real cross-tenant admin queries land here once defined.
app.MapGet("/admin/tenants", () => Results.Ok(Array.Empty<object>()))
   .RequireAuthorization();

app.Run();

/// <summary>Exposed so <c>WebApplicationFactory&lt;Program&gt;</c> can boot the host in tests.</summary>
public partial class Program;
