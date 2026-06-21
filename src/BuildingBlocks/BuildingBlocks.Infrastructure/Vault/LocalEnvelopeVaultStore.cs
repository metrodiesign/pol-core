using System.Security.Cryptography;
using System.Text;
using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BuildingBlocks.Infrastructure.Vault;

/// <summary>
/// Local/dev envelope-encryption vault (PLAN decision #14). Per secret: a random DEK encrypts the
/// plaintext (AES-256-GCM); a per-tenant KEK, derived from a configured master key, wraps the DEK.
/// Only ciphertext + a last-4 hint are persisted; plaintext is revealed solely to server-side PSP
/// calls and never logged.
/// ponytail: KEK is derived from a local master key here. The production upgrade is to move KEK
/// custody to KMS/HSM behind this same <see cref="IVaultSecretStore"/> seam — no caller changes.
/// </summary>
public sealed class LocalEnvelopeVaultStore : IVaultSecretStore
{
    private const string KeyId = "local-envelope-v1";
    private static readonly byte[] KekInfo = "pol-core/vault/kek/v1"u8.ToArray();

    private readonly ProducerDbContext _db;
    private readonly IClock _clock;
    private readonly byte[] _masterKey;

    public LocalEnvelopeVaultStore(ProducerDbContext db, IClock clock, IOptions<VaultOptions> options)
    {
        _db = db;
        _clock = clock;

        var master = options.Value.MasterKeyBase64;
        if (string.IsNullOrWhiteSpace(master))
            throw new InvalidOperationException("Vault:MasterKey is not configured.");

        _masterKey = Convert.FromBase64String(master);
        if (_masterKey.Length != 32)
            throw new InvalidOperationException("Vault:MasterKey must decode to exactly 32 bytes.");
    }

    public async Task StoreAsync(Guid tenantId, string name, string plaintextSecret, CancellationToken cancellationToken)
    {
        var kek = DeriveKek(tenantId);
        var dek = RandomNumberGenerator.GetBytes(32);
        try
        {
            var encryptedSecret = EncryptGcm(dek, Encoding.UTF8.GetBytes(plaintextSecret));
            var wrappedDek = EncryptGcm(kek, dek);
            var hint = LastFour(plaintextSecret);

            var existing = await _db.VaultSecrets.FindAsync([tenantId, name], cancellationToken).ConfigureAwait(false);
            if (existing is null)
                _db.VaultSecrets.Add(new VaultSecretBlob(tenantId, name, KeyId, wrappedDek, encryptedSecret, hint, _clock.UtcNow));
            else
                existing.Rotate(wrappedDek, encryptedSecret, hint, _clock.UtcNow);

            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
            CryptographicOperations.ZeroMemory(kek);
        }
    }

    public async Task<string> RevealAsync(Guid tenantId, string name, CancellationToken cancellationToken)
    {
        var blob = await _db.VaultSecrets.FindAsync([tenantId, name], cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Vault secret '{name}' not found for the tenant.");

        var kek = DeriveKek(tenantId);
        byte[] dek = [];
        try
        {
            dek = DecryptGcm(kek, blob.EncryptedDek);
            var plaintext = DecryptGcm(dek, blob.EncryptedSecret);
            return Encoding.UTF8.GetString(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
            CryptographicOperations.ZeroMemory(kek);
        }
    }

    public async Task<string?> MaskedAsync(Guid tenantId, string name, CancellationToken cancellationToken)
    {
        var blob = await _db.VaultSecrets.FindAsync([tenantId, name], cancellationToken).ConfigureAwait(false);
        return blob is null ? null : $"****{blob.Hint}";
    }

    public Task<bool> ExistsAsync(Guid tenantId, string name, CancellationToken cancellationToken) =>
        _db.VaultSecrets.AnyAsync(x => x.TenantId == tenantId && x.Name == name, cancellationToken);

    private byte[] DeriveKek(Guid tenantId) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, _masterKey, outputLength: 32, salt: tenantId.ToByteArray(), info: KekInfo);

    private static string LastFour(string secret) =>
        secret.Length <= 4 ? new string('*', secret.Length) : secret[^4..];

    private static byte[] EncryptGcm(byte[] key, byte[] plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[AesGcm.TagByteSizes.MaxSize];

        using var aes = new AesGcm(key, tag.Length);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        var packed = new byte[nonce.Length + ciphertext.Length + tag.Length];
        Buffer.BlockCopy(nonce, 0, packed, 0, nonce.Length);
        Buffer.BlockCopy(ciphertext, 0, packed, nonce.Length, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, packed, nonce.Length + ciphertext.Length, tag.Length);
        return packed;
    }

    private static byte[] DecryptGcm(byte[] key, byte[] packed)
    {
        var nonceSize = AesGcm.NonceByteSizes.MaxSize;
        var tagSize = AesGcm.TagByteSizes.MaxSize;

        var nonce = packed.AsSpan(0, nonceSize);
        var tag = packed.AsSpan(packed.Length - tagSize, tagSize);
        var ciphertext = packed.AsSpan(nonceSize, packed.Length - nonceSize - tagSize);

        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(key, tagSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        return plaintext;
    }
}
