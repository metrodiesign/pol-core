using Api.Admins;
using Api.Merchants;
using BuildingBlocks.Web;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.Options;

namespace Api;

internal sealed record ConsoleConfigurationSnapshot(
    AdminSessionOptions AdminSession,
    UserSessionOptions MerchantSession,
    PolCorsOptions Cors,
    IReadOnlyList<string> LegacyKeyFamilies);

internal static class ConsoleConfigurationServiceCollectionExtensions
{
    public static IServiceCollection AddConsoleConfiguration(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        services.AddSingleton(_ => new ConsoleConfigurationResolver(configuration, environment));
        services.AddSingleton<IOptions<AdminSessionOptions>>(provider =>
            Options.Create(provider.GetRequiredService<ConsoleConfigurationResolver>().Value.AdminSession));
        services.AddSingleton<IOptions<UserSessionOptions>>(provider =>
            Options.Create(provider.GetRequiredService<ConsoleConfigurationResolver>().Value.MerchantSession));
        services.AddSingleton<IOptions<PolCorsOptions>>(provider =>
            Options.Create(provider.GetRequiredService<ConsoleConfigurationResolver>().Value.Cors));
        services.AddHostedService<ConsoleConfigurationStartupService>();
        return services;
    }
}

internal sealed class ConsoleConfigurationStartupService : IHostedService
{
    private readonly ConsoleConfigurationResolver _resolver;
    private readonly ILogger<ConsoleConfigurationStartupService> _logger;

    public ConsoleConfigurationStartupService(
        ConsoleConfigurationResolver resolver,
        ILogger<ConsoleConfigurationStartupService> logger)
    {
        _resolver = resolver;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var legacyFamily in _resolver.Value.LegacyKeyFamilies)
            _logger.LogWarning(
                "Configuration key family {LegacyKeyFamily} is deprecated; use {CanonicalKeyFamily}.",
                legacyFamily,
                CanonicalFamily(legacyFamily));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static string CanonicalFamily(string legacyFamily) => legacyFamily switch
    {
        "AdminSession:SpaBaseUrl" => "AdminSession:WebAppBaseUrl",
        "MerchantUser:Session" => "MerchantSession",
        "Cors:AllowedOrigins" => "Cors:MerchantOrigins",
        _ => throw new InvalidOperationException(
            $"Unknown legacy console configuration key family '{legacyFamily}'."),
    };
}

/// <summary>
/// Resolves the Admin/Merchant browser configuration once, after every host configuration provider has
/// been layered. Committed appsettings.json values and C# initializers are defaults; every other provider is
/// operator input. This distinction lets a legacy environment override replace a committed canonical default
/// without being mistaken for an explicit canonical/legacy conflict.
/// </summary>
internal sealed class ConsoleConfigurationResolver
{
    private const string AdminSession = "AdminSession";
    private const string MerchantSession = "MerchantSession";
    private const string LegacyMerchantSession = "MerchantUser:Session";
    private const string Cors = "Cors";
    private const string AdminSpaBaseUrl = "AdminSession:SpaBaseUrl";
    private const string AdminWebAppBaseUrl = "AdminSession:WebAppBaseUrl";
    private const string MerchantWebAppBaseUrl = "MerchantSession:WebAppBaseUrl";
    private const string LegacyMerchantSpaBaseUrl = "MerchantUser:Session:SpaBaseUrl";
    private const string MerchantOrigins = "Cors:MerchantOrigins";
    private const string LegacyMerchantOrigins = "Cors:AllowedOrigins";

    private static readonly HashSet<string> MerchantLegacyFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "IdleMinutes",
        "AbsoluteHours",
        "RotationMinutes",
        "GraceSeconds",
        "SameSite",
        "DefaultReturnPath",
        "ReturnUrlAllowlist",
        "SpaBaseUrl",
    };

    private readonly Lazy<ConsoleConfigurationSnapshot> _value;

    public ConsoleConfigurationResolver(IConfiguration configuration, IHostEnvironment environment) =>
        _value = new Lazy<ConsoleConfigurationSnapshot>(
            () => Resolve(configuration, environment), LazyThreadSafetyMode.ExecutionAndPublication);

    public ConsoleConfigurationSnapshot Value => _value.Value;

    private static ConsoleConfigurationSnapshot Resolve(
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        if (configuration is not IConfigurationRoot root)
            throw new InvalidOperationException(
                "Console configuration requires IConfigurationRoot provider metadata.");

        var baseProviders = root.Providers.Where(IsCommittedBaseAppSettings).ToArray();
        var operatorProviders = root.Providers.Where(provider => !IsCommittedBaseAppSettings(provider)).ToArray();

        var baseEntries = CollectEntries(baseProviders,
            AdminSession, MerchantSession, "Cors:AdminOrigins", MerchantOrigins);
        baseEntries.Remove(AdminSpaBaseUrl);

        var canonicalEntries = CollectEntries(operatorProviders,
            AdminSession, MerchantSession, "Cors:AdminOrigins", MerchantOrigins);
        canonicalEntries.Remove(AdminSpaBaseUrl);

        var legacyEntries = CollectEntries(operatorProviders,
            AdminSpaBaseUrl, LegacyMerchantSession, LegacyMerchantOrigins);
        var mappedLegacyEntries = MapLegacyEntries(legacyEntries);

        var baseline = BuildConfiguration(baseEntries);
        var canonical = BuildConfiguration(baseEntries, canonicalEntries);
        var legacy = BuildConfiguration(baseEntries, mappedLegacyEntries);

        var baselineAdmin = Bind<AdminSessionOptions>(baseline, AdminSession);
        var canonicalAdmin = Bind<AdminSessionOptions>(canonical, AdminSession);
        var legacyAdmin = Bind<AdminSessionOptions>(legacy, AdminSession);
        var baselineMerchant = Bind<UserSessionOptions>(baseline, MerchantSession);
        var canonicalMerchant = Bind<UserSessionOptions>(canonical, MerchantSession);
        var legacyMerchant = Bind<UserSessionOptions>(legacy, MerchantSession);
        var baselineCors = Bind<PolCorsOptions>(baseline, Cors);
        var canonicalCors = Bind<PolCorsOptions>(canonical, Cors);
        var legacyCors = Bind<PolCorsOptions>(legacy, Cors);

        var admin = new AdminSessionOptions
        {
            IdleMinutes = CanonicalScalar(baseEntries, canonicalEntries,
                "AdminSession:IdleMinutes", baselineAdmin.IdleMinutes, canonicalAdmin.IdleMinutes),
            AbsoluteHours = CanonicalScalar(baseEntries, canonicalEntries,
                "AdminSession:AbsoluteHours", baselineAdmin.AbsoluteHours, canonicalAdmin.AbsoluteHours),
            RotationMinutes = CanonicalScalar(baseEntries, canonicalEntries,
                "AdminSession:RotationMinutes", baselineAdmin.RotationMinutes, canonicalAdmin.RotationMinutes),
            GraceSeconds = CanonicalScalar(baseEntries, canonicalEntries,
                "AdminSession:GraceSeconds", baselineAdmin.GraceSeconds, canonicalAdmin.GraceSeconds),
            SameSite = CanonicalScalar(baseEntries, canonicalEntries,
                "AdminSession:SameSite", baselineAdmin.SameSite, canonicalAdmin.SameSite),
            PreAuthTtlMinutes = CanonicalScalar(baseEntries, canonicalEntries,
                "AdminSession:PreAuthTtlMinutes", baselineAdmin.PreAuthTtlMinutes, canonicalAdmin.PreAuthTtlMinutes),
            DefaultReturnPath = CanonicalScalar(baseEntries, canonicalEntries,
                "AdminSession:DefaultReturnPath", baselineAdmin.DefaultReturnPath, canonicalAdmin.DefaultReturnPath),
            ReturnUrlAllowlist = CanonicalSection(baseEntries, canonicalEntries,
                "AdminSession:ReturnUrlAllowlist", baselineAdmin.ReturnUrlAllowlist, canonicalAdmin.ReturnUrlAllowlist),
            WebAppBaseUrl = ResolveOriginAlias(
                baselineAdmin.WebAppBaseUrl,
                canonicalAdmin.WebAppBaseUrl,
                legacyAdmin.WebAppBaseUrl,
                HasField(canonicalEntries, AdminWebAppBaseUrl),
                HasField(mappedLegacyEntries, AdminWebAppBaseUrl),
                AdminWebAppBaseUrl,
                AdminSpaBaseUrl,
                environment),
            ScalarBaseUrl = CanonicalScalar(baseEntries, canonicalEntries,
                "AdminSession:ScalarBaseUrl", baselineAdmin.ScalarBaseUrl, canonicalAdmin.ScalarBaseUrl),
        };

        var merchant = new UserSessionOptions
        {
            IdleMinutes = ResolveAlias(
                baselineMerchant.IdleMinutes, canonicalMerchant.IdleMinutes, legacyMerchant.IdleMinutes,
                canonicalEntries, mappedLegacyEntries,
                "MerchantSession:IdleMinutes", "MerchantUser:Session:IdleMinutes"),
            AbsoluteHours = ResolveAlias(
                baselineMerchant.AbsoluteHours, canonicalMerchant.AbsoluteHours, legacyMerchant.AbsoluteHours,
                canonicalEntries, mappedLegacyEntries,
                "MerchantSession:AbsoluteHours", "MerchantUser:Session:AbsoluteHours"),
            RotationMinutes = ResolveAlias(
                baselineMerchant.RotationMinutes, canonicalMerchant.RotationMinutes, legacyMerchant.RotationMinutes,
                canonicalEntries, mappedLegacyEntries,
                "MerchantSession:RotationMinutes", "MerchantUser:Session:RotationMinutes"),
            GraceSeconds = ResolveAlias(
                baselineMerchant.GraceSeconds, canonicalMerchant.GraceSeconds, legacyMerchant.GraceSeconds,
                canonicalEntries, mappedLegacyEntries,
                "MerchantSession:GraceSeconds", "MerchantUser:Session:GraceSeconds"),
            SameSite = ResolveAlias(
                baselineMerchant.SameSite, canonicalMerchant.SameSite, legacyMerchant.SameSite,
                canonicalEntries, mappedLegacyEntries,
                "MerchantSession:SameSite", "MerchantUser:Session:SameSite", StringComparer.Ordinal.Equals),
            DefaultReturnPath = ResolveAlias(
                baselineMerchant.DefaultReturnPath, canonicalMerchant.DefaultReturnPath, legacyMerchant.DefaultReturnPath,
                canonicalEntries, mappedLegacyEntries,
                "MerchantSession:DefaultReturnPath", "MerchantUser:Session:DefaultReturnPath", StringComparer.Ordinal.Equals),
            ReturnUrlAllowlist = ResolveReturnUrlAlias(
                baselineMerchant.ReturnUrlAllowlist,
                canonicalMerchant.ReturnUrlAllowlist,
                legacyMerchant.ReturnUrlAllowlist,
                HasField(canonicalEntries, "MerchantSession:ReturnUrlAllowlist"),
                HasField(mappedLegacyEntries, "MerchantSession:ReturnUrlAllowlist")),
            WebAppBaseUrl = ResolveOriginAlias(
                baselineMerchant.WebAppBaseUrl,
                canonicalMerchant.WebAppBaseUrl,
                legacyMerchant.WebAppBaseUrl,
                HasField(canonicalEntries, MerchantWebAppBaseUrl),
                HasField(mappedLegacyEntries, MerchantWebAppBaseUrl),
                MerchantWebAppBaseUrl,
                LegacyMerchantSpaBaseUrl,
                environment),
        };

        var cors = new PolCorsOptions
        {
            AdminOrigins = NormalizeOrigins(
                CanonicalSection(baseEntries, canonicalEntries,
                    "Cors:AdminOrigins", baselineCors.AdminOrigins, canonicalCors.AdminOrigins),
                "Cors:AdminOrigins", environment),
            MerchantOrigins = ResolveOriginListAlias(
                baselineCors.MerchantOrigins,
                canonicalCors.MerchantOrigins,
                legacyCors.MerchantOrigins,
                HasField(canonicalEntries, MerchantOrigins),
                HasField(mappedLegacyEntries, MerchantOrigins),
                MerchantOrigins,
                LegacyMerchantOrigins,
                environment),
        };

        ValidateReturnPaths(admin.DefaultReturnPath, admin.ReturnUrlAllowlist,
            "AdminSession:DefaultReturnPath", "AdminSession:ReturnUrlAllowlist");
        ValidateReturnPaths(merchant.DefaultReturnPath, merchant.ReturnUrlAllowlist,
            "MerchantSession:DefaultReturnPath", "MerchantSession:ReturnUrlAllowlist");

        if (!string.IsNullOrWhiteSpace(configuration["MerchantUser:Invitation:Smtp:Host"])
            && string.IsNullOrEmpty(merchant.WebAppBaseUrl))
            throw Invalid(MerchantWebAppBaseUrl,
                "must be a non-blank HTTP(S) origin when Merchant invitation SMTP is configured");

        var legacyFamilies = new List<string>(3);
        if (HasField(legacyEntries, AdminSpaBaseUrl))
            legacyFamilies.Add(AdminSpaBaseUrl);
        if (HasField(legacyEntries, LegacyMerchantSession))
            legacyFamilies.Add(LegacyMerchantSession);
        if (HasField(legacyEntries, LegacyMerchantOrigins))
            legacyFamilies.Add(LegacyMerchantOrigins);

        return new ConsoleConfigurationSnapshot(admin, merchant, cors, legacyFamilies);
    }

    private static T ResolveAlias<T>(
        T baseline,
        T canonical,
        T legacy,
        IReadOnlyDictionary<string, string?> canonicalEntries,
        IReadOnlyDictionary<string, string?> legacyEntries,
        string canonicalKey,
        string legacyKey,
        Func<T, T, bool>? equivalent = null)
    {
        var hasCanonical = HasField(canonicalEntries, canonicalKey);
        var hasLegacy = HasField(legacyEntries, canonicalKey);
        equivalent ??= EqualityComparer<T>.Default.Equals;

        if (hasCanonical && hasLegacy && !equivalent(canonical, legacy))
            throw Conflict(canonicalKey, legacyKey);
        return hasCanonical ? canonical : hasLegacy ? legacy : baseline;
    }

    private static string[] ResolveReturnUrlAlias(
        string[]? baseline,
        string[]? canonical,
        string[]? legacy,
        bool hasCanonical,
        bool hasLegacy)
    {
        baseline ??= [];
        canonical ??= [];
        legacy ??= [];
        if (hasCanonical && hasLegacy
            && !canonical.ToHashSet(StringComparer.Ordinal).SetEquals(legacy))
            throw Conflict("MerchantSession:ReturnUrlAllowlist", "MerchantUser:Session:ReturnUrlAllowlist");
        return hasCanonical ? canonical : hasLegacy ? legacy : baseline;
    }

    private static string ResolveOriginAlias(
        string? baseline,
        string? canonical,
        string? legacy,
        bool hasCanonical,
        bool hasLegacy,
        string canonicalKey,
        string legacyKey,
        IHostEnvironment environment)
    {
        baseline ??= string.Empty;
        canonical ??= string.Empty;
        legacy ??= string.Empty;

        var normalizedCanonical = hasCanonical
            ? NormalizeOrigin(canonical, canonicalKey, environment, allowBlank: true, rejectWildcard: false)
            : string.Empty;
        var normalizedLegacy = hasLegacy
            ? NormalizeOrigin(legacy, canonicalKey, environment, allowBlank: true, rejectWildcard: false)
            : string.Empty;
        if (hasCanonical && hasLegacy
            && !string.Equals(normalizedCanonical, normalizedLegacy, StringComparison.Ordinal))
            throw Conflict(canonicalKey, legacyKey);

        var selected = hasCanonical ? normalizedCanonical : hasLegacy ? normalizedLegacy : baseline;
        return NormalizeOrigin(selected, canonicalKey, environment, allowBlank: true, rejectWildcard: false);
    }

    private static string[] ResolveOriginListAlias(
        string[]? baseline,
        string[]? canonical,
        string[]? legacy,
        bool hasCanonical,
        bool hasLegacy,
        string canonicalKey,
        string legacyKey,
        IHostEnvironment environment)
    {
        baseline ??= [];
        canonical ??= [];
        legacy ??= [];
        var normalizedCanonical = hasCanonical
            ? NormalizeOrigins(canonical, canonicalKey, environment)
            : [];
        var normalizedLegacy = hasLegacy
            ? NormalizeOrigins(legacy, canonicalKey, environment)
            : [];
        if (hasCanonical && hasLegacy
            && !normalizedCanonical.ToHashSet(StringComparer.Ordinal).SetEquals(normalizedLegacy))
            throw Conflict(canonicalKey, legacyKey);

        return hasCanonical
            ? normalizedCanonical
            : hasLegacy ? normalizedLegacy : NormalizeOrigins(baseline, canonicalKey, environment);
    }

    private static string[] NormalizeOrigins(
        IEnumerable<string> origins,
        string canonicalKey,
        IHostEnvironment environment)
    {
        var normalized = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var origin in origins)
        {
            var value = NormalizeOrigin(origin, canonicalKey, environment,
                allowBlank: false, rejectWildcard: true);
            if (!seen.Add(value))
                throw Invalid(canonicalKey, "must not contain duplicate origins after normalization");
            normalized.Add(value);
        }
        return normalized.ToArray();
    }

    private static string NormalizeOrigin(
        string value,
        string canonicalKey,
        IHostEnvironment environment,
        bool allowBlank,
        bool rejectWildcard)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            if (allowBlank)
                return string.Empty;
            throw Invalid(canonicalKey, "must contain only absolute HTTP(S) origins");
        }
        if (rejectWildcard && value.Contains('*', StringComparison.Ordinal))
            throw Invalid(canonicalKey, "must not contain wildcard origins");
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrEmpty(uri.Host))
            throw Invalid(canonicalKey, "must contain only absolute HTTP(S) origins");
        if (!string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || uri.AbsolutePath != "/")
            throw Invalid(canonicalKey, "must contain origins without user info, query, fragment, or non-root path");
        if (uri.Scheme == Uri.UriSchemeHttp
            && (!environment.IsDevelopment() || !uri.IsLoopback))
            throw Invalid(canonicalKey,
                environment.IsDevelopment()
                    ? "may use HTTP in Development only for a loopback origin"
                    : "must use HTTPS outside Development");

        var builder = new UriBuilder(uri)
        {
            Scheme = uri.Scheme.ToLowerInvariant(),
            Host = uri.IdnHost.ToLowerInvariant(),
            Port = uri.IsDefaultPort ? -1 : uri.Port,
            Path = string.Empty,
            Query = string.Empty,
            Fragment = string.Empty,
        };
        return builder.Uri.GetLeftPart(UriPartial.Authority);
    }

    private static void ValidateReturnPaths(
        string defaultPath,
        IEnumerable<string> allowlist,
        string defaultKey,
        string allowlistKey)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in allowlist)
        {
            if (string.IsNullOrEmpty(path)
                || path[0] != '/'
                || (path.Length > 1 && path[1] == '/')
                || path.Contains('\\')
                || path.Any(character => character <= '\u001f' || character == '\u007f'))
                throw Invalid(allowlistKey,
                    "must contain paths with exactly one leading slash and no backslash or ASCII control character");
            if (!paths.Add(path))
                throw Invalid(allowlistKey, "must not contain duplicate paths under ordinal comparison");
        }
        if (!paths.Contains(defaultPath))
            throw Invalid(defaultKey, $"must be present in '{allowlistKey}'");
    }

    private static T CanonicalScalar<T>(
        IReadOnlyDictionary<string, string?> baseEntries,
        IReadOnlyDictionary<string, string?> canonicalEntries,
        string key,
        T baseline,
        T canonical) =>
        HasField(canonicalEntries, key) ? canonical : baseline;

    private static T CanonicalSection<T>(
        IReadOnlyDictionary<string, string?> baseEntries,
        IReadOnlyDictionary<string, string?> canonicalEntries,
        string key,
        T baseline,
        T canonical) =>
        HasField(canonicalEntries, key) ? canonical : baseline;

    private static T Bind<T>(IConfiguration configuration, string sectionName) where T : new()
    {
        var value = new T();
        try
        {
            configuration.GetSection(sectionName).Bind(value);
            return value;
        }
        catch (Exception exception) when (exception is InvalidOperationException or FormatException)
        {
            throw new InvalidOperationException(
                $"Configuration key family '{sectionName}' cannot be bound to its canonical option type.");
        }
    }

    private static IConfigurationRoot BuildConfiguration(
        IReadOnlyDictionary<string, string?> first,
        IReadOnlyDictionary<string, string?>? second = null)
    {
        var builder = new ConfigurationBuilder().AddInMemoryCollection(first);
        if (second is not null)
            builder.AddInMemoryCollection(second);
        return builder.Build();
    }

    private static Dictionary<string, string?> CollectEntries(
        IEnumerable<IConfigurationProvider> providers,
        params string[] roots)
    {
        var entries = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in providers)
            foreach (var root in roots)
                CollectSection(provider, root, entries, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        return entries;
    }

    private static void CollectSection(
        IConfigurationProvider provider,
        string key,
        IDictionary<string, string?> entries,
        ISet<string> visited)
    {
        if (!visited.Add(key))
            return;
        if (provider.TryGet(key, out var value))
            entries[key] = value;

        foreach (var child in provider.GetChildKeys([], key).Distinct(StringComparer.OrdinalIgnoreCase))
            CollectSection(provider, ConfigurationPath.Combine(key, child), entries, visited);
    }

    private static Dictionary<string, string?> MapLegacyEntries(
        IReadOnlyDictionary<string, string?> legacyEntries)
    {
        var mapped = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (legacyEntries.TryGetValue(AdminSpaBaseUrl, out var adminSpa))
            mapped[AdminWebAppBaseUrl] = adminSpa;

        foreach (var (key, value) in legacyEntries)
        {
            if (!key.Equals(LegacyMerchantSession, StringComparison.OrdinalIgnoreCase)
                && !key.StartsWith(LegacyMerchantSession + ":", StringComparison.OrdinalIgnoreCase))
                continue;

            var relative = key.Length == LegacyMerchantSession.Length
                ? string.Empty
                : key[(LegacyMerchantSession.Length + 1)..];
            if (relative.Length == 0)
            {
                mapped[MerchantSession] = value;
                continue;
            }

            var separator = relative.IndexOf(':');
            var field = separator < 0 ? relative : relative[..separator];
            if (!MerchantLegacyFields.Contains(field))
                continue;
            var canonicalField = field.Equals("SpaBaseUrl", StringComparison.OrdinalIgnoreCase)
                ? "WebAppBaseUrl"
                : field;
            var suffix = separator < 0 ? string.Empty : relative[separator..];
            mapped[$"{MerchantSession}:{canonicalField}{suffix}"] = value;
        }

        foreach (var (key, value) in legacyEntries)
        {
            if (!key.Equals(LegacyMerchantOrigins, StringComparison.OrdinalIgnoreCase)
                && !key.StartsWith(LegacyMerchantOrigins + ":", StringComparison.OrdinalIgnoreCase))
                continue;
            var suffix = key.Length == LegacyMerchantOrigins.Length
                ? string.Empty
                : key[LegacyMerchantOrigins.Length..];
            mapped[MerchantOrigins + suffix] = value;
        }
        return mapped;
    }

    private static bool HasField(IReadOnlyDictionary<string, string?> entries, string key) =>
        entries.Keys.Any(candidate => candidate.Equals(key, StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(key + ":", StringComparison.OrdinalIgnoreCase));

    private static bool IsCommittedBaseAppSettings(IConfigurationProvider provider) =>
        provider is JsonConfigurationProvider json
        && string.Equals(Path.GetFileName(json.Source.Path), "appsettings.json", StringComparison.OrdinalIgnoreCase);

    private static InvalidOperationException Conflict(string canonicalKey, string legacyKey) =>
        new($"Configuration keys '{canonicalKey}' and '{legacyKey}' conflict after normalization.");

    private static InvalidOperationException Invalid(string canonicalKey, string rule) =>
        new($"Configuration key '{canonicalKey}' {rule}.");
}
