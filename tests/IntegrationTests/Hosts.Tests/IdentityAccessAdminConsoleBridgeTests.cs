extern alias ApiHost;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Accounts.Application;
using Accounts.Domain;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using ApiIdentity = ApiHost::Api.IdentityAccess;

namespace Hosts.Tests;

file sealed class BridgeState
{
    public Account Account { get; set; } = Account.Create(AccountType.Employee, "Bridge employee", DateTime.UtcNow);
    public bool HasPlatformAccess { get; set; } = true;
    public HashSet<string> Permissions { get; set; } = ["txn.view"];
}

file sealed class BridgeQuery(BridgeState state) : IIdentityAccessQuery
{
    public Task<Account?> FindAccountAsync(Guid accountId, CancellationToken cancellationToken) =>
        Task.FromResult<Account?>(accountId == state.Account.Id ? state.Account : null);

    public Task<string?> FindLoginEmailAsync(Guid accountId, CancellationToken cancellationToken) =>
        Task.FromResult<string?>("bridge@example.test");

    public Task<SystemClientResolution?> FindSystemClientAsync(string clientId, CancellationToken cancellationToken) =>
        Task.FromResult<SystemClientResolution?>(null);

    public Task<AuthorizationSnapshot?> ResolveAuthorizationAsync(
        Guid accountId, Guid? merchantId, Guid? clientId, CancellationToken cancellationToken) =>
        Task.FromResult<AuthorizationSnapshot?>(accountId != state.Account.Id
            ? null
            : new AuthorizationSnapshot(
                state.Account.Id,
                state.Account.AccountType,
                state.Account.Status,
                state.Account.AuthorizationVersion,
                merchantId,
                null,
                null,
                null,
                new HashSet<Guid>(),
                new HashSet<Guid>(),
                state.HasPlatformAccess,
                new HashSet<string>(state.Permissions, StringComparer.Ordinal),
                clientId));

    public Task<IReadOnlyList<MerchantAccessSummary>> ListMerchantAccessAsync(
        Guid accountId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<MerchantAccessSummary>>([]);
}

file sealed class BridgeBffStore : IBffSessionStore
{
    private readonly List<BffSessionTicket> _tickets = [];

    public Task<BffSessionTicket?> FindByHashAsync(byte[] ticketKeyHash, CancellationToken cancellationToken) =>
        Task.FromResult(_tickets.FirstOrDefault(ticket => ticket.TicketKeyHash.SequenceEqual(ticketKeyHash)));

    public void Add(BffSessionTicket ticket) => _tickets.Add(ticket);

    public Task RevokeAsync(BffSessionTicket ticket, DateTime now, CancellationToken cancellationToken)
    {
        ticket.Revoke(now);
        return Task.CompletedTask;
    }

    public Task ReplaceAsync(BffSessionTicket current, BffSessionTicket replacement, DateTime now, CancellationToken cancellationToken)
    {
        current.Revoke(now);
        _tickets.Add(replacement);
        return Task.CompletedTask;
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

file sealed class BridgeFactory : WebApplicationFactory<ApiHost::Program>
{
    public BridgeState State { get; } = new();
    public BridgeBffStore Store { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Development);
        builder.UseSetting("ConnectionStrings:Migrator", "");
        builder.UseSetting("ConnectionStrings:App", "Server=(local);Database=pol_test;Trusted_Connection=True;");
        builder.UseSetting("ConnectionStrings:Admin", "Server=(local);Database=pol_test;Trusted_Connection=True;");
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.IgnoreMachineLocalDevelopmentSettings();
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Vault:MasterKeyBase64"] = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
            });
        });
        builder.ConfigureServices(services =>
        {
            services.AddDataProtection().UseEphemeralDataProtectionProvider();
            services.AddSingleton(State);
            services.RemoveAll<IIdentityAccessQuery>();
            services.AddScoped<IIdentityAccessQuery>(sp => new BridgeQuery(sp.GetRequiredService<BridgeState>()));
            services.RemoveAll<IBffSessionStore>();
            services.AddSingleton<IBffSessionStore>(Store);
        });
    }

    /// <summary>Mints a live BFF session for the state account and returns the Cookie header the SPA would send
    /// (https origin, so the __Host- session cookie is the one the handler reads).</summary>
    public async Task<(string Cookie, string Csrf)> LoginAsync()
    {
        using var scope = Services.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<ApiIdentity.BffSessionManager>();
        var issue = await manager.CreateAsync(State.Account, null, null, "refresh", null, "/", default);
        return (
            $"{ApiIdentity.BffSessionManager.SessionCookieName}={issue.SessionToken}; {ApiIdentity.BffSessionManager.CsrfCookieName}={issue.CsrfToken}",
            issue.CsrfToken);
    }

    public HttpClient CreateBrowser(string cookie)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
        });
        client.DefaultRequestHeaders.Add("Cookie", cookie);
        return client;
    }
}

/// <summary>Employee BFF session on the admin console: the "admin" policy forwards to the BFF scheme when only
/// the BFF cookie is present, the handler binds IAdminScope from the account's authorization snapshot, and the
/// admin CSRF filter reads the BFF double-submit pair.</summary>
[Trait("Capability", "IdentityAccess")]
public sealed class IdentityAccessAdminConsoleBridgeTests
{
    [Fact]
    public async Task Employee_with_platform_access_reads_the_admin_console_as_an_unrestricted_admin()
    {
        using var factory = new BridgeFactory();
        var (cookie, _) = await factory.LoginAsync();
        using var client = factory.CreateBrowser(cookie);

        var response = await client.GetAsync("/api/v1/admins/me");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var me = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(factory.State.Account.Id, me.GetProperty("adminId").GetGuid());
        Assert.Equal("bridge@example.test", me.GetProperty("email").GetString());
        Assert.Equal("Super", me.GetProperty("tier").GetString());
        Assert.True(me.GetProperty("accessibleMerchants").GetProperty("isUnrestricted").GetBoolean());
        Assert.Equal(["txn.view"], me.GetProperty("permissions").EnumerateArray().Select(x => x.GetString()!).ToArray());
    }

    [Fact]
    public async Task Bff_cookie_alone_is_rejected_without_the_admin_cookie_when_the_account_is_not_an_employee()
    {
        using var factory = new BridgeFactory();
        factory.State.Account = Account.Create(AccountType.Agent, "Bridge agent", DateTime.UtcNow);
        var (cookie, _) = await factory.LoginAsync();
        using var client = factory.CreateBrowser(cookie);

        var response = await client.GetAsync("/api/v1/admins/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Dual_console_route_treats_the_bff_cookie_as_the_admin_audience()
    {
        using var factory = new BridgeFactory();
        factory.State.Permissions = [];
        var (cookie, _) = await factory.LoginAsync();
        using var client = factory.CreateBrowser(cookie);

        // Admin audience + bound scope without the permission = 403 from the permission gate, not a 401 from
        // the merchant-user scheme.
        var response = await client.GetAsync("/api/v1/merchants/users");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("permission", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Admin_console_mutation_accepts_the_bff_csrf_pair_and_still_gates_on_permission()
    {
        using var factory = new BridgeFactory();
        factory.State.Permissions = [];
        var (cookie, csrf) = await factory.LoginAsync();
        using var client = factory.CreateBrowser(cookie);

        var withoutHeader = await client.PostAsJsonAsync("/api/v1/roles", new { });
        Assert.Equal(HttpStatusCode.Forbidden, withoutHeader.StatusCode);
        Assert.Contains("csrf_failed", await withoutHeader.Content.ReadAsStringAsync());

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/roles") { Content = JsonContent.Create(new { }) };
        request.Headers.Add(ApiIdentity.BffSessionManager.HeaderName, csrf);
        var withHeader = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, withHeader.StatusCode);
        Assert.DoesNotContain("csrf_failed", await withHeader.Content.ReadAsStringAsync());
    }
}
