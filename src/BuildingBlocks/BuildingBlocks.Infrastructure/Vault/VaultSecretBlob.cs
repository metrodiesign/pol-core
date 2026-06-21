namespace BuildingBlocks.Infrastructure.Vault;

/// <summary>
/// Envelope-encrypted secret at rest: the data key (DEK) encrypts the secret, the per-tenant key
/// (KEK) encrypts the DEK. Only ciphertext and a non-sensitive <see cref="Hint"/> (last few chars)
/// are stored; the plaintext is never persisted and the hint is the only thing display/audit reads.
/// </summary>
public sealed class VaultSecretBlob
{
    public Guid TenantId { get; private set; }
    public string Name { get; private set; } = default!;
    public string KeyId { get; private set; } = default!;
    public byte[] EncryptedDek { get; private set; } = default!;
    public byte[] EncryptedSecret { get; private set; } = default!;
    public string Hint { get; private set; } = default!;
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }

    private VaultSecretBlob() { }

    public VaultSecretBlob(
        Guid tenantId,
        string name,
        string keyId,
        byte[] encryptedDek,
        byte[] encryptedSecret,
        string hint,
        DateTime utcNow)
    {
        TenantId = tenantId;
        Name = name;
        KeyId = keyId;
        EncryptedDek = encryptedDek;
        EncryptedSecret = encryptedSecret;
        Hint = hint;
        CreatedAtUtc = utcNow;
        UpdatedAtUtc = utcNow;
    }

    /// <summary>Overwrites the secret with a new value, re-encrypted under the current active key.</summary>
    public void Rotate(byte[] encryptedDek, string keyId, byte[] encryptedSecret, string hint, DateTime utcNow)
    {
        EncryptedDek = encryptedDek;
        KeyId = keyId;
        EncryptedSecret = encryptedSecret;
        Hint = hint;
        UpdatedAtUtc = utcNow;
    }

    /// <summary>Re-wraps the DEK under a new master key (master-key rotation): only the wrapped DEK and the
    /// key id change. The secret ciphertext + hint are untouched — the plaintext is never re-encrypted.</summary>
    public void Rewrap(byte[] encryptedDek, string keyId, DateTime utcNow)
    {
        EncryptedDek = encryptedDek;
        KeyId = keyId;
        UpdatedAtUtc = utcNow;
    }
}
