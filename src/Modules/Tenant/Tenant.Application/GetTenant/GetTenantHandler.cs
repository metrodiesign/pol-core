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
            .Select(c => new TenantConnectionView(c.Id, c.Psp.ToCode(), ReadMaskedSecrets(c.Metadata)))
            .ToList();

        return new TenantView(tenant.Id, tenant.Code, tenant.DisplayName, tenant.LegalEntityId,
            tenant.Status.ToString(), tenant.Country, tenant.Currency, tenant.EnabledChannels,
            tenant.CreatedAtUtc, connectionViews);
    }

    /// <summary>Reads the masked hints stored on the connection at provisioning — never the vault.</summary>
    private static IReadOnlyDictionary<string, string> ReadMaskedSecrets(string? metadata)
    {
        var masked = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(metadata))
            return masked;

        using var doc = JsonDocument.Parse(metadata);
        if (doc.RootElement.TryGetProperty("secretHints", out var hints) && hints.ValueKind == JsonValueKind.Object)
        {
            foreach (var field in hints.EnumerateObject())
                masked[field.Name] = "****" + (field.Value.GetString() ?? string.Empty);
        }

        return masked;
    }
}
