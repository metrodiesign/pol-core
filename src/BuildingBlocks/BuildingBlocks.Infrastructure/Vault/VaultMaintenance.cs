using System.Security.Cryptography;
using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BuildingBlocks.Infrastructure.Vault;

/// <summary>
/// Re-wraps a tenant's data keys onto the keyring's active master key (master-key rotation). Decrypts only
/// the DEK under the blob's recorded key and re-wraps it under the active key — the secret ciphertext and
/// hint are never touched, so the plaintext is never materialized. Skips blobs already on the active key, so
/// it is idempotent and safe to re-run. Runs under the tenant's RLS scope (UPDATE on the tenant's own rows).
/// </summary>
public sealed class VaultMaintenance : IVaultMaintenance
{
    private readonly ProducerDbContext _db;
    private readonly IClock _clock;
    private readonly VaultKeyring _keyring;

    public VaultMaintenance(ProducerDbContext db, IClock clock, VaultKeyring keyring)
    {
        _db = db;
        _clock = clock;
        _keyring = keyring;
    }

    public async Task<int> RewrapTenantToActiveKeyAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var (activeId, activeKey) = _keyring.Active;

        var blobs = await _db.VaultSecrets
            .Where(b => b.TenantId == tenantId && b.KeyId != activeId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        if (blobs.Count == 0)
            return 0;

        var newKek = VaultEnvelope.DeriveKek(activeKey, tenantId);
        try
        {
            foreach (var blob in blobs)
            {
                var oldKey = _keyring.ResolveOrNull(blob.KeyId)
                    ?? throw new InvalidOperationException(
                        $"Vault key id '{blob.KeyId}' is not in the active keyring; cannot rotate this blob.");

                var oldKek = VaultEnvelope.DeriveKek(oldKey, tenantId);
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
