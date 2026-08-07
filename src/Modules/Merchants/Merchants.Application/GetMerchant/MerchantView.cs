using System.Text.Json;

namespace Merchants.Application.GetMerchant;

/// <summary>Admin read model for a provisioned merchant. <see cref="Metadata"/> follows the closed non-secret
/// config contract; secrets appear only as masked hints.</summary>
public sealed record MerchantView(
    Guid Id,
    string Code,
    string Name,
    string? Note,
    string Status,
    string Country,
    string Currency,
    string EnabledChannels,
    JsonElement? Metadata,
    DateTime CreatedAt,
    IReadOnlyList<MerchantConnectionView> Connections);

/// <summary><see cref="EnabledMethods"/> + <see cref="Config"/> + <see cref="MerchantId"/> are the verbatim
/// non-secret PSP config stored at provisioning (REQ-9.1 read-back); <see cref="MaskedSecrets"/> never
/// carries plaintext.</summary>
public sealed record MerchantConnectionView(
    Guid PspConnectionId,
    string Psp,
    string? MerchantId,
    IReadOnlyList<string> EnabledMethods,
    JsonElement? Config,
    IReadOnlyDictionary<string, string> MaskedSecrets);
