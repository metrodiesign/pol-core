using BuildingBlocks.Infrastructure.Persistence;
using BuildingBlocks.Infrastructure.Vault;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace BuildingBlocks.Web;

/// <summary>Readiness probe: can the host reach its database? Connectivity only, no schema is named.</summary>
internal sealed class ProducerDbReadinessCheck : IHealthCheck
{
    private readonly ProducerDbContext _db;

    public ProducerDbReadinessCheck(ProducerDbContext db) => _db = db;

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _db.Database.CanConnectAsync(cancellationToken).ConfigureAwait(false)
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("database unreachable");
        }
        catch (Exception ex)
        {
            // The exception is captured for server-side logging only; the minimal response writer never
            // echoes it, so no connection string / SQL detail reaches the client.
            return HealthCheckResult.Unhealthy("database unreachable", ex);
        }
    }
}

/// <summary>Readiness probe: is the vault master key present and a well-formed 32-byte AES key?</summary>
internal sealed class VaultReadinessCheck : IHealthCheck
{
    private readonly VaultOptions _options;

    public VaultReadinessCheck(IOptions<VaultOptions> options) => _options = options.Value;

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_options.MasterKeyBase64))
            return Task.FromResult(HealthCheckResult.Unhealthy("vault master key is not configured"));

        Span<byte> buffer = stackalloc byte[32];
        var decoded = Convert.TryFromBase64String(_options.MasterKeyBase64, buffer, out var written) && written == 32;
        return Task.FromResult(decoded
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("vault master key is malformed"));
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

    public static IServiceCollection AddReadinessHealthChecks(this IServiceCollection services)
    {
        services.AddHealthChecks()
            .AddCheck<ProducerDbReadinessCheck>("producer-db", tags: [ReadyTag])
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
