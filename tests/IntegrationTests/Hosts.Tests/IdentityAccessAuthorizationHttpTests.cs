extern alias ApiHost;
using System.Security.Claims;
using Accounts.Application;
using Accounts.Domain;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using ApiIdentity = ApiHost::Api.IdentityAccess;

namespace Hosts.Tests;

file sealed class IdentityAuthorizationState
{
    public Account Account { get; set; } = Account.Create(AccountType.Employee, "HTTP account", DateTime.UtcNow);
    public long TokenVersion { get; set; }
    public Guid? MerchantId { get; set; }
    public Guid? TokenMerchantId { get; set; }
    public string? TokenContext { get; set; }
    public string? RequiredPermission { get; set; }
    public Guid? TokenAccountId { get; set; }
}

file sealed class IdentityAuthorizationQuery(IdentityAuthorizationState state) : IIdentityAccessQuery
{
    public Task<Account?> FindAccountAsync(Guid accountId, CancellationToken cancellationToken) =>
        Task.FromResult<Account?>(accountId == state.Account.Id ? state.Account : null);

    public Task<SystemClientResolution?> FindSystemClientAsync(string clientId, CancellationToken cancellationToken) =>
        Task.FromResult<SystemClientResolution?>(null);

    public Task<AuthorizationSnapshot?> ResolveAuthorizationAsync(
        Guid accountId, Guid? merchantId, Guid? clientId, CancellationToken cancellationToken)
    {
        if (accountId != state.Account.Id || (merchantId is not null && merchantId != state.MerchantId))
            return Task.FromResult<AuthorizationSnapshot?>(null);
        return Task.FromResult<AuthorizationSnapshot?>(new AuthorizationSnapshot(
            state.Account.Id,
            state.Account.AccountType,
            state.Account.Status,
            state.Account.AuthorizationVersion,
            merchantId,
            Access.Domain.DataScope.Merchant,
            null,
            null,
            new HashSet<Guid>(),
            new HashSet<Guid>(),
            string.Equals(state.TokenContext, "PLATFORM", StringComparison.Ordinal)
                ? true
                : false,
            new HashSet<string>(state.RequiredPermission is null ? [] : [state.RequiredPermission], StringComparer.Ordinal),
            clientId));
    }

    public Task<IReadOnlyList<MerchantAccessSummary>> ListMerchantAccessAsync(
        Guid accountId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<MerchantAccessSummary>>([]);
}

file sealed class IdentityAuthorizationTestHandler(
    Microsoft.Extensions.Options.IOptionsMonitor<AuthenticationSchemeOptions> options,
    Microsoft.Extensions.Logging.ILoggerFactory logger,
    System.Text.Encodings.Web.UrlEncoder encoder,
    IdentityAuthorizationState state)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var identity = new ClaimsIdentity("IdentityAccessTest");
        identity.AddClaim(new Claim("sub", (state.TokenAccountId ?? state.Account.Id).ToString("D")));
        identity.AddClaim(new Claim("authz_version", state.TokenVersion.ToString()));
        if (state.TokenMerchantId is { } merchantId)
            identity.AddClaim(new Claim("merchant_id", merchantId.ToString("D")));
        if (state.TokenContext is not null)
            identity.AddClaim(new Claim("token_context", state.TokenContext));
        if (state.RequiredPermission is not null)
            identity.AddClaim(new Claim("required_permission", state.RequiredPermission));
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), "IdentityAccessTest")));
    }
}

file sealed class IdentityAuthorizationFactory : WebApplicationFactory<ApiHost::Program>
{
    public IdentityAuthorizationState State { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Development);
        builder.UseSetting("ConnectionStrings:Migrator", "");
        builder.UseSetting("ConnectionStrings:App", Task2AppConnection());
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
            services.AddSingleton(State);
            services.RemoveAll<IIdentityAccessQuery>();
            services.AddScoped<IIdentityAccessQuery>(sp =>
                new IdentityAuthorizationQuery(sp.GetRequiredService<IdentityAuthorizationState>()));
            services.AddAuthentication()
                .AddScheme<AuthenticationSchemeOptions, IdentityAuthorizationTestHandler>(
                    "IdentityAccessTest", _ => { });
            services.AddAuthorizationBuilder()
                .AddPolicy("identity-platform", policy => policy
                    .AddAuthenticationSchemes("IdentityAccessTest")
                    .RequireAuthenticatedUser()
                    .AddRequirements(new ApiIdentity.IdentityAccessRequirement()));
        });
    }

    private static string Task2AppConnection()
    {
        var server = Environment.GetEnvironmentVariable("POL_SQL_SERVER");
        var password = Environment.GetEnvironmentVariable("POL_APP_PASSWORD");
        if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(password))
            return "Server=(local);Database=pol_test;Trusted_Connection=True;";
        var database = Environment.GetEnvironmentVariable("POL_DB") ?? "PolIdentityAccessTask2Test";
        return $"Server={server};Database={database};User Id=pol_app;Password={password};Encrypt=True;TrustServerCertificate=True;Pooling=False";
    }
}

[Trait("Capability", "IdentityAccess")]
public sealed class IdentityAccessAuthorizationHttpTests
{
    [Fact]
    [Trait("Requirement", "REQ-3.4")]
    [Trait("Requirement", "REQ-3.11")]
    public async Task Authenticated_http_request_is_denied_when_authz_version_is_stale()
    {
        using var factory = new IdentityAuthorizationFactory();
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/api/v1/me");
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);

        factory.State.Account.BumpAuthorizationVersion(DateTime.UtcNow);
        response = await client.GetAsync("/api/v1/me");
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    [Trait("Requirement", "REQ-3.4")]
    public async Task Account_self_context_denies_a_subject_that_is_not_the_account_owner()
    {
        using var factory = new IdentityAuthorizationFactory();
        using var client = factory.CreateClient();
        factory.State.TokenAccountId = Guid.NewGuid();

        var response = await client.GetAsync("/api/v1/me");

        Assert.Equal(System.Net.HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    [Trait("Requirement", "REQ-3.5")]
    [Trait("Requirement", "REQ-3.8")]
    public async Task Platform_context_requires_employee_platform_access_and_permission()
    {
        using var factory = new IdentityAuthorizationFactory();
        using var client = factory.CreateClient();
        factory.State.TokenContext = "PLATFORM";
        factory.State.RequiredPermission = "access.manage";

        var response = await client.GetAsync("/api/v1/me");
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);

        factory.State.Account = Account.Create(AccountType.Agent, "Agent", DateTime.UtcNow);
        response = await client.GetAsync("/api/v1/me");
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    [Trait("Requirement", "REQ-3.6")]
    [Trait("Requirement", "REQ-3.7")]
    public async Task Merchant_context_requires_the_active_merchant_access_snapshot()
    {
        using var factory = new IdentityAuthorizationFactory();
        using var client = factory.CreateClient();
        factory.State.TokenContext = "MERCHANT";
        factory.State.MerchantId = Guid.NewGuid();
        factory.State.TokenMerchantId = factory.State.MerchantId;
        factory.State.RequiredPermission = "payment.view";

        var response = await client.GetAsync("/api/v1/me");
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);

        factory.State.MerchantId = Guid.NewGuid();
        response = await client.GetAsync("/api/v1/me");
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, response.StatusCode);
    }
}
