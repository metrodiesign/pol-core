extern alias ApiHost;

using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Admins.Application;
using Admins.Application.Users;
using Admins.Domain.Users;
using Iam.Domain.Permissions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Offices.Application;
using Offices.Domain;

namespace Hosts.Tests;

file sealed class OfficeTestAdminAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "OfficeTestAdmin";
    public const string HeaderName = "X-Test-Admin";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.ContainsKey(HeaderName))
            return Task.FromResult(AuthenticateResult.NoResult());

        var identity = new ClaimsIdentity([new Claim("sub", "office-admin")], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}

file sealed class OfficeAdminScope(IReadOnlySet<string> permissions) : IAdminScope
{
    public bool IsBound => true;
    public Resolution Current { get; } =
        new(Guid.NewGuid(), "office-admin@example.com", Tier.Super, AccessibleMerchants.All)
        {
            Permissions = permissions,
        };
    public AccessibleMerchants Accessible => Current.Accessible;
}

file sealed class FakeOfficeStore : IOfficeStore
{
    public int ListCalls { get; private set; }
    public int? Page { get; private set; }
    public int? Limit { get; private set; }

    public Task<BuildingBlocks.Application.PagedResult<OfficeItem>> ListAsync(
        int page, int limit, string? search, CancellationToken cancellationToken)
    {
        ListCalls++;
        Page = page;
        Limit = limit;
        return Task.FromResult(new BuildingBlocks.Application.PagedResult<OfficeItem>(
            [new OfficeItem(Guid.Parse("b2000000-0000-4000-8000-000000000001"), "hq", "สำนักงานใหญ่", OfficeStatus.Active)],
            page, limit, 1));
    }

    public Task<OfficeItem> CreateAsync(string code, string name, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<OfficeItem> UpdateAsync(
        Guid id, string name, OfficeStatus status, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<OfficeItem> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<OfficeItem> DeactivateAsync(Guid id, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}

file sealed class OfficeAuthorizationFactory(bool grantUserManage)
    : WebApplicationFactory<ApiHost::Program>
{
    private const string UnusedConnection =
        "Server=(local);Database=pol_test;Trusted_Connection=True;";

    public FakeOfficeStore Store { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Development);
        builder.UseSetting("ConnectionStrings:Migrator", "");
        builder.UseSetting("ConnectionStrings:App", UnusedConnection);
        builder.UseSetting("ConnectionStrings:Admin", UnusedConnection);
        builder.UseSetting("ConnectionStrings:Worker", UnusedConnection);
        builder.UseSetting("AdminAuth:Providers:Google:ClientId", "");
        builder.UseSetting("AdminAuth:Providers:Microsoft:ClientId", "");
        builder.UseSetting("MerchantAuth:Providers:Google:ClientId", "");
        builder.UseSetting("MerchantAuth:Providers:Microsoft:ClientId", "");
        builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Vault:MasterKeyBase64"] = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
                ["Merchant:DevMerchantId"] = "00000000-0000-0000-0000-000000000001",
            }));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IHostedService>();
            services.AddAuthentication()
                .AddScheme<AuthenticationSchemeOptions, OfficeTestAdminAuthHandler>(
                    OfficeTestAdminAuthHandler.SchemeName, _ => { });
            services.PostConfigure<AuthorizationOptions>(options => options.AddPolicy("admin", policy => policy
                .AddAuthenticationSchemes(OfficeTestAdminAuthHandler.SchemeName)
                .RequireAuthenticatedUser()));

            IReadOnlySet<string> permissions = grantUserManage
                ? new HashSet<string>([Keys.UserManage], StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);
            services.AddScoped<IAdminScope>(_ => new OfficeAdminScope(permissions));
            services.AddScoped<IOfficeStore>(_ => Store);
        });
    }
}

public sealed class OfficeAuthorizationEndpointTests
{
    private const string Route = "/api/v1/offices?page=1&limit=25";

    [Fact]
    public async Task Authenticated_admin_with_user_manage_gets_200_without_csrf()
    {
        using var factory = new OfficeAuthorizationFactory(grantUserManage: true);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(OfficeTestAdminAuthHandler.HeaderName, "1");

        var response = await client.GetAsync(Route);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, factory.Store.ListCalls);
        Assert.Equal(1, factory.Store.Page);
        Assert.Equal(25, factory.Store.Limit);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(1, body.RootElement.GetProperty("page").GetInt32());
        Assert.Equal(25, body.RootElement.GetProperty("limit").GetInt32());
        Assert.Equal(1, body.RootElement.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Authenticated_super_without_user_manage_gets_403_before_store()
    {
        using var factory = new OfficeAuthorizationFactory(grantUserManage: false);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(OfficeTestAdminAuthHandler.HeaderName, "1");

        var response = await client.GetAsync(Route);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, factory.Store.ListCalls);
    }

    [Fact]
    public async Task Request_without_admin_session_gets_401_before_store()
    {
        using var factory = new OfficeAuthorizationFactory(grantUserManage: true);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(Route);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, factory.Store.ListCalls);
    }

    [Theory]
    [InlineData("/api/v1/positions")]
    [InlineData("/api/v1/offices")]
    [InlineData("/api/v1/levels")]
    [InlineData("/api/v1/divisions")]
    public void Every_master_data_list_keeps_admin_and_user_manage_gates(string route)
    {
        using var factory = new OfficeAuthorizationFactory(grantUserManage: true);
        using var _ = factory.CreateClient();

        var endpoint = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Single(candidate => candidate.RoutePattern.RawText == route
                && (candidate.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains("GET") ?? false));

        var policy = endpoint.Metadata.OfType<IAuthorizeData>()
            .Select(data => data.Policy)
            .Last(value => !string.IsNullOrEmpty(value));
        Assert.Equal("admin", policy);
        var required = Assert.Single(endpoint.Metadata.OfType<ApiHost::Api.Iam.RequiredPermission>());
        Assert.Equal(Keys.UserManage, required.Permission);
    }
}
