namespace Merchants.Application.ProvisionMerchant;

/// <summary>The provisioning outcome. Secrets are masked (never plaintext).</summary>
public sealed record ProvisionMerchantResult(
    Guid MerchantId,
    IReadOnlyList<ProvisionedConnection> Connections);

/// <summary>A provisioned PSP connection with its masked secret hints (field -> "****hint").</summary>
public sealed record ProvisionedConnection(
    Guid PspConnectionId,
    string Psp,
    IReadOnlyDictionary<string, string> MaskedSecrets);
