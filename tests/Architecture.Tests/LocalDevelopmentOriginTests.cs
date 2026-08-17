using System.Text.Json;

namespace Architecture.Tests;

public sealed class LocalDevelopmentOriginTests
{
    private const string ApiOrigin = "https://localhost:5001";
    private const string MerchantMicrosoftAuthority =
        "https://vcpexternaldev.ciamlogin.com/2a6d4554-88f1-4089-a995-0bf31c622493/v2.0";
    private const string MerchantMicrosoftClientId = "dd7d2f17-60dc-4bd9-99a4-e2a93077bc9a";
    private const string MerchantMicrosoftCallbackPath = "/api/v1/merchants/auth/microsoft/callback";
    private const string CustomerOrigin = "https://localhost:3000";
    private const string AdminOrigin = "https://localhost:3001";
    private const string MerchantOrigin = "https://localhost:3002";

    [Fact]
    public void Committed_local_examples_pin_api_and_spa_origins()
    {
        var root = FindRepoRoot();

        using var launch = ReadJson(root, "src/Hosts/Api/Properties/launchSettings.json");
        var profiles = launch.RootElement.GetProperty("profiles");
        var profile = Assert.Single(profiles.EnumerateObject());
        Assert.Equal("https", profile.Name);
        Assert.Equal(ApiOrigin, profile.Value.GetProperty("applicationUrl").GetString());
        var launchEnvironment = profile.Value.GetProperty("environmentVariables");
        Assert.Equal(MerchantOrigin,
            launchEnvironment.GetProperty("MerchantUser__Session__SpaBaseUrl").GetString());
        Assert.Equal(MerchantMicrosoftAuthority,
            launchEnvironment.GetProperty("MerchantAuth__Providers__Microsoft__Authority").GetString());
        Assert.Equal(MerchantMicrosoftClientId,
            launchEnvironment.GetProperty("MerchantAuth__Providers__Microsoft__ClientId").GetString());
        Assert.Equal(MerchantMicrosoftCallbackPath,
            launchEnvironment.GetProperty("MerchantAuth__Providers__Microsoft__CallbackPath").GetString());
        Assert.False(launchEnvironment.TryGetProperty(
            "MerchantAuth__Providers__Microsoft__ClientSecret", out _));

        using var settings = ReadJson(root, "src/Hosts/Api/appsettings.Development.json.example");
        var config = settings.RootElement;
        Assert.Equal(AdminOrigin, config.GetProperty("AdminSession").GetProperty("SpaBaseUrl").GetString());
        Assert.Equal(ApiOrigin, config.GetProperty("AdminSession").GetProperty("ScalarBaseUrl").GetString());
        Assert.Equal(MerchantOrigin,
            config.GetProperty("MerchantUser").GetProperty("Session").GetProperty("SpaBaseUrl").GetString());
        Assert.Equal([MerchantOrigin], Strings(config.GetProperty("Cors").GetProperty("AllowedOrigins")));
        Assert.Equal([AdminOrigin], Strings(config.GetProperty("Cors").GetProperty("AdminOrigins")));

        var psp = config.GetProperty("Psp");
        Assert.Equal(ApiOrigin, psp.GetProperty("PublicBaseUrl").GetString());
        Assert.Equal(CustomerOrigin + "/checkout/return",
            psp.GetProperty("TwoCTwoP").GetProperty("FrontendReturnUrl").GetString());
        Assert.Equal(CustomerOrigin + "/checkout/return",
            psp.GetProperty("Omise").GetProperty("ReturnUri").GetString());

        var envExample = File.ReadAllText(Path.Combine(root, ".env.example"));
        Assert.Contains($"AdminSession__SpaBaseUrl={AdminOrigin}", envExample, StringComparison.Ordinal);
        Assert.Contains($"MerchantUser__Session__SpaBaseUrl={MerchantOrigin}", envExample, StringComparison.Ordinal);
        Assert.Contains($"Psp__TwoCTwoP__FrontendReturnUrl={CustomerOrigin}/checkout/return", envExample,
            StringComparison.Ordinal);
        Assert.Contains($"Psp__Omise__ReturnUri={CustomerOrigin}/checkout/return", envExample,
            StringComparison.Ordinal);
        Assert.DoesNotContain("localhost:5200", envExample, StringComparison.Ordinal);
        Assert.DoesNotContain("localhost:5300", envExample, StringComparison.Ordinal);
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
