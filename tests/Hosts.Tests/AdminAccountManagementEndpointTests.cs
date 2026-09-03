extern alias ApiHost;
using System.Net;
using System.Net.Http;
using BuildingBlocks.Infrastructure.Outbox;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Hosts.Tests;

/// <summary>
/// The admin-management and pre-bound invite endpoints all gate on <c>RequireAuthorization("admin")</c> (REQ-7.1): a
/// request with no session cookie is 401 before any permission/tier filter or handler runs. This boots the real
/// host (no live DB needed — authorization short-circuits before the handler) and asserts 401 on each route and
/// verb, including the unsafe POST/DELETE (auth precedes the CSRF endpoint filter).
/// </summary>
public sealed class AdminAccountManagementEndpointTests
{
    private static readonly Guid Id = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Sid = Guid.Parse("22222222-2222-2222-2222-222222222222");

    public static TheoryData<string, string> UncookiedRoutes() => new()
    {
        { "GET", "/api/v1/admins" },
        { "POST", "/api/v1/admins" },
        { "GET", $"/api/v1/admins/{Id}" },
        { "GET", $"/api/v1/admins/{Id}/effective-permissions" },
        { "GET", $"/api/v1/admins/{Id}/sessions" },
        { "POST", $"/api/v1/admins/{Id}/reactivate" },
        { "DELETE", $"/api/v1/admins/{Id}/sessions/{Sid}" },
    };

    [Theory]
    [MemberData(nameof(UncookiedRoutes))]
    public async Task Every_admin_management_route_returns_401_without_a_session_cookie(string method, string path)
    {
        using var factory = new AdminMgmtFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}

file sealed class AdminMgmtFactory : WebApplicationFactory<ApiHost::Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Development);
        // Dev-convenience auto-migrate (Program.cs) reads this key too; blank it so a developer's real local
        // appsettings.Development.json Migrator connection can never make this "no live DB" test touch one.
        builder.UseSetting("ConnectionStrings:Migrator", "");
        builder.UseSetting("ConnectionStrings:App", "Server=(local);Database=pol_test;Trusted_Connection=True;");
        builder.UseSetting("ConnectionStrings:Admin", "Server=(local);Database=pol_test;Trusted_Connection=True;");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Vault:MasterKeyBase64"] = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
        }));
    }
}
