using System.Text.Json;
using BuildingBlocks.Application;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using Payments.Application.Ports;
using Payments.Domain;
using Tenant.Domain;

namespace Tenant.Application.GetTenant;

/// <summary>
/// Handles <see cref="GetTenantQuery"/>: loads the tenant (admin/bypass connection) and projects it +
/// its PSP connections to a read model. Masked secret hints are read from the connection's metadata
/// (stored at provisioning), so this path never touches the vault (REQ-6.5 / REQ-9).
/// </summary>
public sealed class GetTenantHandler : IQueryHandler<GetTenantQuery, TenantView>
{
    private readonly ITenantRepository _tenants;
    private readonly IPspConnectionRepository _pspConnections;

    public GetTenantHandler(
        ITenantRepository tenants,
        [FromKeyedServices("admin")] IPspConnectionRepository pspConnections)
    {
        _tenants = tenants;
        _pspConnections = pspConnections;
    }

    public async ValueTask<TenantView> Handle(GetTenantQuery query, CancellationToken cancellationToken)
    {
        var code = TenantCode.Normalize(query.Code);
        var tenant = await _tenants.GetByCodeAsync(code, cancellationToken)
            ?? throw new NotFoundException($"Tenant '{code}' was not found.");

        var connections = await _pspConnections.ListByTenantAsync(tenant.Id, cancellationToken);
        var connectionViews = connections
            .Select(c =>
            {
                var (merchantId, config, masked) = ReadConnectionMetadata(c.Metadata);
                return new TenantConnectionView(c.Id, c.Psp.ToCode(), merchantId, config, masked);
            })
            .ToList();

        return new TenantView(tenant.Id, tenant.Code, tenant.DisplayName, tenant.LegalEntityId,
            tenant.Status.ToString(), tenant.Country, tenant.Currency, tenant.EnabledChannels,
            ParseJson(tenant.Metadata), tenant.CreatedAtUtc, connectionViews);
    }

    /// <summary>Projects the connection's stored metadata for read-back (REQ-9.1): the non-secret config +
    /// merchant id verbatim, and the masked secret hints — never the vault, never plaintext.</summary>
    private static (string? MerchantId, JsonElement? Config, IReadOnlyDictionary<string, string> Masked)
        ReadConnectionMetadata(string? metadata)
    {
        var masked = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(metadata))
            return (null, null, masked);

        using var doc = JsonDocument.Parse(metadata);
        var root = doc.RootElement;

        var merchantId = root.TryGetProperty("merchantId", out var mid) && mid.ValueKind == JsonValueKind.String
            ? mid.GetString()
            : null;

        // Clone detaches the element from the JsonDocument before it is disposed.
        JsonElement? config = root.TryGetProperty("config", out var cfg) && cfg.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            ? cfg.Clone()
            : null;

        if (root.TryGetProperty("secretHints", out var hints) && hints.ValueKind == JsonValueKind.Object)
        {
            foreach (var field in hints.EnumerateObject())
                masked[field.Name] = "****" + (field.Value.GetString() ?? string.Empty);
        }

        return (merchantId, config, masked);
    }

    /// <summary>Parses verbatim-stored JSON (the tenant Metadata blob) into a detached element, or null.</summary>
    private static JsonElement? ParseJson(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        using var doc = JsonDocument.Parse(raw);
        return doc.RootElement.Clone();
    }
}
