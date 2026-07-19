using System.Security.Cryptography;
using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Vault;
using Microsoft.EntityFrameworkCore;

namespace Persistence.MerchantRuntime.Vault;

/// <summary>
/// Re-wraps a merchant's data keys onto the keyring's active master key (master-key rotation). Decrypts only
/// the DEK under the blob's recorded key and re-wraps it under the active key — the secret ciphertext and
/// hint are never touched, so the plaintext is never materialized. Skips blobs already on the active key, so
/// it is idempotent and safe to re-run.
/// </summary>
internal sealed class VaultMaintenance : IVaultMaintenance
{
    private readonly MerchantRuntimeDbContext _db;
    private readonly IClock _clock;
    private readonly VaultKeyring _keyring;

    public VaultMaintenance(MerchantRuntimeDbContext db, IClock clock, VaultKeyring keyring)
    {
        _db = db;
        _clock = clock;
        _keyring = keyring;
    }

    public async Task<int> RewrapMerchantToActiveKeyAsync(Guid merchantId, CancellationToken cancellationToken)
    {
        var (activeId, activeKey) = _keyring.Active;

        var blobs = await _db.VaultSecrets
            .Where(b => b.MerchantId == merchantId && b.KeyId != activeId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        if (blobs.Count == 0)
            return 0;

        var newKek = VaultEnvelope.DeriveKek(activeKey, merchantId);
        try
        {
            foreach (var blob in blobs)
            {
                var oldKey = _keyring.ResolveOrNull(blob.KeyId)
                    ?? throw new InvalidOperationException(
                        $"Vault key id '{blob.KeyId}' is not in the active keyring; cannot rotate this blob.");

                var oldKek = VaultEnvelope.DeriveKek(oldKey, merchantId);
                byte[] dek = [];
                try
                {
                    dek = VaultEnvelope.Decrypt(oldKek, blob.EncryptedDek);
                    var rewrappedDek = VaultEnvelope.Encrypt(newKek, dek);
                    blob.Rewrap(rewrappedDek, activeId, _clock.UtcNow);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(dek);
                    CryptographicOperations.ZeroMemory(oldKek);
                }
            }

            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return blobs.Count;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(newKek);
        }
    }
}
