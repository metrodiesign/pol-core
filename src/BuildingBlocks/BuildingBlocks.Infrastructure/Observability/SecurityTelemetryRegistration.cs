using BuildingBlocks.Application;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http;

namespace BuildingBlocks.Infrastructure.Observability;

/// <summary>
/// Wires REQ-13's denial/rollback/authz taxonomy to the external tamper-resistant sink (task 9 — Seq,
/// self-hosted alongside the rest of this docker-compose scaffold). <paramref name="applicationName"/> is
/// per-host ("Api"/"Worker", REQ-13.3's same partial-attribution intent as the SQL connection string's
/// Application Name, just on the telemetry side too). <c>Seq:IngestionUrl</c> missing/unset is NOT a boot
/// failure (REQ-13.4's sink is additive observability, never a hard dependency) — the dispatcher falls back
/// to logging locally via <see cref="ILogger"/> instead of posting.
/// </summary>
public static class SecurityTelemetryRegistration
{
    public static IServiceCollection AddSecurityTelemetry(
        this IServiceCollection services, IConfiguration configuration, string applicationName)
    {
        services.AddHttpClient("SecurityTelemetry", c => c.Timeout = TimeSpan.FromSeconds(10));

        services.AddSingleton<SecurityTelemetryChannel>();
        services.AddSingleton<ISecurityTelemetry>(sp => sp.GetRequiredService<SecurityTelemetryChannel>());

        var ingestionUrl = configuration["Seq:IngestionUrl"];
        services.AddSingleton<IHostedService>(sp => new SecurityTelemetryDispatcher(
            sp.GetRequiredService<SecurityTelemetryChannel>(),
            sp.GetRequiredService<IHttpClientFactory>().CreateClient("SecurityTelemetry"),
            ingestionUrl,
            applicationName,
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SecurityTelemetryDispatcher>>()));

        return services;
    }
}
