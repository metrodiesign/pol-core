namespace Tenant.Application.ProvisionTenant;

/// <summary>The provisioning outcome. Secrets are masked (never plaintext).</summary>
public sealed record ProvisionTenantResult(
    Guid TenantId,
    IReadOnlyList<ProvisionedConnection> Connections);

/// <summary>A provisioned PSP connection with its masked secret hints (field -> "****hint").</summary>
public sealed record ProvisionedConnection(
    Guid PspConnectionId,
    string Psp,
    IReadOnlyDictionary<string, string> MaskedSecrets);
