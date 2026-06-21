using AdminConsole;
using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure;
using BuildingBlocks.Infrastructure.Persistence;
using BuildingBlocks.Infrastructure.Vault;
using Cart.Infrastructure;
using Checkout.Infrastructure;
using Mediator;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Orders.Infrastructure;
using Payments.Infrastructure;
using Products.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

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

// Google SSO skeleton (PLAN #5): distinct audience per console + an "hd" hosted-domain guard and a
// required verified email. // ponytail: real JWKS/key wiring is config-driven; this is a compiling,
// correctly-configured skeleton — no real Google keys are embedded.
var googleClientId = builder.Configuration["Google:ClientId"];
var googleHostedDomain = builder.Configuration["Google:HostedDomain"];
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = "https://accounts.google.com";
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuers = ["https://accounts.google.com", "accounts.google.com"],
            ValidateAudience = true,
            ValidAudience = googleClientId,
            ValidateLifetime = true,
            RequireExpirationTime = true,
        };
        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = context =>
            {
                var principal = context.Principal;
                if (principal?.FindFirst("email_verified")?.Value is not "true")
                {
                    context.Fail("The 'email_verified' claim is required.");
                    return Task.CompletedTask;
                }

                if (!string.IsNullOrEmpty(googleHostedDomain) &&
                    principal.FindFirst("hd")?.Value != googleHostedDomain)
                {
                    context.Fail("The token's hosted domain ('hd') is not allowed.");
                }

                return Task.CompletedTask;
            },
        };
    });
builder.Services.AddAuthorization();

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// ponytail: placeholder admin surface — real cross-tenant admin queries land here once defined.
app.MapGet("/admin/tenants", () => Results.Ok(Array.Empty<object>()))
   .RequireAuthorization();

app.Run();

/// <summary>Exposed so <c>WebApplicationFactory&lt;Program&gt;</c> can boot the host in tests.</summary>
public partial class Program;
