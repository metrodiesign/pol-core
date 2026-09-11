namespace BuildingBlocks.Infrastructure.Vault;

/// <summary>
/// Envelope-encrypted secret at rest: the data key (DEK) encrypts the secret, the per-merchant key
/// (KEK) encrypts the DEK. Only ciphertext and a non-sensitive <see cref="Hint"/> (last few chars)
/// are stored; the plaintext is never persisted and the hint is the only thing display/audit reads.
/// </summary>
public sealed class VaultSecretBlob
{
    public Guid MerchantId { get; private set; }
    public string SecretName { get; private set; } = default!;
    public string SecretKey { get; private set; } = default!;
    public byte[] EncryptedDek { get; private set; } = default!;
    public byte[] EncryptedSecret { get; private set; } = default!;
    public string Hint { get; private set; } = default!;
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }

    private VaultSecretBlob() { }

    public VaultSecretBlob(
        Guid merchantId,
        string name,
        string keyId,
        byte[] encryptedDek,
        byte[] encryptedSecret,
        string hint,
        DateTime utcNow)
    {
        MerchantId = merchantId;
        SecretName = name;
        SecretKey = keyId;
        EncryptedDek = encryptedDek;
        EncryptedSecret = encryptedSecret;
        Hint = hint;
        CreatedAt = utcNow;
        UpdatedAt = utcNow;
    }

    /// <summary>Overwrites the secret with a new value, re-encrypted under the current active key.</summary>
    public void Rotate(byte[] encryptedDek, string keyId, byte[] encryptedSecret, string hint, DateTime utcNow)
    {
        EncryptedDek = encryptedDek;
        SecretKey = keyId;
        EncryptedSecret = encryptedSecret;
        Hint = hint;
        UpdatedAt = utcNow;
    }

    /// <summary>Re-wraps the DEK under a new master key (master-key rotation): only the wrapped DEK and the
    /// key id change. The secret ciphertext + hint are untouched — the plaintext is never re-encrypted.</summary>
    public void Rewrap(byte[] encryptedDek, string keyId, DateTime utcNow)
    {
        EncryptedDek = encryptedDek;
        SecretKey = keyId;
        UpdatedAt = utcNow;
    }
}
