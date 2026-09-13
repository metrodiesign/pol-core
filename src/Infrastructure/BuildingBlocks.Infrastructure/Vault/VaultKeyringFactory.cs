namespace BuildingBlocks.Infrastructure.Vault;

/// <summary>
/// Builds + validates a <see cref="VaultKeyring"/> from <see cref="VaultOptions"/> ONCE at startup, so a
/// misconfigured key fails the host boot with a diagnosable (secret-free) error rather than a runtime
/// decrypt failure. Each key is sourced FILE &gt; inline-base64 (the env override binds onto the inline
/// value automatically via .NET configuration). The legacy <see cref="VaultOptions.MasterKeyBase64"/> is
/// shimmed into a single entry id "local-envelope-v1" when no keyring is configured.
/// </summary>
public static class VaultKeyringFactory
{
    private const int KeyBytes = 32;

    public static VaultKeyring Build(VaultOptions options)
    {
        var entries = options.Keys;

        // Guard the legacy->keyring migration: both set almost always means the operator added the new
        // active key but forgot to move the legacy key into Keys[LegacyKeyId]. That would boot green (the
        // active key is valid) then fail CLOSED on every pre-rotation blob. Fail fast with an id-only message.
        if (entries.Count > 0 && !string.IsNullOrWhiteSpace(options.MasterKeyBase64))
            throw new InvalidOperationException(
                $"Both Vault:MasterKeyBase64 and Vault:Keys are set; move the legacy key into " +
                $"Vault:Keys:{VaultOptions.LegacyKeyId} and clear Vault:MasterKeyBase64.");

        // Legacy shim: no explicit keyring -> one entry under the id every pre-rotation blob carries.
        if (entries.Count == 0)
        {
            if (string.IsNullOrWhiteSpace(options.MasterKeyBase64))
                throw new InvalidOperationException(
                    "Vault is not configured: set Vault:Keys (with a KeyFile or KeyBase64) or the legacy Vault:MasterKeyBase64.");

            return new VaultKeyring(
                VaultOptions.LegacyKeyId,
                new Dictionary<string, byte[]>(StringComparer.Ordinal)
                {
                    [VaultOptions.LegacyKeyId] = DecodeKey(VaultOptions.LegacyKeyId, options.MasterKeyBase64),
                });
        }

        var keys = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var (id, entry) in entries)
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new InvalidOperationException("Vault keyring has an entry with an empty id.");
            if (keys.ContainsKey(id))
                throw new InvalidOperationException($"Vault keyring has a duplicate key id '{id}'.");
            keys[id] = DecodeKey(id, ResolveRawKey(id, entry));
        }

        var activeId = options.ActiveKeyId
            ?? (keys.Count == 1 ? keys.Keys.First() : null)
            ?? throw new InvalidOperationException("Vault:ActiveKeyId must be set when multiple keys are configured.");

        if (!keys.ContainsKey(activeId))
            throw new InvalidOperationException($"Vault:ActiveKeyId '{activeId}' is not present in the keyring.");

        return new VaultKeyring(activeId, keys);
    }

    /// <summary>FILE first (a mounted secret), else the inline/env base64. The id only is named on error.</summary>
    private static string ResolveRawKey(string id, VaultKeyEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.KeyFile))
        {
            if (!File.Exists(entry.KeyFile))
                throw new InvalidOperationException($"Vault key '{id}' KeyFile does not exist at the configured path.");
            return File.ReadAllText(entry.KeyFile).Trim();
        }

        if (!string.IsNullOrWhiteSpace(entry.KeyBase64))
            return entry.KeyBase64.Trim();

        throw new InvalidOperationException($"Vault key '{id}' has neither KeyFile nor KeyBase64 configured.");
    }

    private static byte[] DecodeKey(string id, string base64)
    {
        byte[] key;
        try
        {
            key = Convert.FromBase64String(base64.Trim());
        }
        catch (FormatException)
        {
            throw new InvalidOperationException($"Vault key '{id}' is not valid base64.");
        }

        if (key.Length != KeyBytes)
            throw new InvalidOperationException($"Vault key '{id}' must decode to exactly {KeyBytes} bytes.");

        return key;
    }
}
