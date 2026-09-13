extern alias ApiHost;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Accounts.Application;
using Accounts.Domain;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using OpenIddict.Validation.AspNetCore;

namespace Hosts.Tests;

file sealed class BridgeState
{
    public Account Account { get; set; } = Account.Create(AccountType.Employee, "Bridge employee", DateTime.UtcNow);
    public bool HasPlatformAccess { get; set; } = true;
    public HashSet<string> Permissions { get; set; } = ["txn.view"];
    public string? ClientId { get; set; }
    /// <summary>authz_version to stamp on the token; null = the account's current version.</summary>
    public long? TokenVersion { get; set; }
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

/// <summary>Stands in for OpenIddict token validation: any Bearer header becomes the platform JWT principal of
/// the state account (the claims the /oauth/token handler puts on an employee token).</summary>
file sealed class BridgeBearerHandler(
    Microsoft.Extensions.Options.IOptionsMonitor<AuthenticationSchemeOptions> options,
    Microsoft.Extensions.Logging.ILoggerFactory logger,
    System.Text.Encodings.Web.UrlEncoder encoder,
    BridgeState state)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Context.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(AuthenticateResult.NoResult());
        var identity = new ClaimsIdentity(Scheme.Name);
        identity.AddClaim(new Claim("sub", state.Account.Id.ToString("D")));
        identity.AddClaim(new Claim("account_type", state.Account.AccountType.ToString().ToUpperInvariant()));
        identity.AddClaim(new Claim("authz_version", (state.TokenVersion ?? state.Account.AuthorizationVersion).ToString()));
        identity.AddClaim(new Claim("token_context", "ACCOUNT_SELF"));
        if (state.ClientId is not null)
            identity.AddClaim(new Claim("client_id", state.ClientId));
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }
}

file sealed class BridgeFactory : WebApplicationFactory<ApiHost::Program>
{
    public BridgeState State { get; } = new();

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
            services.AddTransient<BridgeBearerHandler>();
            services.PostConfigure<AuthenticationOptions>(options =>
                options.Schemes.Single(scheme =>
                        scheme.Name == OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)
                    .HandlerType = typeof(BridgeBearerHandler));
        });
    }

    public HttpClient CreateBearerClient()
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
        });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "employee-jwt");
        return client;
    }
}

/// <summary>Employee JWT on the admin console: the "admin" policy forwards a Bearer request to the platform token
/// scheme (OpenIddict validation, stood in for here), which materializes IAdminScope from the account's
/// authorization snapshot; the admin CSRF filter does not apply to a Bearer request.</summary>
[Trait("Capability", "IdentityAccess")]
public sealed class IdentityAccessAdminConsoleBridgeTests
{
    [Fact]
    public async Task Employee_with_platform_access_reads_the_admin_console_as_an_unrestricted_admin()
    {
        using var factory = new BridgeFactory();
        using var client = factory.CreateBearerClient();

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
    public async Task Bearer_token_of_a_non_employee_account_is_rejected_on_the_admin_console()
    {
        using var factory = new BridgeFactory();
        factory.State.Account = Account.Create(AccountType.Agent, "Bridge agent", DateTime.UtcNow);
        using var client = factory.CreateBearerClient();

        var response = await client.GetAsync("/api/v1/admins/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task System_client_token_never_reaches_the_admin_console()
    {
        using var factory = new BridgeFactory();
        factory.State.Account = Account.Create(AccountType.System, "Bridge system", DateTime.UtcNow);
        factory.State.ClientId = "system-client";
        using var client = factory.CreateBearerClient();

        var response = await client.GetAsync("/api/v1/admins/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Stale_authorization_version_is_unauthenticated_not_forbidden()
    {
        using var factory = new BridgeFactory();
        factory.State.TokenVersion = factory.State.Account.AuthorizationVersion + 1;
        using var client = factory.CreateBearerClient();

        var response = await client.GetAsync("/api/v1/admins/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Dual_console_route_treats_the_bearer_token_as_the_admin_audience()
    {
        using var factory = new BridgeFactory();
        factory.State.Permissions = [];
        using var client = factory.CreateBearerClient();

        // Admin audience + bound scope without the permission = 403 from the permission gate, not a 401 from
        // the merchant-user scheme.
        var response = await client.GetAsync("/api/v1/merchants/users");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("permission", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Admin_console_mutation_with_a_bearer_token_skips_csrf_and_still_gates_on_permission()
    {
        using var factory = new BridgeFactory();
        factory.State.Permissions = [];
        using var client = factory.CreateBearerClient();

        var response = await client.PostAsJsonAsync("/api/v1/roles", new { });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.DoesNotContain("csrf_failed", await response.Content.ReadAsStringAsync());
    }
}
