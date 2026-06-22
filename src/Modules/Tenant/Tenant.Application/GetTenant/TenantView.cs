namespace Tenant.Application.GetTenant;

/// <summary>Admin read model for a provisioned tenant. Secrets appear only as masked hints.</summary>
public sealed record TenantView(
    Guid Id,
    string Code,
    string DisplayName,
    string LegalEntityId,
    string Status,
    string Country,
    string Currency,
    string EnabledChannels,
    DateTime CreatedAtUtc,
    IReadOnlyList<TenantConnectionView> Connections);

public sealed record TenantConnectionView(
    Guid PspConnectionId,
    string Psp,
    IReadOnlyDictionary<string, string> MaskedSecrets);
