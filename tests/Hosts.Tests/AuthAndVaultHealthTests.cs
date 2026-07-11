using BuildingBlocks.Infrastructure.Vault;
using BuildingBlocks.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Hosts.Tests;

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
