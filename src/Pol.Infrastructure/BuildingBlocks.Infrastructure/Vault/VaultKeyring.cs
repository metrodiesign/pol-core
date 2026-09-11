namespace BuildingBlocks.Infrastructure.Vault;

/// <summary>
/// An immutable, validated set of vault master keys (key id -> 32-byte key) plus the active id new secrets
/// are written under. Built once at startup by <see cref="VaultKeyringFactory"/> (fail-fast), so the store
/// never re-reads a secret file or re-decodes base64 per request. Reads resolve a blob's recorded key id via
/// <see cref="ResolveOrNull"/> and FAIL CLOSED (return null) on an unknown id — never a fallback to the
/// active key, which would mask a custody mistake or enable a downgrade via a forged key id.
/// </summary>
public sealed class VaultKeyring
{
    private readonly IReadOnlyDictionary<string, byte[]> _keys;

    public VaultKeyring(string activeKeyId, IReadOnlyDictionary<string, byte[]> keys)
    {
        ActiveKeyId = activeKeyId;
        _keys = keys;
    }

    public string ActiveKeyId { get; }

    /// <summary>The id + 32-byte key new secrets are encrypted under.</summary>
    public (string Id, byte[] Key) Active => (ActiveKeyId, _keys[ActiveKeyId]);

    /// <summary>The 32-byte key for <paramref name="keyId"/>, or null if the id is not in the keyring
    /// (the caller must fail closed — a retired/unknown id must never silently decrypt).</summary>
    public byte[]? ResolveOrNull(string keyId) =>
        _keys.TryGetValue(keyId, out var key) ? key : null;

    /// <summary>The set of key ids the keyring can decrypt — used by the rotation retire-gate to confirm no
    /// blob references a key before it is removed.</summary>
    public IReadOnlyCollection<string> KeyIds => (IReadOnlyCollection<string>)_keys.Keys;
}
