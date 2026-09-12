namespace BuildingBlocks.Application;

/// <summary>
/// Master-key rotation maintenance — deliberately OFF <see cref="IVaultSecretStore"/> so the reveal/store
/// seam the request path uses stays minimal. Run from an admin/maintenance entrypoint after the keyring's
/// active key is rolled: it re-wraps a merchant's data keys onto the new master key without ever decrypting
/// the secret plaintext. The cross-merchant sweep and the retire-gate (no blob may reference an old key id
/// before it is removed from the keyring) are operational steps in the rotation runbook.
/// </summary>
public interface IVaultMaintenance
{
    /// <summary>Re-wraps every secret of <paramref name="merchantId"/> currently under a non-active key id onto
    /// the active key. Returns the number of blobs re-wrapped (0 if already all-active). Idempotent.</summary>
    Task<int> RewrapMerchantToActiveKeyAsync(Guid merchantId, CancellationToken cancellationToken);
}
