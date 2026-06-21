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

    public void Rotate(byte[] encryptedDek, byte[] encryptedSecret, string hint, DateTime utcNow)
    {
        EncryptedDek = encryptedDek;
        EncryptedSecret = encryptedSecret;
        Hint = hint;
        UpdatedAtUtc = utcNow;
    }
}
