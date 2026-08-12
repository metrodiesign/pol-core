namespace BuildingBlocks.Infrastructure.Vault;

public enum VaultSecretVersionState
{
    Staged = 1,
    Active = 2,
    Retired = 3,
    Discarded = 4,
}

/// <summary>Immutable ciphertext version; only lifecycle metadata changes.</summary>
public sealed class VaultSecretVersion
{
    public Guid Id { get; private set; }
    public Guid MerchantId { get; private set; }
    public string SecretName { get; private set; } = default!;
    public int Version { get; private set; }
    public string SecretKey { get; private set; } = default!;
    public byte[] EncryptedDek { get; private set; } = default!;
    public byte[] EncryptedSecret { get; private set; } = default!;
    public string Hint { get; private set; } = default!;
    public VaultSecretVersionState State { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime? ExpiresAt { get; private set; }
    public DateTime? ActivatedAt { get; private set; }
    public DateTime? RetiredAt { get; private set; }

    private VaultSecretVersion() { }

    public VaultSecretVersion(
        Guid id,
        Guid merchantId,
        string secretName,
        int version,
        string secretKey,
        byte[] encryptedDek,
        byte[] encryptedSecret,
        string hint,
        DateTime createdAt,
        DateTime? expiresAt)
    {
        if (id == Guid.Empty || merchantId == Guid.Empty || version < 1)
            throw new ArgumentException("Secret version identity is invalid.");
        ArgumentException.ThrowIfNullOrWhiteSpace(secretName);
        ArgumentException.ThrowIfNullOrWhiteSpace(secretKey);
        Id = id;
        MerchantId = merchantId;
        SecretName = secretName.Trim();
        Version = version;
        SecretKey = secretKey.Trim();
        EncryptedDek = encryptedDek;
        EncryptedSecret = encryptedSecret;
        Hint = hint;
        State = VaultSecretVersionState.Staged;
        CreatedAt = createdAt;
        ExpiresAt = expiresAt;
    }

    public void Activate(DateTime now)
    {
        if (State == VaultSecretVersionState.Active)
            return;
        if (State != VaultSecretVersionState.Staged)
            throw new InvalidOperationException("Only a staged secret version can activate.");
        State = VaultSecretVersionState.Active;
        ActivatedAt = now;
    }

    public void Retire(DateTime now)
    {
        if (State == VaultSecretVersionState.Retired)
            return;
        if (State != VaultSecretVersionState.Active)
            throw new InvalidOperationException("Only an active secret version can retire.");
        State = VaultSecretVersionState.Retired;
        RetiredAt = now;
    }

    public void Discard(DateTime now)
    {
        if (State == VaultSecretVersionState.Discarded)
            return;
        if (State != VaultSecretVersionState.Staged)
            throw new InvalidOperationException("Only a staged secret version can be discarded.");
        State = VaultSecretVersionState.Discarded;
        RetiredAt = now;
    }
}
