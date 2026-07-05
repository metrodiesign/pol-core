extern alias ApiHost;
using ApiHost::Api;
using System.Net;
using System.Security.Claims;
using BuildingBlocks.Infrastructure.Outbox;
using BuildingBlocks.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Hosts.Tests;

/// <summary>
/// The /api/v1/admins/* surface gates on <c>RequireAuthorization("admin")</c>. After the BFF cutover (REQ-10) that
/// policy is the AdminSession COOKIE scheme — not the retired Bearer "admin" audience: it is pinned to that
/// scheme, refuses anonymous (REQ-7.2), and is NOT registered by the Google Bearer wiring (REQ-10.2). A live
/// /api/v1/admins request with no session cookie returns 401 — not 500 (missing policy) and not a login redirect.
/// </summary>
public sealed class AdminProvisioningAuthorizationTests
{
    [Fact]
    public async Task Admin_policy_is_pinned_to_the_session_cookie_scheme_and_refuses_anonymous()
    {
        var sp = new ServiceCollection()
            .AddLogging()
            .AddGoogleIdTokenAuthentication(Audiences(), Env())
            .AddAdminSessionScheme()
            .BuildServiceProvider();

        var policy = await sp.GetRequiredService<IAuthorizationPolicyProvider>().GetPolicyAsync("admin");
        Assert.NotNull(policy);
        Assert.Contains(AdminSessionAuthenticationHandler.SchemeName, policy!.AuthenticationSchemes); // REQ-10.6 scheme-pinned
        Assert.False((await sp.GetRequiredService<IAuthorizationService>().AuthorizeAsync(Anonymous(), "admin")).Succeeded); // REQ-7.2
    }

    [Fact]
    public async Task The_bearer_wiring_no_longer_registers_an_admin_policy()
    {
        // admin audience retired (REQ-10.2): the Google Bearer wiring registers only the "tenant" policy.
        var sp = new ServiceCollection().AddLogging().AddGoogleIdTokenAuthentication(Audiences(), Env()).BuildServiceProvider();
        var provider = sp.GetRequiredService<IAuthorizationPolicyProvider>();

        Assert.Null(await provider.GetPolicyAsync("admin"));
        Assert.NotNull(await provider.GetPolicyAsync("tenant")); // tenant policy unaffected
    }

    [Fact]
    public async Task An_admin_route_without_a_session_cookie_returns_401()
    {
        using var factory = new GateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/api/v1/admins/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static IConfiguration Audiences() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Google:Audiences:tenant"] = "111-tenant.apps.googleusercontent.com",
        ["Google:Audiences:admin"] = "222-admin.apps.googleusercontent.com", // present in config but stripped at wiring
    }).Build();

    private static ClaimsPrincipal Anonymous() => new(new ClaimsIdentity());

    private static IHostEnvironment Env() => new StubEnvironment();

    private sealed class StubEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "Hosts.Tests";
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}

file sealed class GateFactory : WebApplicationFactory<ApiHost::Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Development);
        builder.UseSetting("Google:Audiences:tenant", "test-client-id.apps.googleusercontent.com");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Producer"] = "Server=(local);Database=pol_test;Trusted_Connection=True;",
            ["Vault:MasterKeyBase64"] = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
        }));
        builder.ConfigureServices(services =>
        {
            var dispatcher = services.SingleOrDefault(d =>
                d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(OutboxDispatcher));
            if (dispatcher is not null) services.Remove(dispatcher);
        });
    }
}
