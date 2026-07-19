using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure;
using BuildingBlocks.Infrastructure.Observability;
using BuildingBlocks.Infrastructure.Persistence;
using BuildingBlocks.Infrastructure.Vault;
using BuildingBlocks.Web;
using Carts.Infrastructure;
using Checkouts.Infrastructure;
using Mediator;
using Microsoft.Data.SqlClient;
using Orders.Infrastructure;
using Payments.Infrastructure;
using Merchants.Infrastructure;
using Persistence.MerchantRuntime;
using Persistence.MerchantRuntime.Outbox;
using Persistence.MerchantUsers;
using Persistence.MerchantUsers.Outbox;
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
builder.Services.AddSecurityTelemetry(builder.Configuration, applicationName: "Worker");

// 1-principal collapse (task 8): the worker connects as pol_app, same as every other host — cross-merchant
// outbox lease/drain is a WorkerWriteAuthorizer capability now (app-layer default-deny), not a DB-level RLS
// bypass grant. Both runtime clusters the worker touches get their own dispatcher: MerchantRuntimeDbContext
// (payment/checkout/order events, txn.OutboxMessages) and MerchantUserDbContext (registration events,
// merch.UserOutbox).
var workerConnString = builder.Configuration.GetConnectionString("Worker");
// REQ-13.3: every host connects as the SAME pol_app login (task 8, 1 principal) — Application Name is the
// one thing that still lets sys.dm_exec_sessions/sys.dm_exec_requests attribute activity to Api vs Worker.
workerConnString = new SqlConnectionStringBuilder(workerConnString) { ApplicationName = "Worker" }.ConnectionString;
builder.Services.AddMerchantRuntimePersistence(workerConnString!, _ => new WorkerWriteAuthorizer())
    .AddMerchantRuntimeOutboxDispatcher();
builder.Services.AddMerchantUserPersistence(workerConnString!, _ => new WorkerWriteAuthorizer())
    .AddMerchantUserOutboxDispatcher();

// The worker has no admin/control-plane surface — Mediator still discovers Iam's role-CRUD handlers and
// ProvisionMerchantHandler transitively (Merchants.Application needs Iam.Application for RolePorts), so
// ValidateOnBuild needs something constructible even though the worker's own code never dispatches either.
builder.Services.AddSingleton<Iam.Application.Roles.IRoleStore, WorkerUnsupportedRoleStore>();
builder.Services.AddSingleton<IProvisioningWriter, WorkerUnsupportedProvisioningWriter>();
builder.Services.AddSingleton<Iam.Application.Roles.IRoleAssignmentCounter, WorkerUnsupportedRoleAssignmentCounter>();
builder.Services.AddSingleton<Iam.Application.Roles.IRoleAuditSink>(Iam.Application.Roles.NullRoleAuditSink.Instance);
builder.Services.AddKeyedSingleton<IUnitOfWork, WorkerUnsupportedAdminUnitOfWork>("admin");

builder.Services.AddReadinessHealthChecks(workerConnString!);

builder.Services.AddSingleton(new ModuleAssemblies(WorkerModuleAssemblies.All));
builder.Services.Configure<VaultOptions>(builder.Configuration.GetSection(VaultOptions.SectionName));

// The actor is bound per outbox message by the dispatcher (no HTTP, no claim).
builder.Services.AddScoped<IActorContext, WorkerActorContext>();

builder.Services.AddProductsModule();
builder.Services.AddCartModule();
builder.Services.AddCheckoutModule();
builder.Services.AddOrdersModule();
builder.Services.AddPaymentsModule();
builder.Services.AddMerchantsModule();

var app = builder.Build();

app.MapPolHealthChecks();

app.Run();

/// <summary>Exposed so <c>WebApplicationFactory&lt;Program&gt;</c> can boot the worker in tests.</summary>
public partial class Program;
