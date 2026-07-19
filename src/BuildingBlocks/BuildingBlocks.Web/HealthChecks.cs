using BuildingBlocks.Infrastructure.Vault;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace BuildingBlocks.Web;

/// <summary>Readiness probe: can the host reach its database? Connectivity only, no schema is named. A raw
/// connection open (not a DbContext) — task 8's "1 principal" collapse split the single migration-owner
/// PolDbContext into 3 separate runtime contexts, each living in its own host-specific Persistence.* project
/// this shared BuildingBlocks.Web assembly may not reference; every host still connects to ONE physical
/// database, so a plain connection-string probe covers all of them identically.</summary>
internal sealed class AppDbReadinessCheck : IHealthCheck
{
    private readonly string _connectionString;

    public AppDbReadinessCheck(string connectionString) => _connectionString = connectionString;

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            // The exception is captured for server-side logging only; the minimal response writer never
            // echoes it, so no connection string / SQL detail reaches the client.
            return HealthCheckResult.Unhealthy("database unreachable", ex);
        }
    }
}

/// <summary>Readiness probe: does the vault keyring build with a well-formed 32-byte active key? Resolves
/// the keyring (a mounted secret file is read once when it is first built) and reports not-ready rather than
/// throwing if it is misconfigured — so a bad key custody never 500s the probe, it just gates traffic.</summary>
internal sealed class VaultReadinessCheck : IHealthCheck
{
    private readonly IServiceProvider _services;

    public VaultReadinessCheck(IServiceProvider services) => _services = services;

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var keyring = _services.GetRequiredService<VaultKeyring>();
            var (_, key) = keyring.Active;
            return Task.FromResult(key.Length == 32
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("vault active key is malformed"));
        }
        catch (Exception ex)
        {
            // The factory's (secret-free) error is captured for server-side logging only; the minimal
            // response writer never echoes it.
            return Task.FromResult(HealthCheckResult.Unhealthy("vault keyring is not configured", ex));
        }
    }
}

/// <summary>
/// Split health endpoints reused by every host. <c>/health/live</c> is process-only (orchestrator restart
/// signal — touches no dependency). <c>/health/ready</c> gates traffic on the "ready"-tagged checks (DB +
/// vault) and writes ONLY a status token so it can never leak topology, check names, or exception detail.
/// </summary>
public static class HealthCheckExtensions
{
    private const string ReadyTag = "ready";

    public static IServiceCollection AddReadinessHealthChecks(this IServiceCollection services, string connectionString)
    {
        services.AddHealthChecks()
            .AddCheck("app-db", new AppDbReadinessCheck(connectionString), tags: [ReadyTag])
            .AddCheck<VaultReadinessCheck>("vault", tags: [ReadyTag]);
        return services;
    }

    public static void MapPolHealthChecks(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
        endpoints.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains(ReadyTag),
            ResponseWriter = WriteMinimalResponse,
        });
    }

    // Body is just {"status":"..."} — no entry names, descriptions, exceptions, or schema strings.
    private static Task WriteMinimalResponse(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";
        var status = report.Status == HealthStatus.Healthy ? "healthy" : "not_ready";
        return context.Response.WriteAsync($"{{\"status\":\"{status}\"}}");
    }
}
