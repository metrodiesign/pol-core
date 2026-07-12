extern alias ApiHost;

using BuildingBlocks.Infrastructure.Outbox;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Hosts.Tests;

// The Api and Worker hosts are the composition roots. PLAN #7 says the container must be free of
// captive-dependency / scope mistakes: a Scoped service (IActorContext, the MerchantGuardBehavior,
// the DbContext-backed idempotency/outbox/vault stores) must never be captured by a Singleton. Both
// Program.cs files switch on ValidateScopes + ValidateOnBuild in the Development environment, so the
// observable contract is: "boot the host under Development and the provider validates without
// throwing — with NO live SQL Server". These tests assert exactly that.
//
// No live database is touched: the SQL Server DbContexts are registered with a deterministic dummy
// connection string that is never opened (container validation constructs the DbContext but never
// runs a query), and the outbox dispatcher BackgroundService — the only component that would poll
// SQL Server at startup — is removed.

file static class HostHarness
{
    public sealed class ValidatingFactory<TEntry> : WebApplicationFactory<TEntry>
        where TEntry : class
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // Development is what flips on ValidateScopes + ValidateOnBuild in both Program.cs files.
            builder.UseEnvironment(Environments.Development);

            // Dev-convenience auto-migrate (Program.cs) reads this key too; blank it so a developer's real local
            // appsettings.Development.json Migrator connection can never make this "no live DB" test touch one.
            builder.UseSetting("ConnectionStrings:Migrator", "");

            // Deterministic, never-opened connection strings so DbContext registration does not depend
            // on the machine's environment. A query would fail, but container validation never runs one.
            // ConnectionStrings:Admin is required at boot (Program.cs fails fast without it) even though
            // Development skips the confidential-client/injected-credential guards.
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:App"] = "Server=(local);Database=pol_test;Trusted_Connection=True;",
                    ["ConnectionStrings:Admin"] = "Server=(local);Database=pol_test;Trusted_Connection=True;",
                    // Self-contained: the test must not depend on a (now uncommitted) appsettings.Development.json.
                    // Fake 32-byte (all-zero) base64 key — never a real secret.
                    ["Vault:MasterKeyBase64"] = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
                });
            });

            builder.ConfigureServices(services =>
            {
                // Remove ONLY the outbox dispatcher (a BackgroundService that polls SQL Server with
                // provider-specific T-SQL) — never the web host's own IHostedService — so starting the
                // host for validation never reaches for a live database.
                var dispatcher = services.SingleOrDefault(d =>
                    d.ServiceType == typeof(IHostedService) &&
                    d.ImplementationType == typeof(OutboxDispatcher));
                if (dispatcher is not null)
                    services.Remove(dispatcher);
            });
        }
    }
}

public sealed class ApiContainerTests
{
    [Fact]
    public void Api_container_builds_and_validates_without_a_live_database()
    {
        using var factory = new HostHarness.ValidatingFactory<ApiHost::Program>();

        // Accessing Services boots the host: this is where ValidateOnBuild + ValidateScopes run.
        // A captive-dependency or scope violation (PLAN #7) would throw here.
        var provider = factory.Services;

        Assert.NotNull(provider);
    }

    [Fact]
    public void Api_resolves_the_scoped_mediator_inside_a_scope()
    {
        using var factory = new HostHarness.ValidatingFactory<ApiHost::Program>();

        // The Mediator pipeline is registered Scoped so handlers can depend on the Scoped DbContext.
        // With ValidateScopes on, resolving it from a scope must succeed (resolving from the root
        // would throw) — proving the request-scoped pipeline is wired correctly.
        using var scope = factory.Services.CreateScope();
        var mediator = scope.ServiceProvider.GetService<Mediator.IMediator>();

        Assert.NotNull(mediator);
    }
}
