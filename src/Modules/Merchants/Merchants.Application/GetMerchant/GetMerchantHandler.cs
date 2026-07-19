using System.Text.Json;
using BuildingBlocks.Application;
using Mediator;
using Merchants.Domain;
using Payments.Application.Ports.Psp;
using Payments.Domain.Psp;

namespace Merchants.Application.GetMerchant;

/// <summary>
/// Handles <see cref="GetMerchantQuery"/>: loads the merchant and projects it + its PSP connections to a
/// read model (admin cross-merchant read-back — <see cref="IConnectionRepository.ListByTenantAsync"/> is an
/// escape-hatch port for this one caller, task 8.5.8). Masked secret hints are read from the connection's
/// metadata (stored at provisioning), so this path never touches the vault (REQ-6.5 / REQ-9).
/// </summary>
public sealed class GetMerchantHandler : IQueryHandler<GetMerchantQuery, MerchantView>
{
    private readonly IMerchantRepository _merchants;
    private readonly IConnectionRepository _pspConnections;

    public GetMerchantHandler(IMerchantRepository merchants, IConnectionRepository pspConnections)
    {
        _merchants = merchants;
        _pspConnections = pspConnections;
    }

    public async ValueTask<MerchantView> Handle(GetMerchantQuery query, CancellationToken cancellationToken)
    {
        var code = MerchantCode.Normalize(query.Code);
        var merchant = await _merchants.GetByCodeAsync(code, cancellationToken)
            ?? throw new NotFoundException($"Merchant '{code}' was not found.");

        var connections = await _pspConnections.ListByTenantAsync(merchant.Id, cancellationToken);
        var connectionViews = connections
            .Select(c =>
            {
                var (merchantId, config, masked) = ReadConnectionMetadata(c.Metadata);
                var methods = c.EnabledMethods.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                return new MerchantConnectionView(c.Id, c.Psp.ToCode(), merchantId, methods, config, masked);
            })
            .ToList();

        return new MerchantView(merchant.Id, merchant.Code, merchant.DisplayName, merchant.LegalEntityId,
            merchant.Status.ToString(), merchant.Country, merchant.Currency, merchant.EnabledChannels,
            ParseJson(merchant.Metadata), merchant.CreatedAt, connectionViews);
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

    /// <summary>Parses verbatim-stored JSON (the merchant Metadata blob) into a detached element, or null.</summary>
    private static JsonElement? ParseJson(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        using var doc = JsonDocument.Parse(raw);
        return doc.RootElement.Clone();
    }
}
