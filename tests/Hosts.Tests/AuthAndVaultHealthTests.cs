using System.Security.Claims;
using BuildingBlocks.Infrastructure.Vault;
using BuildingBlocks.Web;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Hosts.Tests;

/// <summary>
/// The single API serves more than one browser SPA, each with its own Google OAuth client AND role.
/// <c>Google:Audiences</c> maps role -> client id. These pin that every mapped id is a valid token audience,
/// that the validated audience resolves to the right role, that a per-role authorization policy is registered,
/// and that a tenant-role principal is admitted by the "tenant" policy while an admin-role one is rejected —
/// the boundary that stops an admin-SPA token from reaching a tenant route.
/// </summary>
public sealed class GoogleMultiAudienceTests
{
    private const string TenantAud = "111-tenant.apps.googleusercontent.com";
    private const string AdminAud = "222-admin.apps.googleusercontent.com";

    [Fact]
    public void Every_mapped_audience_is_a_valid_audience()
    {
        var options = BuildAuth().GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme);

        var valid = options.TokenValidationParameters.ValidAudiences.ToArray();
        Assert.Contains(TenantAud, valid);
        Assert.Contains(AdminAud, valid);
        Assert.Equal(2, valid.Length);
    }

    [Theory]
    [InlineData(TenantAud, "tenant")]
    [InlineData(AdminAud, "admin")]
    [InlineData("999-unknown.apps.googleusercontent.com", null)]
    [InlineData(null, null)]
    public void The_validated_audience_resolves_to_its_role(string? audience, string? expectedRole) =>
        Assert.Equal(expectedRole, GoogleAuthenticationExtensions.RoleForAudience(Audiences(), audience));

    [Fact]
    public async Task A_tenant_role_principal_passes_the_tenant_policy_but_an_admin_role_principal_does_not()
    {
        var authz = BuildAuth().GetRequiredService<IAuthorizationService>();

        Assert.True((await authz.AuthorizeAsync(Principal("tenant"), "tenant")).Succeeded);
        Assert.False((await authz.AuthorizeAsync(Principal("admin"), "tenant")).Succeeded);
        Assert.True((await authz.AuthorizeAsync(Principal("admin"), "admin")).Succeeded);
    }

    [Fact]
    public void Non_development_host_refuses_when_every_audience_is_a_placeholder() =>
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddGoogleIdTokenAuthentication(
            ConfigAudiences(new()
            {
                ["tenant"] = "REPLACE_WITH_TENANT_SPA_GOOGLE_CLIENT_ID.apps.googleusercontent.com",
                ["admin"] = "REPLACE_WITH_ADMIN_SPA_GOOGLE_CLIENT_ID.apps.googleusercontent.com",
            }), Env(Environments.Production)));

    private static Dictionary<string, string> Audiences() => new() { ["tenant"] = TenantAud, ["admin"] = AdminAud };

    private static ServiceProvider BuildAuth() => new ServiceCollection()
        .AddLogging() // DefaultAuthorizationService needs ILogger
        .AddGoogleIdTokenAuthentication(ConfigAudiences(Audiences()), Env(Environments.Production))
        .BuildServiceProvider();

    private static ClaimsPrincipal Principal(string role) =>
        new(new ClaimsIdentity([new Claim(GoogleAuthenticationExtensions.RoleClaimType, role)], "test"));

    private static IConfiguration ConfigAudiences(Dictionary<string, string> audiences)
    {
        var values = new Dictionary<string, string?>();
        foreach (var (role, id) in audiences)
            values[$"Google:Audiences:{role}"] = id;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static IHostEnvironment Env(string environmentName) =>
        new StubEnvironment { EnvironmentName = environmentName };

    private sealed class StubEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "Hosts.Tests";
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}

/// <summary>
/// The security-critical branch of the Google auth wiring is the one that THROWS: a non-Development host
/// must refuse to boot when Google:Audiences is unset or still the committed placeholder (otherwise it would
/// "validate" tokens against a meaningless audience). Development keeps the escape hatch. These pin all
/// three regression vectors (removing the throw, dropping the !IsDevelopment guard, breaking the placeholder
/// check) — none of which the Development-only WebApplicationFactory tests would catch.
/// </summary>
public sealed class GoogleAuthGuardTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("REPLACE_WITH_TENANT_SPA_GOOGLE_CLIENT_ID.apps.googleusercontent.com")]
    public void Non_development_host_refuses_to_start_with_an_unset_or_placeholder_audience(string? clientId)
    {
        Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddGoogleIdTokenAuthentication(Config(clientId), Env(Environments.Production)));
    }

    [Fact]
    public void Non_development_host_starts_with_a_real_audience()
    {
        var exception = Record.Exception(() =>
            new ServiceCollection().AddGoogleIdTokenAuthentication(
                Config("1234567890-abc.apps.googleusercontent.com"), Env(Environments.Production)));

        Assert.Null(exception);
    }

    [Fact]
    public void Development_host_may_start_with_an_unset_audience()
    {
        var exception = Record.Exception(() =>
            new ServiceCollection().AddGoogleIdTokenAuthentication(Config(null), Env(Environments.Development)));

        Assert.Null(exception);
    }

    private static IConfiguration Config(string? clientId) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Google:Audiences:tenant"] = clientId })
            .Build();

    private static IHostEnvironment Env(string environmentName) =>
        new StubEnvironment { EnvironmentName = environmentName };

    private sealed class StubEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "Hosts.Tests";
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}

/// <summary>
/// The vault readiness probe is one of two checks behind /health/ready. Its failure paths are otherwise
/// only reachable with a live database (the end-to-end readiness test fails on the DB branch), so these
/// unit tests pin the vault branches directly — including that the failure description never echoes the key.
/// </summary>
public sealed class VaultReadinessCheckTests
{
    [Fact]
    public async Task A_missing_master_key_is_unhealthy()
    {
        var result = await Check(null);
        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    [Fact]
    public async Task A_malformed_master_key_is_unhealthy_and_does_not_echo_the_key()
    {
        const string badKey = "this-is-not-valid-base64-32-bytes";
        var result = await Check(badKey);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.DoesNotContain(badKey, result.Description ?? string.Empty);
    }

    [Fact]
    public async Task A_valid_32_byte_master_key_is_healthy()
    {
        var result = await Check(Convert.ToBase64String(new byte[32]));
        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    // Build the keyring via the real factory (legacy MasterKeyBase64 shim) behind a provider, exactly as the
    // host wires it; a missing/malformed key makes the factory throw, which the readiness check turns into
    // not-ready (never a 500, never echoing the key).
    private static Task<HealthCheckResult> Check(string? masterKeyBase64)
    {
        var services = new ServiceCollection()
            .AddSingleton(_ => VaultKeyringFactory.Build(new VaultOptions { MasterKeyBase64 = masterKeyBase64 ?? string.Empty }))
            .BuildServiceProvider();
        return new VaultReadinessCheck(services).CheckHealthAsync(new HealthCheckContext());
    }
}
