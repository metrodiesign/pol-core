extern alias ApiHost;
using ApiHost::Api;
using ApiHost::Api.Admins;
using System.Net;
using System.Security.Claims;
using BuildingBlocks.Infrastructure.Outbox;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Hosts.Tests;

/// <summary>
/// The /api/v1/admins/* surface gates on <c>RequireAuthorization("admin")</c>. That policy is the
/// Session COOKIE scheme — T5 retired the Google id-token Bearer scheme entirely, so there is no
/// dual-scheme fallback left to test against: it is pinned to that one scheme and refuses anonymous (REQ-7.2). A
/// live /api/v1/admins request with no session cookie returns 401 — not 500 (missing policy) and not a login
/// redirect.
/// </summary>
public sealed class AdminProvisioningAuthorizationTests
{
    [Fact]
    public async Task Admin_policy_is_pinned_to_the_session_cookie_scheme_and_refuses_anonymous()
    {
        var sp = new ServiceCollection()
            .AddLogging()
            .AddPlatformUserSessionScheme()
            .BuildServiceProvider();

        var policy = await sp.GetRequiredService<IAuthorizationPolicyProvider>().GetPolicyAsync("admin");
        Assert.NotNull(policy);
        Assert.Contains(SessionAuthenticationHandler.SchemeName, policy!.AuthenticationSchemes); // REQ-10.6 scheme-pinned
        Assert.False((await sp.GetRequiredService<IAuthorizationService>().AuthorizeAsync(Anonymous(), "admin")).Succeeded); // REQ-7.2
    }

    [Fact]
    public async Task An_admin_route_without_a_session_cookie_returns_401()
    {
        using var factory = new GateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/api/v1/admins/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static ClaimsPrincipal Anonymous() => new(new ClaimsIdentity());
}

file sealed class GateFactory : WebApplicationFactory<ApiHost::Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Development);
        // Dev-convenience auto-migrate (Program.cs) reads this key too; blank it so a developer's real local
        // appsettings.Development.json Migrator connection can never make this "no live DB" test touch one.
        builder.UseSetting("ConnectionStrings:Migrator", "");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:App"] = "Server=(local);Database=pol_test;Trusted_Connection=True;",
            ["ConnectionStrings:Admin"] = "Server=(local);Database=pol_test;Trusted_Connection=True;",
            ["Vault:MasterKeyBase64"] = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
        }));
    }
}
