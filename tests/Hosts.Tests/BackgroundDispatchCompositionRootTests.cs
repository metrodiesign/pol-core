extern alias ApiHost;

using System.Net;
using BuildingBlocks.Application;
using Merchants.Application.Users;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Hosts.Tests;

/// <summary>
/// multi-tier-deployment task 1: proves the composition-root merge (Worker's hosted services + scope
/// discriminator moved into the Api host, Program.cs) resolves the right branch for
/// <see cref="IActorContext"/> — the single highest-risk item design.md flags, since it sits directly on
/// the GuardedRuntimeDbContext/IWriteAuthorizer security boundary. A background-created scope (the outbox
/// dispatcher's own <c>IServiceScopeFactory.CreateScope()</c>) must never resolve the HTTP branch, and vice
/// versa — a misresolution would silently authorize a background write as a specific HTTP actor, or reject
/// a legitimate background write.
/// </summary>
public sealed class BackgroundDispatchCompositionRootTests
{
    private sealed class ValidatingFactory : WebApplicationFactory<ApiHost::Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(Environments.Development);
            builder.UseSetting("ConnectionStrings:Migrator", "");
            builder.UseSetting("ConnectionStrings:App", "Server=(local);Database=pol_test;Trusted_Connection=True;");
            builder.UseSetting("ConnectionStrings:Admin", "Server=(local);Database=pol_test;Trusted_Connection=True;");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Vault:MasterKeyBase64"] = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
                });
            });
        }
    }

    [Fact]
    public void HTTP_scope_resolves_HttpActorContext()
    {
        using var factory = new ValidatingFactory();
        using var scope = factory.Services.CreateScope();

        // Simulate the state Kestrel/middleware sets before user code runs: HttpContext present on the
        // scope's IHttpContextAccessor (AsyncLocal-backed, so this only affects the current async flow).
        var accessor = scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>();
        accessor.HttpContext = new DefaultHttpContext();

        var actor = scope.ServiceProvider.GetRequiredService<IActorContext>();

        Assert.IsType<ApiHost::Api.HttpActorContext>(actor);
    }

    [Fact]
    public void Background_dispatch_scope_resolves_WorkerActorContext()
    {
        using var factory = new ValidatingFactory();

        // No HttpContext is ever set on this scope's accessor — exactly what OutboxDispatcher/
        // MerchantUserOutboxDispatcher do via their own IServiceScopeFactory.CreateScope() calls.
        using var scope = factory.Services.CreateScope();

        var actor = scope.ServiceProvider.GetRequiredService<IActorContext>();

        Assert.IsType<ApiHost::Api.BackgroundDispatch.WorkerActorContext>(actor);
    }

    [Fact]
    public void Prune_services_resolve_the_full_persistence_graph_under_the_background_branch()
    {
        // REQ-5.6 (AN-4): SessionPruneService/UserSessionPruneService already create background scopes
        // (CreateScope, no HttpContext) — post-merge they resolve WorkerActorContext instead of the old
        // HttpActorContext-with-null-HttpContext. Their actual prune deletes are EF ExecuteDeleteAsync bulk
        // operations that never touch IActorContext/IWriteAuthorizer, so the behavior that CAN break here is
        // DI construction itself: MerchantUserDbContext (and everything MerchantUserPersistenceRegistration
        // wires over it) must still construct cleanly when IActorContext resolves to WorkerActorContext.
        using var factory = new ValidatingFactory();
        using var scope = factory.Services.CreateScope();

        var store = scope.ServiceProvider.GetRequiredService<ISessionStore>();

        Assert.NotNull(store);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BackgroundDispatchScope_IsHttpRequest_matches_HttpContext_presence(bool httpContextPresent)
    {
        // Unit-level proof that the SAME discriminator drives IWriteAuthorizer's factory (ResolveMerchantWriteAuthorizer
        // in Program.cs) as the IActorContext registration above — no full host boot needed for this one.
        var services = new ServiceCollection();
        services.AddHttpContextAccessor();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var accessor = scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>();
        if (httpContextPresent)
            accessor.HttpContext = new DefaultHttpContext();

        Assert.Equal(httpContextPresent, ApiHost::Api.BackgroundDispatch.BackgroundDispatchScope.IsHttpRequest(scope.ServiceProvider));
    }
}
