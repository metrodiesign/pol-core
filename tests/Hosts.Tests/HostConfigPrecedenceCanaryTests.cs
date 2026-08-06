extern alias ApiHost;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Persistence.MerchantRuntime;

namespace Hosts.Tests;

// Precedence canary (host-test-config-precedence REQ-4): Program.cs reads GetConnectionString("App") at
// build time (Program.cs:137), BEFORE the deferred sources a factory's ConfigureAppConfiguration adds are
// applied. This factory deliberately sets the SAME key through both channels with CONFLICTING markers, so a
// green run proves "the UseSetting value actually reached the build-time read" — not "both sources happened
// to agree" (the silent fallback that let PR #184 stay green on dev and fail only on CI). The conflicting
// deferred value lives in this file, not in any machine-local appsettings, so the outcome is identical on
// every machine. If factory precedence ever changes (framework upgrade), this is the test that turns red.
//
// NOTE: this is the ONE test allowed to put "ConnectionStrings" inside ConfigureAppConfiguration — the
// HostTestConfigGateTests ban (Architecture.Tests) exempts exactly this file, because planting the losing
// value in the slow channel is the point of the canary.
file sealed class PrecedenceCanaryFactory : WebApplicationFactory<ApiHost::Program>
{
    // Markers are fake, credential-free connection strings; only their Database= token matters.
    public const string UseSettingConn = "Server=(local);Database=UseSettingWins;Trusted_Connection=True;";
    public const string DeferredConn = "Server=(local);Database=DeferredSourceLoses;Trusted_Connection=True;";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Development);
        // Dev-convenience auto-migrate reads this key; blank it so no machine-local Migrator connection is touched.
        builder.UseSetting("ConnectionStrings:Migrator", "");
        builder.UseSetting("ConnectionStrings:App", UseSettingConn);
        builder.UseSetting("ConnectionStrings:Admin", UseSettingConn);
        builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Same key, conflicting marker, in the lower-precedence deferred source (REQ-4.2).
                ["ConnectionStrings:App"] = DeferredConn,
                ["Vault:MasterKeyBase64"] = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
            }));
    }
}

public sealed class HostConfigPrecedenceCanaryTests
{
    [Fact]
    public void The_connection_string_the_host_actually_uses_is_the_UseSetting_value_not_the_deferred_one()
    {
        using var factory = new PrecedenceCanaryFactory();
        using var scope = factory.Services.CreateScope();

        // The connection string the host ACTUALLY uses: Program.cs captured it at build time and passed it
        // into AddMerchantRuntimePersistence. GetDbConnection() only constructs the connection object — it is
        // never opened, so this runs with no SQL Server anywhere (REQ-4.4).
        var actual = scope.ServiceProvider.GetRequiredService<MerchantRuntimeDbContext>()
            .Database.GetDbConnection().ConnectionString;

        // Compare by Database= token so a failure message never carries a machine-local credential.
        var actualCatalog = new SqlConnectionStringBuilder(actual).InitialCatalog;
        var expectedCatalog = new SqlConnectionStringBuilder(PrecedenceCanaryFactory.UseSettingConn).InitialCatalog;
        var deferredCatalog = new SqlConnectionStringBuilder(PrecedenceCanaryFactory.DeferredConn).InitialCatalog;

        Assert.True(
            actualCatalog == expectedCatalog,
            $"Host config precedence broke: the host booted with Database={actualCatalog}, " +
            $"but the test set Database={expectedCatalog} via UseSetting " +
            $"(conflicting deferred ConfigureAppConfiguration value: Database={deferredCatalog}). " +
            "UseSetting must win and reach Program.cs's build-time GetConnectionString(\"App\") read; " +
            "a mismatch means test-set config silently fell back to another source (see " +
            ".ai/specs/host-test-config-precedence/requirements.md).");
    }
}
