extern alias ApiHost;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Hosts.Tests;

public sealed class ConsoleConfigurationResolverTests
{
    [Fact] // REQ-1.3-1.9, REQ-2.1, REQ-8.1
    public void Canonical_settings_bind_every_session_and_cors_group()
    {
        var configuration = Configuration(new Dictionary<string, string?>
        {
            ["AdminSession:IdleMinutes"] = "31",
            ["AdminSession:AbsoluteHours"] = "41",
            ["AdminSession:RotationMinutes"] = "7",
            ["AdminSession:GraceSeconds"] = "8",
            ["AdminSession:SameSite"] = "None",
            ["AdminSession:PreAuthTtlMinutes"] = "9",
            ["AdminSession:DefaultReturnPath"] = "/dashboard",
            ["AdminSession:ReturnUrlAllowlist:0"] = "/",
            ["AdminSession:ReturnUrlAllowlist:1"] = "/dashboard",
            ["AdminSession:WebAppBaseUrl"] = "https://ADMIN.example.com:443/",
            ["AdminSession:ScalarBaseUrl"] = "https://api.example.com",
            ["MerchantSession:IdleMinutes"] = "51",
            ["MerchantSession:AbsoluteHours"] = "61",
            ["MerchantSession:RotationMinutes"] = "11",
            ["MerchantSession:GraceSeconds"] = "12",
            ["MerchantSession:SameSite"] = "Lax",
            ["MerchantSession:DefaultReturnPath"] = "/dashboard",
            ["MerchantSession:ReturnUrlAllowlist:0"] = "/",
            ["MerchantSession:ReturnUrlAllowlist:1"] = "/dashboard",
            ["MerchantSession:WebAppBaseUrl"] = "https://MERCHANT.example.com/",
            ["Cors:AdminOrigins:0"] = "https://ADMIN.example.com:443/",
            ["Cors:MerchantOrigins:0"] = "https://MERCHANT.example.com/",
        });

        var snapshot = Resolve(configuration);

        Assert.Equal(31, snapshot.AdminSession.IdleMinutes);
        Assert.Equal(41, snapshot.AdminSession.AbsoluteHours);
        Assert.Equal(7, snapshot.AdminSession.RotationMinutes);
        Assert.Equal(8, snapshot.AdminSession.GraceSeconds);
        Assert.Equal("None", snapshot.AdminSession.SameSite);
        Assert.Equal(9, snapshot.AdminSession.PreAuthTtlMinutes);
        Assert.Equal("/dashboard", snapshot.AdminSession.DefaultReturnPath);
        Assert.Equal(["/", "/dashboard"], snapshot.AdminSession.ReturnUrlAllowlist);
        Assert.Equal("https://admin.example.com", snapshot.AdminSession.WebAppBaseUrl);
        Assert.Equal("https://api.example.com", snapshot.AdminSession.ScalarBaseUrl);
        Assert.Equal(51, snapshot.MerchantSession.IdleMinutes);
        Assert.Equal(61, snapshot.MerchantSession.AbsoluteHours);
        Assert.Equal(11, snapshot.MerchantSession.RotationMinutes);
        Assert.Equal(12, snapshot.MerchantSession.GraceSeconds);
        Assert.Equal("Lax", snapshot.MerchantSession.SameSite);
        Assert.Equal("/dashboard", snapshot.MerchantSession.DefaultReturnPath);
        Assert.Equal(["/", "/dashboard"], snapshot.MerchantSession.ReturnUrlAllowlist);
        Assert.Equal("https://merchant.example.com", snapshot.MerchantSession.WebAppBaseUrl);
        Assert.Equal(["https://admin.example.com"], snapshot.Cors.AdminOrigins);
        Assert.Equal(["https://merchant.example.com"], snapshot.Cors.MerchantOrigins);
        Assert.Empty(snapshot.LegacyKeyFamilies);
    }

    [Fact] // REQ-2.2, REQ-2.8, REQ-8.2
    public void Legacy_only_settings_map_to_the_canonical_snapshot()
    {
        var configuration = Configuration(new Dictionary<string, string?>
        {
            ["AdminSession:DefaultReturnPath"] = "/",
            ["AdminSession:ReturnUrlAllowlist:0"] = "/",
            ["AdminSession:SpaBaseUrl"] = "https://admin.example.com/",
            ["MerchantUser:Session:IdleMinutes"] = "35",
            ["MerchantUser:Session:AbsoluteHours"] = "48",
            ["MerchantUser:Session:RotationMinutes"] = "6",
            ["MerchantUser:Session:GraceSeconds"] = "15",
            ["MerchantUser:Session:SameSite"] = "None",
            ["MerchantUser:Session:DefaultReturnPath"] = "/dashboard",
            ["MerchantUser:Session:ReturnUrlAllowlist:0"] = "/",
            ["MerchantUser:Session:ReturnUrlAllowlist:1"] = "/dashboard",
            ["MerchantUser:Session:SpaBaseUrl"] = "https://merchant.example.com/",
            ["Cors:AllowedOrigins:0"] = "https://merchant.example.com/",
        });

        var snapshot = Resolve(configuration);

        Assert.Equal("https://admin.example.com", snapshot.AdminSession.WebAppBaseUrl);
        Assert.Equal(35, snapshot.MerchantSession.IdleMinutes);
        Assert.Equal(48, snapshot.MerchantSession.AbsoluteHours);
        Assert.Equal(6, snapshot.MerchantSession.RotationMinutes);
        Assert.Equal(15, snapshot.MerchantSession.GraceSeconds);
        Assert.Equal("None", snapshot.MerchantSession.SameSite);
        Assert.Equal("/dashboard", snapshot.MerchantSession.DefaultReturnPath);
        Assert.Equal(["/", "/dashboard"], snapshot.MerchantSession.ReturnUrlAllowlist);
        Assert.Equal("https://merchant.example.com", snapshot.MerchantSession.WebAppBaseUrl);
        Assert.Equal(["https://merchant.example.com"], snapshot.Cors.MerchantOrigins);
        Assert.Equal(
            ["AdminSession:SpaBaseUrl", "MerchantUser:Session", "Cors:AllowedOrigins"],
            snapshot.LegacyKeyFamilies);
    }

    [Fact] // REQ-2.4, REQ-2.5
    public void Last_provider_wins_within_canonical_and_legacy_families()
    {
        var configuration = Configuration(
            new Dictionary<string, string?>
            {
                ["AdminSession:ReturnUrlAllowlist:0"] = "/",
                ["AdminSession:IdleMinutes"] = "10",
                ["MerchantUser:Session:IdleMinutes"] = "20",
                ["MerchantUser:Session:ReturnUrlAllowlist:0"] = "/",
            },
            new Dictionary<string, string?>
            {
                ["AdminSession:IdleMinutes"] = "11",
                ["MerchantUser:Session:IdleMinutes"] = "21",
            });

        var snapshot = Resolve(configuration);

        Assert.Equal(11, snapshot.AdminSession.IdleMinutes);
        Assert.Equal(21, snapshot.MerchantSession.IdleMinutes);
    }

    [Fact] // REQ-2.6, REQ-2.11-2.14, REQ-8.3
    public void Equivalent_canonical_and_legacy_values_use_canonical_after_normalization()
    {
        var configuration = Configuration(new Dictionary<string, string?>
        {
            ["AdminSession:ReturnUrlAllowlist:0"] = "/",
            ["AdminSession:WebAppBaseUrl"] = "HTTPS://ADMIN.EXAMPLE.COM:443/",
            ["AdminSession:SpaBaseUrl"] = "https://admin.example.com",
            ["MerchantSession:IdleMinutes"] = "60",
            ["MerchantUser:Session:IdleMinutes"] = "060",
            ["MerchantSession:DefaultReturnPath"] = "/dashboard",
            ["MerchantUser:Session:DefaultReturnPath"] = "/dashboard",
            ["MerchantSession:ReturnUrlAllowlist:0"] = "/",
            ["MerchantSession:ReturnUrlAllowlist:1"] = "/dashboard",
            ["MerchantUser:Session:ReturnUrlAllowlist:0"] = "/dashboard",
            ["MerchantUser:Session:ReturnUrlAllowlist:1"] = "/",
            ["MerchantSession:WebAppBaseUrl"] = "https://merchant.example.com",
            ["MerchantUser:Session:SpaBaseUrl"] = "HTTPS://MERCHANT.EXAMPLE.COM:443/",
            ["Cors:MerchantOrigins:0"] = "https://merchant.example.com",
            ["Cors:MerchantOrigins:1"] = "https://other.example.com:443/",
            ["Cors:AllowedOrigins:0"] = "HTTPS://OTHER.EXAMPLE.COM",
            ["Cors:AllowedOrigins:1"] = "https://merchant.example.com/",
        });

        var snapshot = Resolve(configuration);

        Assert.Equal("https://admin.example.com", snapshot.AdminSession.WebAppBaseUrl);
        Assert.Equal(60, snapshot.MerchantSession.IdleMinutes);
        Assert.Equal(["/", "/dashboard"], snapshot.MerchantSession.ReturnUrlAllowlist);
        Assert.Equal("https://merchant.example.com", snapshot.MerchantSession.WebAppBaseUrl);
        Assert.Equal(
            ["https://merchant.example.com", "https://other.example.com"],
            snapshot.Cors.MerchantOrigins);
    }

    [Fact] // REQ-2.7, REQ-2.9, REQ-8.4
    public void Conflicting_aliases_fail_with_key_names_and_without_values()
    {
        const string canonicalValue = "https://canonical.example.com";
        const string legacyValue = "https://legacy.example.com";
        var configuration = Configuration(new Dictionary<string, string?>
        {
            ["AdminSession:ReturnUrlAllowlist:0"] = "/",
            ["MerchantSession:ReturnUrlAllowlist:0"] = "/",
            ["MerchantSession:WebAppBaseUrl"] = canonicalValue,
            ["MerchantUser:Session:SpaBaseUrl"] = legacyValue,
        });

        var error = Assert.Throws<InvalidOperationException>(() => Resolve(configuration));

        Assert.Contains("MerchantSession:WebAppBaseUrl", error.Message, StringComparison.Ordinal);
        Assert.Contains("MerchantUser:Session:SpaBaseUrl", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(canonicalValue, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(legacyValue, error.Message, StringComparison.Ordinal);
    }

    [Fact] // REQ-2.14
    public void String_alias_comparison_is_ordinal()
    {
        var configuration = Configuration(new Dictionary<string, string?>
        {
            ["AdminSession:ReturnUrlAllowlist:0"] = "/",
            ["MerchantSession:ReturnUrlAllowlist:0"] = "/",
            ["MerchantSession:SameSite"] = "Lax",
            ["MerchantUser:Session:SameSite"] = "lax",
        });

        var error = Assert.Throws<InvalidOperationException>(() => Resolve(configuration));

        Assert.Contains("MerchantSession:SameSite", error.Message, StringComparison.Ordinal);
        Assert.Contains("MerchantUser:Session:SameSite", error.Message, StringComparison.Ordinal);
    }

    private static ApiHost::Api.ConsoleConfigurationSnapshot Resolve(IConfiguration configuration) =>
        new ApiHost::Api.ConsoleConfigurationResolver(configuration, new TestEnvironment()).Value;

    private static ConfigurationManager Configuration(params Dictionary<string, string?>[] providers)
    {
        var configuration = new ConfigurationManager();
        foreach (var provider in providers)
            configuration.AddInMemoryCollection(provider);
        return configuration;
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "Hosts.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
