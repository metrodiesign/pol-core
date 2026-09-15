using System.Text.Json;

namespace Architecture.Tests;

public sealed class LocalDevelopmentOriginTests
{
    private const string ApiOrigin = "https://localhost:5001";
    private const string MerchantMicrosoftAuthority =
        "https://viriyahexternal.ciamlogin.com/1aee3cad-1e4d-4de5-9e25-424d0d12520b/v2.0";
    private const string MerchantMicrosoftClientId = "fb0e40a7-afbc-42c4-a025-b87f74a96321";
    private const string MerchantMicrosoftCallbackPath = "/api/v1/merchants/auth/microsoft/callback";
    private const string CustomerOrigin = "https://localhost:3000";
    private const string AdminOrigin = "https://localhost:3001";
    private const string MerchantOrigin = "https://localhost:3002";

    [Fact]
    public void Committed_local_examples_pin_api_and_spa_origins()
    {
        var root = FindRepoRoot();

        using var launch = ReadJson(root, "src/Api/Properties/launchSettings.json");
        var profiles = launch.RootElement.GetProperty("profiles");
        var profile = Assert.Single(profiles.EnumerateObject());
        Assert.Equal("https", profile.Name);
        Assert.Equal(ApiOrigin, profile.Value.GetProperty("applicationUrl").GetString());
        var launchEnvironment = profile.Value.GetProperty("environmentVariables");
        Assert.Equal(MerchantOrigin,
            launchEnvironment.GetProperty("MerchantSession__WebAppBaseUrl").GetString());
        Assert.Equal(MerchantMicrosoftAuthority,
            launchEnvironment.GetProperty("MerchantAuth__Providers__Microsoft__Authority").GetString());
        Assert.Equal(MerchantMicrosoftClientId,
            launchEnvironment.GetProperty("MerchantAuth__Providers__Microsoft__ClientId").GetString());
        Assert.Equal(MerchantMicrosoftCallbackPath,
            launchEnvironment.GetProperty("MerchantAuth__Providers__Microsoft__CallbackPath").GetString());
        Assert.False(launchEnvironment.TryGetProperty(
            "MerchantAuth__Providers__Microsoft__ClientSecret", out _));

        using var settings = ReadJson(root, "src/Api/appsettings.Development.json.example");
        var config = settings.RootElement;
        Assert.Equal(AdminOrigin, config.GetProperty("AdminSession").GetProperty("WebAppBaseUrl").GetString());
        Assert.Equal(ApiOrigin, config.GetProperty("AdminSession").GetProperty("ScalarBaseUrl").GetString());
        Assert.Equal(MerchantOrigin,
            config.GetProperty("MerchantSession").GetProperty("WebAppBaseUrl").GetString());
        Assert.Equal([MerchantOrigin], Strings(config.GetProperty("Cors").GetProperty("MerchantOrigins")));
        Assert.Equal([AdminOrigin], Strings(config.GetProperty("Cors").GetProperty("AdminOrigins")));

        var psp = config.GetProperty("Psp");
        Assert.Equal(ApiOrigin, psp.GetProperty("PublicBaseUrl").GetString());
        Assert.Equal(CustomerOrigin + "/checkout/return",
            psp.GetProperty("TwoCTwoP").GetProperty("FrontendReturnUrl").GetString());
        Assert.Equal(CustomerOrigin + "/checkout/return",
            psp.GetProperty("Omise").GetProperty("ReturnUri").GetString());

        var envExample = File.ReadAllText(Path.Combine(root, ".env.example"));
        Assert.Contains($"AdminSession__WebAppBaseUrl={AdminOrigin}", envExample, StringComparison.Ordinal);
        Assert.DoesNotContain("AdminSession__ReturnUrlAllowlist", envExample, StringComparison.Ordinal);
        Assert.Contains("Cors__AdminOrigins__0=", envExample, StringComparison.Ordinal);
        Assert.Contains($"MerchantSession__WebAppBaseUrl={MerchantOrigin}", envExample, StringComparison.Ordinal);
        Assert.Contains("MerchantSession__ReturnUrlAllowlist__0=/", envExample, StringComparison.Ordinal);
        Assert.Contains("MerchantSession__ReturnUrlAllowlist__1=/dashboard", envExample, StringComparison.Ordinal);
        Assert.Contains("Cors__MerchantOrigins__0=", envExample, StringComparison.Ordinal);
        foreach (var side in new[] { "MerchantAuth__Providers__Microsoft", "IdentityAccess__Workforce" })
        {
            Assert.Contains($"{side}__Authority", envExample, StringComparison.Ordinal);
            Assert.Contains($"{side}__ClientId", envExample, StringComparison.Ordinal);
            Assert.Contains($"{side}__ClientSecret", envExample, StringComparison.Ordinal);
            Assert.Contains($"{side}__CallbackPath", envExample, StringComparison.Ordinal);
        }
        Assert.DoesNotContain("AdminAuth__", envExample, StringComparison.Ordinal);
        Assert.Contains($"Psp__TwoCTwoP__FrontendReturnUrl={CustomerOrigin}/checkout/return", envExample,
            StringComparison.Ordinal);
        Assert.Contains($"Psp__Omise__ReturnUri={CustomerOrigin}/checkout/return", envExample,
            StringComparison.Ordinal);
        Assert.DoesNotContain("localhost:5200", envExample, StringComparison.Ordinal);
        Assert.DoesNotContain("localhost:5300", envExample, StringComparison.Ordinal);
        Assert.DoesNotContain("AdminSession__SpaBaseUrl", envExample, StringComparison.Ordinal);
        Assert.DoesNotContain("MerchantUser__Session", envExample, StringComparison.Ordinal);
        Assert.DoesNotContain("Cors__AllowedOrigins", envExample, StringComparison.Ordinal);

        var compose = File.ReadAllText(Path.Combine(root, "docker-compose.prod.yml"));
        Assert.Contains("AdminSession__WebAppBaseUrl: ${ADMIN_FRONTEND_ORIGIN}", compose, StringComparison.Ordinal);
        Assert.Contains("MerchantSession__WebAppBaseUrl: ${MERCHANT_USER_FRONTEND_ORIGIN}", compose, StringComparison.Ordinal);
        Assert.Contains("Cors__AdminOrigins__0: ${ADMIN_FRONTEND_ORIGIN", compose, StringComparison.Ordinal);
        Assert.Contains("Cors__MerchantOrigins__0: ${MERCHANT_USER_FRONTEND_ORIGIN", compose, StringComparison.Ordinal);
        Assert.DoesNotContain("AdminSession__SpaBaseUrl", compose, StringComparison.Ordinal);
        Assert.DoesNotContain("MerchantUser__Session", compose, StringComparison.Ordinal);
        Assert.DoesNotContain("Cors__AllowedOrigins", compose, StringComparison.Ordinal);
    }

    private static string[] Strings(JsonElement array) =>
        array.EnumerateArray().Select(value => value.GetString()!).ToArray();

    private static JsonDocument ReadJson(string root, string path) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(root, path)));

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "pol-core.slnx")))
            directory = directory.Parent;

        Assert.NotNull(directory);
        return directory.FullName;
    }
}
