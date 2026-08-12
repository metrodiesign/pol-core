using System.Security.Cryptography;
using System.Text;
using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Vault;
using Microsoft.EntityFrameworkCore;

namespace Persistence.MerchantRuntime.Vault;

/// <summary>
/// Self-hosted envelope-encryption vault (PLAN decision #14). Per secret: a random DEK encrypts the
/// plaintext (AES-256-GCM); a per-merchant KEK, derived from a keyring master key, wraps the DEK. Only
/// ciphertext + a last-4 hint are persisted; plaintext is revealed solely to server-side PSP calls and
/// never logged. The master key is rotatable: <see cref="StoreAsync"/> stamps the keyring's ACTIVE id into
/// the blob, and <see cref="RevealAsync"/> decrypts with the key the blob recorded — failing CLOSED if that
/// key id is no longer in the keyring (no silent fallback to the active key).
/// </summary>
internal sealed class LocalEnvelopeVaultStore : IVaultSecretStore
{
    private readonly MerchantRuntimeDbContext _db;
    private readonly IClock _clock;
    private readonly VaultKeyring _keyring;
    private readonly IVaultRevealAuditWriter _auditWriter;

    public LocalEnvelopeVaultStore(MerchantRuntimeDbContext db, IClock clock, VaultKeyring keyring, IVaultRevealAuditWriter auditWriter)
    {
        _db = db;
        _clock = clock;
        _keyring = keyring;
        _auditWriter = auditWriter;
    }

    public async Task StoreAsync(Guid merchantId, string name, string plaintextSecret, CancellationToken cancellationToken)
    {
        var (activeKeyId, masterKey) = _keyring.Active;
        var kek = VaultEnvelope.DeriveKek(masterKey, merchantId);
        var dek = RandomNumberGenerator.GetBytes(32);
        try
        {
            var encryptedSecret = VaultEnvelope.Encrypt(dek, Encoding.UTF8.GetBytes(plaintextSecret));
            var wrappedDek = VaultEnvelope.Encrypt(kek, dek);
            var hint = LastFour(plaintextSecret);

            var existing = await _db.VaultSecrets
                .FirstOrDefaultAsync(x => x.MerchantId == merchantId && x.SecretName == name, cancellationToken)
                .ConfigureAwait(false);
            if (existing is null)
                _db.VaultSecrets.Add(new VaultSecretBlob(merchantId, name, activeKeyId, wrappedDek, encryptedSecret, hint, _clock.UtcNow));
            else
                existing.Rotate(wrappedDek, activeKeyId, encryptedSecret, hint, _clock.UtcNow);

            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
            CryptographicOperations.ZeroMemory(kek);
        }
    }

    /// <summary>Insert-only write for the provisioning path: NO read-before-write, so a principal granted
    /// only INSERT on merch.VaultSecrets can store a secret without ever holding SELECT (the migration
    /// keeps pol_admin from reading plaintext back). Tracks the row but does NOT save — the caller's unit of
    /// work commits it in the provisioning transaction, where a (merchantId, name) collision becomes a
    /// translated 409 rather than a 500.</summary>
    public Task InsertAsync(Guid merchantId, string name, string plaintextSecret, CancellationToken cancellationToken)
    {
        var (activeKeyId, masterKey) = _keyring.Active;
        var kek = VaultEnvelope.DeriveKek(masterKey, merchantId);
        var dek = RandomNumberGenerator.GetBytes(32);
        try
        {
            var encryptedSecret = VaultEnvelope.Encrypt(dek, Encoding.UTF8.GetBytes(plaintextSecret));
            var wrappedDek = VaultEnvelope.Encrypt(kek, dek);
            var hint = LastFour(plaintextSecret);

            _db.VaultSecrets.Add(new VaultSecretBlob(merchantId, name, activeKeyId, wrappedDek, encryptedSecret, hint, _clock.UtcNow));
            return Task.CompletedTask;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
            CryptographicOperations.ZeroMemory(kek);
        }
    }

    public async Task<string> RevealAsync(Guid merchantId, string name, CancellationToken cancellationToken)
    {
        var blob = await PlatformReadGuard.ReadAsync(ct => _db.VaultSecrets
                .FirstOrDefaultAsync(x => x.MerchantId == merchantId && x.SecretName == name, ct), cancellationToken)
                .ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Vault secret '{name}' not found for the merchant.");

        // Fail CLOSED on an unknown/retired key id — never fall back to the active key (that would mask a
        // custody mistake or let a forged SecretKey downgrade to a different key). The id is a non-secret label.
        var masterKey = _keyring.ResolveOrNull(blob.SecretKey)
            ?? throw new InvalidOperationException($"Vault key id '{blob.SecretKey}' is not in the active keyring.");

        var kek = VaultEnvelope.DeriveKek(masterKey, merchantId);
        byte[] dek = [];
        string plaintext;
        try
        {
            dek = VaultEnvelope.Decrypt(kek, blob.EncryptedDek);
            plaintext = Encoding.UTF8.GetString(VaultEnvelope.Decrypt(dek, blob.EncryptedSecret));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
            CryptographicOperations.ZeroMemory(kek);
        }

        // Record the reveal tamper-evidently before returning. Fail CLOSED on an audit-write error — a
        // secret that reached process memory must never escape unaudited.
        await _auditWriter.AppendAsync(merchantId, name, cancellationToken).ConfigureAwait(false);
        return plaintext;
    }

    public async Task<string?> MaskedAsync(Guid merchantId, string name, CancellationToken cancellationToken)
    {
        var blob = await PlatformReadGuard.ReadAsync(ct => _db.VaultSecrets
            .FirstOrDefaultAsync(x => x.MerchantId == merchantId && x.SecretName == name, ct), cancellationToken)
            .ConfigureAwait(false);
        return blob is null ? null : $"****{blob.Hint}";
    }

    public async Task<bool> ExistsAsync(Guid merchantId, string name, CancellationToken cancellationToken) =>
        await PlatformReadGuard.ReadAsync(ct => _db.VaultSecrets
            .AnyAsync(x => x.MerchantId == merchantId && x.SecretName == name, ct), cancellationToken)
            .ConfigureAwait(false);

    public async Task<Guid> StageVersionAsync(
        Guid merchantId,
        string name,
        string plaintextSecret,
        string maskedHint,
        DateTime? expiresAt,
        CancellationToken cancellationToken)
    {
        if (merchantId == Guid.Empty)
            throw new ArgumentException("MerchantId is required.", nameof(merchantId));
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(plaintextSecret);
        ArgumentException.ThrowIfNullOrWhiteSpace(maskedHint);

        var nextVersion = await PlatformReadGuard.ReadAsync(ct => _db.VaultSecretVersions.IgnoreQueryFilters()
            .Where(x => x.MerchantId == merchantId && x.SecretName == name)
            .Select(x => (int?)x.Version).MaxAsync(ct), cancellationToken) + 1 ?? 1;
        var (activeKeyId, masterKey) = _keyring.Active;
        var kek = VaultEnvelope.DeriveKek(masterKey, merchantId);
        var dek = RandomNumberGenerator.GetBytes(32);
        try
        {
            var id = Guid.CreateVersion7();
            var encryptedSecret = VaultEnvelope.Encrypt(dek, Encoding.UTF8.GetBytes(plaintextSecret));
            var wrappedDek = VaultEnvelope.Encrypt(kek, dek);
            _db.VaultSecretVersions.Add(new VaultSecretVersion(
                id, merchantId, name, nextVersion, activeKeyId, wrappedDek, encryptedSecret,
                maskedHint, _clock.UtcNow, expiresAt));
            return id;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
            CryptographicOperations.ZeroMemory(kek);
        }
    }

    public async Task<string> ReadVersionForServerAsync(
        Guid merchantId, Guid versionId, CancellationToken cancellationToken)
    {
        var version = await PlatformReadGuard.ReadAsync(ct => _db.VaultSecretVersions
            .IgnoreQueryFilters().AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == versionId && x.MerchantId == merchantId, ct), cancellationToken)
            ?? throw new KeyNotFoundException("Vault secret version was not found.");
        if (version.State is VaultSecretVersionState.Discarded
            || version.ExpiresAt is { } expiry && expiry <= _clock.UtcNow)
            throw new InvalidOperationException("Vault secret version is not readable.");

        var masterKey = _keyring.ResolveOrNull(version.SecretKey)
            ?? throw new InvalidOperationException("Vault key is unavailable.");
        var kek = VaultEnvelope.DeriveKek(masterKey, merchantId);
        byte[] dek = [];
        byte[] plaintext = [];
        try
        {
            dek = VaultEnvelope.Decrypt(kek, version.EncryptedDek);
            plaintext = VaultEnvelope.Decrypt(dek, version.EncryptedSecret);
            await _auditWriter.AppendAsync(merchantId, version.SecretName, cancellationToken).ConfigureAwait(false);
            return Encoding.UTF8.GetString(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(dek);
            CryptographicOperations.ZeroMemory(kek);
        }
    }

    public async Task ActivateVersionAsync(Guid merchantId, Guid versionId, CancellationToken cancellationToken)
    {
        var version = await LoadVersionAsync(merchantId, versionId, cancellationToken);
        version.Activate(_clock.UtcNow);
    }

    public async Task RetireVersionAsync(Guid merchantId, Guid versionId, CancellationToken cancellationToken)
    {
        var version = await LoadVersionAsync(merchantId, versionId, cancellationToken);
        version.Retire(_clock.UtcNow);
    }

    public async Task DiscardVersionAsync(Guid merchantId, Guid versionId, CancellationToken cancellationToken)
    {
        var version = await LoadVersionAsync(merchantId, versionId, cancellationToken);
        version.Discard(_clock.UtcNow);
    }

    public async Task<string?> MaskedVersionAsync(Guid merchantId, Guid versionId, CancellationToken cancellationToken) =>
        await PlatformReadGuard.ReadAsync(ct => _db.VaultSecretVersions.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.Id == versionId && x.MerchantId == merchantId)
            .Select(x => x.Hint).SingleOrDefaultAsync(ct), cancellationToken);

    private async Task<VaultSecretVersion> LoadVersionAsync(
        Guid merchantId, Guid versionId, CancellationToken cancellationToken)
    {
        var tracked = _db.VaultSecretVersions.Local.SingleOrDefault(x => x.Id == versionId && x.MerchantId == merchantId);
        return tracked ?? await PlatformReadGuard.ReadAsync(ct => _db.VaultSecretVersions.IgnoreQueryFilters()
            .SingleOrDefaultAsync(x => x.Id == versionId && x.MerchantId == merchantId, ct), cancellationToken)
            ?? throw new KeyNotFoundException("Vault secret version was not found.");
    }

    private static string LastFour(string secret) =>
        secret.Length <= 4 ? new string('*', secret.Length) : secret[^4..];
}
