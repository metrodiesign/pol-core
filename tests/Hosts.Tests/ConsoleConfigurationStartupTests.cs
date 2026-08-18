extern alias ApiHost;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hosts.Tests;

public sealed class ConsoleConfigurationStartupTests
{
    public static TheoryData<string, string, string> InvalidWebAppOrigins => new()
    {
        { "AdminSession:WebAppBaseUrl", "admin.example.com", Environments.Development },
        { "AdminSession:WebAppBaseUrl", "ftp://admin.example.com", Environments.Development },
        { "AdminSession:WebAppBaseUrl", "https://user@admin.example.com", Environments.Development },
        { "AdminSession:WebAppBaseUrl", "https://admin.example.com?x=1", Environments.Development },
        { "AdminSession:WebAppBaseUrl", "https://admin.example.com#fragment", Environments.Development },
        { "AdminSession:WebAppBaseUrl", "https://admin.example.com/path", Environments.Development },
        { "AdminSession:WebAppBaseUrl", "http://admin.example.com", Environments.Development },
        { "AdminSession:WebAppBaseUrl", "http://localhost:3001", Environments.Production },
    };

    public static TheoryData<string> InvalidReturnPaths => new()
    {
        "dashboard",
        "//evil.example.com",
        "/dashboard\\next",
        "/dashboard\u0001next",
    };

    public static TheoryData<string, string> InvalidCorsOrigins => new()
    {
        { "app.example.com", Environments.Development },
        { "ftp://app.example.com", Environments.Development },
        { "https://user@app.example.com", Environments.Development },
        { "https://app.example.com?x=1", Environments.Development },
        { "https://app.example.com#fragment", Environments.Development },
        { "https://app.example.com/path", Environments.Development },
        { "https://*.example.com", Environments.Development },
        { "http://app.example.com", Environments.Development },
        { "http://localhost:3002", Environments.Production },
    };

    [Fact] // REQ-2.3, REQ-2.9, REQ-8.2
    public async Task Startup_logs_one_key_only_warning_per_legacy_family()
    {
        const string sensitiveLookingValue = "https://legacy-value.example.com";
        var values = ValidCanonical();
        values.Remove("MerchantSession:DefaultReturnPath");
        values.Remove("MerchantSession:ReturnUrlAllowlist:0");
        values.Remove("MerchantSession:ReturnUrlAllowlist:1");
        values["AdminSession:SpaBaseUrl"] = sensitiveLookingValue;
        values["MerchantUser:Session:DefaultReturnPath"] = "/dashboard";
        values["MerchantUser:Session:ReturnUrlAllowlist:0"] = "/";
        values["MerchantUser:Session:ReturnUrlAllowlist:1"] = "/dashboard";
        values["MerchantUser:Session:SpaBaseUrl"] = sensitiveLookingValue;
        values["Cors:AllowedOrigins:0"] = sensitiveLookingValue;
        var logger = new CapturingLogger<ApiHost::Api.ConsoleConfigurationStartupService>();
        var service = StartupService(Configuration(values), Environments.Development, logger);

        await service.StartAsync(CancellationToken.None);

        Assert.Equal(3, logger.Messages.Count);
        Assert.Single(logger.Messages, message => message.Contains("AdminSession:SpaBaseUrl", StringComparison.Ordinal));
        Assert.Single(logger.Messages, message => message.Contains("MerchantUser:Session", StringComparison.Ordinal));
        Assert.Single(logger.Messages, message => message.Contains("Cors:AllowedOrigins", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Messages,
            message => message.Contains(sensitiveLookingValue, StringComparison.Ordinal));
    }

    [Theory] // REQ-3.3-3.6, REQ-3.17-3.18, REQ-8.5
    [MemberData(nameof(InvalidWebAppOrigins))]
    public async Task Invalid_web_app_origins_fail_startup_without_echoing_the_value(
        string key,
        string value,
        string environmentName)
    {
        var values = ValidCanonical();
        values[key] = value;

        var error = await StartFailure(values, environmentName);

        Assert.Contains(key, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(value, error.Message, StringComparison.Ordinal);
    }

    [Theory] // REQ-3.7, REQ-3.19, REQ-8.5
    [MemberData(nameof(InvalidReturnPaths))]
    public async Task Invalid_return_paths_fail_startup(string path)
    {
        var values = ValidCanonical();
        values["AdminSession:DefaultReturnPath"] = path;
        values["AdminSession:ReturnUrlAllowlist:0"] = path;

        var error = await StartFailure(values, Environments.Development);

        Assert.Contains("AdminSession:ReturnUrlAllowlist", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(path, error.Message, StringComparison.Ordinal);
    }

    [Fact] // REQ-3.8, REQ-8.5
    public async Task Duplicate_return_paths_fail_startup_under_ordinal_comparison()
    {
        var values = ValidCanonical();
        values["AdminSession:ReturnUrlAllowlist:1"] = "/";

        var error = await StartFailure(values, Environments.Development);

        Assert.Contains("AdminSession:ReturnUrlAllowlist", error.Message, StringComparison.Ordinal);
    }

    [Fact] // REQ-3.9, REQ-8.5
    public async Task Default_return_path_missing_from_allowlist_fails_startup()
    {
        var values = ValidCanonical();
        values["AdminSession:DefaultReturnPath"] = "/missing";

        var error = await StartFailure(values, Environments.Development);

        Assert.Contains("AdminSession:DefaultReturnPath", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("/missing", error.Message, StringComparison.Ordinal);
    }

    [Theory] // REQ-3.10-3.14, REQ-3.17-3.18, REQ-8.5
    [MemberData(nameof(InvalidCorsOrigins))]
    public async Task Invalid_cors_origins_fail_startup_without_echoing_the_value(
        string origin,
        string environmentName)
    {
        var values = ValidCanonical();
        values["Cors:MerchantOrigins:0"] = origin;

        var error = await StartFailure(values, environmentName);

        Assert.Contains("Cors:MerchantOrigins", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(origin, error.Message, StringComparison.Ordinal);
    }

    [Fact] // REQ-3.15, REQ-8.5
    public async Task Duplicate_normalized_cors_origins_fail_startup()
    {
        var values = ValidCanonical();
        values["Cors:MerchantOrigins:0"] = "https://APP.example.com:443/";
        values["Cors:MerchantOrigins:1"] = "https://app.example.com";

        var error = await StartFailure(values, Environments.Development);

        Assert.Contains("Cors:MerchantOrigins", error.Message, StringComparison.Ordinal);
    }

    [Fact] // REQ-3.20
    public async Task Configured_merchant_invitation_smtp_requires_a_web_app_base_url()
    {
        var values = ValidCanonical();
        values["MerchantUser:Invitation:Smtp:Host"] = "smtp.example.com";

        var error = await StartFailure(values, Environments.Development);

        Assert.Contains("MerchantSession:WebAppBaseUrl", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("smtp.example.com", error.Message, StringComparison.Ordinal);
    }

    [Theory] // REQ-3.2, REQ-3.5-3.6, REQ-3.16, REQ-8.6
    [InlineData("Development", "http://localhost:3001", "http://127.0.0.1:3002")]
    [InlineData("Production", "https://admin.example.com", "https://merchant.example.com")]
    public async Task Valid_environment_transport_and_empty_cors_lists_start_successfully(
        string environmentName,
        string adminBaseUrl,
        string merchantBaseUrl)
    {
        var values = ValidCanonical();
        values["AdminSession:WebAppBaseUrl"] = adminBaseUrl;
        values["MerchantSession:WebAppBaseUrl"] = merchantBaseUrl;
        var logger = new CapturingLogger<ApiHost::Api.ConsoleConfigurationStartupService>();
        var service = StartupService(Configuration(values), environmentName, logger);

        await service.StartAsync(CancellationToken.None);

        var snapshot = service.Resolver.Value;
        Assert.Equal(adminBaseUrl, snapshot.AdminSession.WebAppBaseUrl);
        Assert.Equal(merchantBaseUrl, snapshot.MerchantSession.WebAppBaseUrl);
        Assert.Empty(snapshot.Cors.AdminOrigins);
        Assert.Empty(snapshot.Cors.MerchantOrigins);
    }

    [Fact] // REQ-3.2, REQ-4.6
    public async Task Blank_web_app_base_urls_preserve_same_origin_configuration()
    {
        var logger = new CapturingLogger<ApiHost::Api.ConsoleConfigurationStartupService>();
        var service = StartupService(
            Configuration(ValidCanonical()), Environments.Development, logger);

        await service.StartAsync(CancellationToken.None);

        Assert.Equal(string.Empty, service.Resolver.Value.AdminSession.WebAppBaseUrl);
        Assert.Equal(string.Empty, service.Resolver.Value.MerchantSession.WebAppBaseUrl);
    }

    [Fact] // REQ-2.8
    public void Legacy_operator_value_overrides_the_committed_canonical_base_default_without_conflict()
    {
        var configuration = new ConfigurationManager();
        configuration.SetBasePath(FindRepoRoot());
        configuration.AddJsonFile("src/Hosts/Api/appsettings.json", optional: false, reloadOnChange: false);
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MerchantUser:Session:IdleMinutes"] = "37",
        });

        var snapshot = new ApiHost::Api.ConsoleConfigurationResolver(
            configuration, new TestEnvironment(Environments.Development)).Value;

        Assert.Equal(37, snapshot.MerchantSession.IdleMinutes);
    }

    [Fact] // REQ-3.1
    public async Task Service_registration_exposes_all_options_from_one_startup_validated_snapshot()
    {
        var configuration = Configuration(ValidCanonical());
        var services = new ServiceCollection();
        services.AddLogging();
        ApiHost::Api.ConsoleConfigurationServiceCollectionExtensions.AddConsoleConfiguration(
            services, configuration, new TestEnvironment(Environments.Development));
        await using var provider = services.BuildServiceProvider();

        foreach (var hosted in provider.GetServices<IHostedService>())
            await hosted.StartAsync(CancellationToken.None);

        var snapshot = provider.GetRequiredService<ApiHost::Api.ConsoleConfigurationResolver>().Value;
        Assert.Same(snapshot.AdminSession,
            provider.GetRequiredService<IOptions<ApiHost::Api.Admins.AdminSessionOptions>>().Value);
        Assert.Same(snapshot.MerchantSession,
            provider.GetRequiredService<IOptions<ApiHost::Api.Merchants.UserSessionOptions>>().Value);
        Assert.Same(snapshot.Cors,
            provider.GetRequiredService<IOptions<BuildingBlocks.Web.PolCorsOptions>>().Value);
    }

    private static async Task<InvalidOperationException> StartFailure(
        Dictionary<string, string?> values,
        string environmentName)
    {
        var logger = new CapturingLogger<ApiHost::Api.ConsoleConfigurationStartupService>();
        var service = StartupService(Configuration(values), environmentName, logger);
        return await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.StartAsync(CancellationToken.None));
    }

    private static StartupHarness StartupService(
        IConfiguration configuration,
        string environmentName,
        CapturingLogger<ApiHost::Api.ConsoleConfigurationStartupService> logger)
    {
        var resolver = new ApiHost::Api.ConsoleConfigurationResolver(
            configuration, new TestEnvironment(environmentName));
        var service = new ApiHost::Api.ConsoleConfigurationStartupService(resolver, logger);
        return new StartupHarness(resolver, service);
    }

    private static ConfigurationManager Configuration(Dictionary<string, string?> values)
    {
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(values);
        return configuration;
    }

    private static Dictionary<string, string?> ValidCanonical() => new()
    {
        ["AdminSession:DefaultReturnPath"] = "/",
        ["AdminSession:ReturnUrlAllowlist:0"] = "/",
        ["MerchantSession:DefaultReturnPath"] = "/dashboard",
        ["MerchantSession:ReturnUrlAllowlist:0"] = "/",
        ["MerchantSession:ReturnUrlAllowlist:1"] = "/dashboard",
    };

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "pol-core.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return directory.FullName;
    }

    private sealed record StartupHarness(
        ApiHost::Api.ConsoleConfigurationResolver Resolver,
        ApiHost::Api.ConsoleConfigurationStartupService Service)
    {
        public Task StartAsync(CancellationToken cancellationToken) => Service.StartAsync(cancellationToken);
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }

    private sealed class TestEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "Hosts.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
