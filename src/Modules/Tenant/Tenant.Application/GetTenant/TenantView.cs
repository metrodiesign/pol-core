using System.Text.Json;

namespace Tenant.Application.GetTenant;

/// <summary>Admin read model for a provisioned tenant. <see cref="Metadata"/> is the verbatim non-secret
/// config the admin submitted (branding/routing/session/...); secrets appear only as masked hints.</summary>
public sealed record TenantView(
    Guid Id,
    string Code,
    string DisplayName,
    string LegalEntityId,
    string Status,
    string Country,
    string Currency,
    string EnabledChannels,
    JsonElement? Metadata,
    DateTime CreatedAtUtc,
    IReadOnlyList<TenantConnectionView> Connections);

/// <summary><see cref="Config"/> + <see cref="MerchantId"/> are the verbatim non-secret PSP config stored at
/// provisioning (REQ-9.1 read-back); <see cref="MaskedSecrets"/> never carries plaintext.</summary>
public sealed record TenantConnectionView(
    Guid PspConnectionId,
    string Psp,
    string? MerchantId,
    JsonElement? Config,
    IReadOnlyDictionary<string, string> MaskedSecrets);
