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
using Worker;

var builder = WebApplication.CreateBuilder(args);

builder.AddJsonConsoleLogging();

// Fail fast on captive scope/lifetime mistakes (everything that touches the DbContext is Scoped).
if (builder.Environment.IsDevelopment())
{
    builder.Host.UseDefaultServiceProvider(o =>
    {
        o.ValidateScopes = true;
        o.ValidateOnBuild = true;
    });
}

builder.Services.AddMediator(options => options.ServiceLifetime = ServiceLifetime.Scoped);
builder.Services.AddBuildingBlocksInfrastructure();
builder.Services.AddOutboxDispatcher();

// The worker connects as pol_worker: it can read/lease the outbox across tenants (the table has no
// FILTER predicate) but is NOT in the bypass role, so per-message it writes consumer tables (Orders)
// only for the tenant the dispatcher binds via SESSION_CONTEXT.
var workerConnString = builder.Configuration.GetConnectionString("Worker");
builder.Services.AddDbContext<ProducerDbContext>((sp, opt) =>
    opt.UseSqlServer(workerConnString)
       .AddInterceptors(sp.GetRequiredService<SessionContextConnectionInterceptor>()));

builder.Services.AddReadinessHealthChecks();

builder.Services.AddSingleton(new ModuleAssemblies(WorkerModuleAssemblies.All));
builder.Services.Configure<VaultOptions>(builder.Configuration.GetSection(VaultOptions.SectionName));

// Tenant is bound per outbox message by the dispatcher (no HTTP, no claim).
builder.Services.AddScoped<ITenantContext, WorkerTenantContext>();

builder.Services.AddProductsModule();
builder.Services.AddCartModule();
builder.Services.AddCheckoutModule();
builder.Services.AddOrdersModule();
builder.Services.AddPaymentsModule();

var app = builder.Build();

app.MapPolHealthChecks();

app.Run();

/// <summary>Exposed so <c>WebApplicationFactory&lt;Program&gt;</c> can boot the worker in tests.</summary>
public partial class Program;
