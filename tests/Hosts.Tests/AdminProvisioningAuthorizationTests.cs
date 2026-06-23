using System.Security.Claims;
using BuildingBlocks.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Hosts.Tests;

/// <summary>
/// The admin provisioning routes (POST/GET /admin/tenants) gate on <c>RequireAuthorization("admin")</c>.
/// These pin that the "admin" policy admits ONLY an admin-SPA token and rejects a tenant-SPA token
/// (REQ-7.1 / REQ-7.3) — the boundary that stops a tenant principal from reaching the cross-tenant
/// provisioning surface — and that an anonymous caller is refused (REQ-7.2). Full HTTP round-trips with
/// real Google tokens are out of scope here, as elsewhere in this suite; the policy is the unit under test.
/// </summary>
public sealed class AdminProvisioningAuthorizationTests
{
    [Fact]
    public async Task Admin_policy_admits_admin_role_rejects_tenant_role_and_anonymous()
    {
        var authz = BuildAuth().GetRequiredService<IAuthorizationService>();

        Assert.True((await authz.AuthorizeAsync(Principal("admin"), "admin")).Succeeded);    // REQ-7.1
        Assert.False((await authz.AuthorizeAsync(Principal("tenant"), "admin")).Succeeded);  // REQ-7.3
        Assert.False((await authz.AuthorizeAsync(Anonymous(), "admin")).Succeeded);          // REQ-7.2
    }

    private static ServiceProvider BuildAuth() => new ServiceCollection()
        .AddLogging() // DefaultAuthorizationService needs ILogger
        .AddGoogleIdTokenAuthentication(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Google:Audiences:tenant"] = "111-tenant.apps.googleusercontent.com",
                ["Google:Audiences:admin"] = "222-admin.apps.googleusercontent.com",
            }).Build(),
            new StubEnvironment())
        .BuildServiceProvider();

    private static ClaimsPrincipal Principal(string role) =>
        new(new ClaimsIdentity([new Claim(GoogleAuthenticationExtensions.RoleClaimType, role)], "test"));

    private static ClaimsPrincipal Anonymous() => new(new ClaimsIdentity());

    private sealed class StubEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "Hosts.Tests";
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
