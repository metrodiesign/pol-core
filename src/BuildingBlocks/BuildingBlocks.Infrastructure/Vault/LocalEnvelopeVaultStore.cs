using System.Security.Cryptography;
using System.Text;
using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BuildingBlocks.Infrastructure.Vault;

/// <summary>
/// Self-hosted envelope-encryption vault (PLAN decision #14). Per secret: a random DEK encrypts the
/// plaintext (AES-256-GCM); a per-merchant KEK, derived from a keyring master key, wraps the DEK. Only
/// ciphertext + a last-4 hint are persisted; plaintext is revealed solely to server-side PSP calls and
/// never logged. The master key is rotatable: <see cref="StoreAsync"/> stamps the keyring's ACTIVE id into
/// the blob, and <see cref="RevealAsync"/> decrypts with the key the blob recorded — failing CLOSED if that
/// key id is no longer in the keyring (no silent fallback to the active key).
/// </summary>
public sealed class LocalEnvelopeVaultStore : IVaultSecretStore
{
    private readonly PolDbContext _db;
    private readonly IClock _clock;
    private readonly VaultKeyring _keyring;
    private readonly IVaultRevealAuditWriter _auditWriter;

    public LocalEnvelopeVaultStore(PolDbContext db, IClock clock, VaultKeyring keyring, IVaultRevealAuditWriter auditWriter)
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

            var existing = await _db.VaultSecrets.FindAsync([merchantId, name], cancellationToken).ConfigureAwait(false);
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
        var blob = await _db.VaultSecrets.FindAsync([merchantId, name], cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Vault secret '{name}' not found for the merchant.");

        // Fail CLOSED on an unknown/retired key id — never fall back to the active key (that would mask a
        // custody mistake or let a forged KeyId downgrade to a different key). The id is a non-secret label.
        var masterKey = _keyring.ResolveOrNull(blob.KeyId)
            ?? throw new InvalidOperationException($"Vault key id '{blob.KeyId}' is not in the active keyring.");

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
        var blob = await _db.VaultSecrets.FindAsync([merchantId, name], cancellationToken).ConfigureAwait(false);
        return blob is null ? null : $"****{blob.Hint}";
    }

    public Task<bool> ExistsAsync(Guid merchantId, string name, CancellationToken cancellationToken) =>
        _db.VaultSecrets.AnyAsync(x => x.MerchantId == merchantId && x.Name == name, cancellationToken);

    private static string LastFour(string secret) =>
        secret.Length <= 4 ? new string('*', secret.Length) : secret[^4..];
}
