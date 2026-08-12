namespace BuildingBlocks.Application;

/// <summary>
/// Write-only-from-outside secret custody for PSP credentials (PLAN decision #14). Secrets are
/// stored under envelope encryption with a per-merchant KEK; the plaintext is revealed only to
/// server-side PSP calls and is never logged. Display and audit paths use <see cref="MaskedAsync"/>.
/// The concrete KEK custody provider (Azure Key Vault / AWS KMS / SQL Always Encrypted) is a
/// pre-implementation hosting decision; <see cref="IVaultSecretStore"/> is the stable seam.
/// <para>For a PSP connection the revealed plaintext is a per-PSP camelCase JSON envelope, NOT a raw key
/// (2C2P: {merchantId, secretKey}; Omise: {secretKey, promptPayTemplateId, promptPayTeamId}). The seam
/// stays a single reveal; each PSP adapter parses the envelope it expects. Every adapter and test double
/// MUST agree on this shape.</para>
/// </summary>
public interface IVaultSecretStore
{
    Task StoreAsync(Guid merchantId, string name, string plaintextSecret, CancellationToken cancellationToken);

    /// <summary>Write a brand-new secret WITHOUT reading first (no rotation, no existence probe). The
    /// provisioning path runs under a principal granted INSERT-only on the vault — it must never SELECT —
    /// so this is the only write it can make there. The caller guarantees (merchantId, name) is new (a fresh
    /// merchant); a collision surfaces as the unit of work's unique-violation conflict, not a silent rotate.</summary>
    Task InsertAsync(Guid merchantId, string name, string plaintextSecret, CancellationToken cancellationToken);

    /// <summary>Reveal plaintext. Server-side PSP calls only — never return to a client, never log.</summary>
    Task<string> RevealAsync(Guid merchantId, string name, CancellationToken cancellationToken);

    /// <summary>Masked representation for display/audit (e.g. <c>sk_live_****abcd</c>).</summary>
    Task<string?> MaskedAsync(Guid merchantId, string name, CancellationToken cancellationToken);

    Task<bool> ExistsAsync(Guid merchantId, string name, CancellationToken cancellationToken);

    Task<Guid> StageVersionAsync(
        Guid merchantId,
        string name,
        string plaintextSecret,
        string maskedHint,
        DateTime? expiresAt,
        CancellationToken cancellationToken) =>
        Task.FromException<Guid>(new NotSupportedException("Versioned vault storage is not implemented."));

    Task<string> ReadVersionForServerAsync(
        Guid merchantId, Guid versionId, CancellationToken cancellationToken) =>
        Task.FromException<string>(new NotSupportedException("Versioned vault storage is not implemented."));

    Task ActivateVersionAsync(Guid merchantId, Guid versionId, CancellationToken cancellationToken) =>
        Task.FromException(new NotSupportedException("Versioned vault storage is not implemented."));
    Task RetireVersionAsync(Guid merchantId, Guid versionId, CancellationToken cancellationToken) =>
        Task.FromException(new NotSupportedException("Versioned vault storage is not implemented."));
    Task DiscardVersionAsync(Guid merchantId, Guid versionId, CancellationToken cancellationToken) =>
        Task.FromException(new NotSupportedException("Versioned vault storage is not implemented."));
    Task<string?> MaskedVersionAsync(Guid merchantId, Guid versionId, CancellationToken cancellationToken) =>
        Task.FromException<string?>(new NotSupportedException("Versioned vault storage is not implemented."));
}
