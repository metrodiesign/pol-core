namespace BuildingBlocks.Application;

/// <summary>
/// Write-only-from-outside secret custody for PSP credentials (PLAN decision #14). Secrets are
/// stored under envelope encryption with a per-tenant KEK; the plaintext is revealed only to
/// server-side PSP calls and is never logged. Display and audit paths use <see cref="MaskedAsync"/>.
/// The concrete KEK custody provider (Azure Key Vault / AWS KMS / SQL Always Encrypted) is a
/// pre-implementation hosting decision; <see cref="IVaultSecretStore"/> is the stable seam.
/// </summary>
public interface IVaultSecretStore
{
    Task StoreAsync(Guid tenantId, string name, string plaintextSecret, CancellationToken cancellationToken);

    /// <summary>Reveal plaintext. Server-side PSP calls only — never return to a client, never log.</summary>
    Task<string> RevealAsync(Guid tenantId, string name, CancellationToken cancellationToken);

    /// <summary>Masked representation for display/audit (e.g. <c>sk_live_****abcd</c>).</summary>
    Task<string?> MaskedAsync(Guid tenantId, string name, CancellationToken cancellationToken);

    Task<bool> ExistsAsync(Guid tenantId, string name, CancellationToken cancellationToken);
}
