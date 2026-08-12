using Merchants.Domain;

namespace Merchants.Application;

/// <summary>
/// Persistence port for the <see cref="Merchant"/> master record. Provisioning + admin read-back only.
/// Runtime uses one DB principal; the persistence adapter is therefore a narrow, allowlisted query-filter
/// escape hatch. Permission and accessible-merchant checks remain at the Admin host seam.
/// </summary>
public interface IMerchantRepository
{
    /// <summary>Stages a new merchant for insertion. Persisted on the next unit-of-work save.</summary>
    void Add(Merchant merchant);

    /// <summary>Resolves a merchant by its normalized code, or null.</summary>
    Task<Merchant?> GetByCodeAsync(string normalizedCode, CancellationToken cancellationToken);

    /// <summary>True if a merchant with the normalized code already exists (idempotency pre-check).</summary>
    Task<bool> ExistsByCodeAsync(string normalizedCode, CancellationToken cancellationToken);
}
