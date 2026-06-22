using BuildingBlocks.Infrastructure.Vault;
using BuildingBlocks.Web;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Hosts.Tests;

/// <summary>
/// The single API serves more than one browser SPA, each with its own Google OAuth client, so a token's
/// audience may be ANY configured client id. These pin that <c>Google:ClientIds</c> binds EVERY id as a valid
/// audience (not just the first), and that the non-Development fail-fast still fires when every id is a
/// placeholder.
/// </summary>
public sealed class GoogleMultiAudienceTests
{
    [Fact]
    public void Every_configured_client_id_is_a_valid_audience()
    {
        string[] ids = ["111-tenant.apps.googleusercontent.com", "222-admin.apps.googleusercontent.com"];

        var provider = new ServiceCollection()
            .AddGoogleIdTokenAuthentication(ConfigIds(ids), Env(Environments.Production))
            .BuildServiceProvider();
        var options = provider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme);

        Assert.Equal(ids, options.TokenValidationParameters.ValidAudiences);
    }

    [Fact]
    public void Non_development_host_refuses_when_every_client_id_is_a_placeholder()
    {
        string[] placeholders =
            ["REPLACE_WITH_TENANT_SPA_GOOGLE_CLIENT_ID.apps.googleusercontent.com",
             "REPLACE_WITH_ADMIN_SPA_GOOGLE_CLIENT_ID.apps.googleusercontent.com"];

        Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddGoogleIdTokenAuthentication(ConfigIds(placeholders), Env(Environments.Production)));
    }

    private static IConfiguration ConfigIds(string[] clientIds)
    {
        var values = new Dictionary<string, string?>();
        for (var i = 0; i < clientIds.Length; i++)
            values[$"Google:ClientIds:{i}"] = clientIds[i];
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
/// must refuse to boot when Google:ClientId is unset or still the committed placeholder (otherwise it would
/// "validate" tokens against a meaningless audience). Development keeps the escape hatch. These pin all
/// three regression vectors (removing the throw, dropping the !IsDevelopment guard, breaking the placeholder
/// check) — none of which the Development-only WebApplicationFactory tests would catch.
/// </summary>
public sealed class GoogleAuthGuardTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("REPLACE_WITH_TENANT_CONSOLE_GOOGLE_CLIENT_ID.apps.googleusercontent.com")]
    public void Non_development_host_refuses_to_start_with_an_unset_or_placeholder_client_id(string? clientId)
    {
        Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddGoogleIdTokenAuthentication(Config(clientId), Env(Environments.Production)));
    }

    [Fact]
    public void Non_development_host_starts_with_a_real_client_id()
    {
        var exception = Record.Exception(() =>
            new ServiceCollection().AddGoogleIdTokenAuthentication(
                Config("1234567890-abc.apps.googleusercontent.com"), Env(Environments.Production)));

        Assert.Null(exception);
    }

    [Fact]
    public void Development_host_may_start_with_an_unset_client_id()
    {
        var exception = Record.Exception(() =>
            new ServiceCollection().AddGoogleIdTokenAuthentication(Config(null), Env(Environments.Development)));

        Assert.Null(exception);
    }

    private static IConfiguration Config(string? clientId) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Google:ClientId"] = clientId })
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
