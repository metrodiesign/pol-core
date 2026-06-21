namespace BuildingBlocks.Infrastructure.Vault;

public sealed class VaultOptions
{
    public const string SectionName = "Vault";

    /// <summary>
    /// 32-byte AES master key, base64-encoded. DEV/LOCAL custody only — it stands in for the
    /// external KMS/HSM that holds the per-tenant KEK in production (PLAN decision #14). Supplied
    /// via environment/secret manager, never committed.
    /// </summary>
    public string MasterKeyBase64 { get; set; } = string.Empty;
}
