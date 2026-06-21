namespace BuildingBlocks.Infrastructure.Vault;

/// <summary>
/// Vault custody config bound from the "Vault" section. The master keys live in a versioned keyring
/// (<see cref="Keys"/> + <see cref="ActiveKeyId"/>) so the master key can be rotated without re-encrypting
/// secrets: each <see cref="VaultSecretBlob.KeyId"/> records which key wrapped its DEK, and new writes use
/// the active key. Each key is sourced from a mounted secret FILE or env/appsettings — never committed.
/// <para><see cref="MasterKeyBase64"/> is the legacy single-key shim: when no <see cref="Keys"/> are
/// configured it becomes the one entry id "local-envelope-v1" (the id every pre-rotation blob carries), so
/// existing hosts/tests keep working with zero config change.</para>
/// </summary>
public sealed class VaultOptions
{
    public const string SectionName = "Vault";

    /// <summary>The legacy id every blob written before key rotation carries; also the shim entry's id.</summary>
    public const string LegacyKeyId = "local-envelope-v1";

    /// <summary>The key id new secrets are encrypted under. Defaults to the only key when one is configured.</summary>
    public string? ActiveKeyId { get; set; }

    /// <summary>The keyring: key id -> where its 32-byte master key is sourced. Keyed by id so the standard
    /// .NET env override <c>Vault__Keys__&lt;Id&gt;__KeyFile</c> binds by id (a List would bind by index).</summary>
    public Dictionary<string, VaultKeyEntry> Keys { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Legacy single 32-byte master key (base64). Shimmed into the keyring as
    /// <see cref="LegacyKeyId"/> when <see cref="Keys"/> is empty. DEV/back-compat only — production uses
    /// <see cref="Keys"/> with a mounted <see cref="VaultKeyEntry.KeyFile"/>.</summary>
    public string MasterKeyBase64 { get; set; } = string.Empty;
}

/// <summary>One keyring entry's key source. Exactly one of <see cref="KeyFile"/> (a mounted secret file,
/// preferred for production) or <see cref="KeyBase64"/> (env/appsettings, dev) supplies the 32-byte key;
/// <see cref="KeyFile"/> takes precedence when both are set.</summary>
public sealed class VaultKeyEntry
{
    /// <summary>Path to a mounted secret file whose contents are the base64 32-byte key (Docker/K8s secret).</summary>
    public string? KeyFile { get; set; }

    /// <summary>The base64 32-byte key inline (env override <c>Vault__Keys__&lt;Id&gt;__KeyBase64</c> or dev appsettings).</summary>
    public string? KeyBase64 { get; set; }
}
