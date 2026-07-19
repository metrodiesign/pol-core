using Merchants.Application;
using Merchants.Domain;
using Microsoft.EntityFrameworkCore;

namespace Persistence.MerchantRuntime.Merchants;

/// <summary>
/// The admin-console cross-context read for <see cref="Merchant"/> (task 8.5.7's <c>MerchantDirectory</c>
/// fix, <c>Api/Admins/HostWiring.cs</c>) — <c>Admins.Application</c>'s own <c>IAdminMerchantDirectory</c>
/// needs id-keyed/bulk lookups <see cref="IMerchantRepository"/> has no reason to carry (that port only ever
/// needs code-keyed lookups for its own module's handlers). Same class as <see cref="MerchantRepository"/>
/// implements both — one adapter, two narrow ports, no duplicated query logic.
/// </summary>
public interface IMerchantDirectoryReader
{
    Task<bool> IsActiveMerchantAsync(Guid merchantId, CancellationToken cancellationToken);
    Task<IReadOnlyDictionary<Guid, string>> GetCodesByIdsAsync(IReadOnlySet<Guid> merchantIds, CancellationToken cancellationToken);
    Task<Guid?> GetIdByCodeAsync(string code, CancellationToken cancellationToken);
}

/// <summary>EF Core repository for <see cref="Merchant"/> over the MerchantRuntime data plane. Unkeyed —
/// this cluster has no separate RLS-bypass principal to key against (REQ-8, "1 principal").</summary>
internal sealed class MerchantRepository : IMerchantRepository, IMerchantDirectoryReader
{
    private readonly MerchantRuntimeDbContext _db;

    public MerchantRepository(MerchantRuntimeDbContext db) => _db = db;

    public void Add(Merchant merchant) => _db.Set<Merchant>().Add(merchant);

    public Task<Merchant?> GetByCodeAsync(string normalizedCode, CancellationToken cancellationToken) =>
        _db.Set<Merchant>().FirstOrDefaultAsync(x => x.Code == normalizedCode, cancellationToken);

    public Task<bool> ExistsByCodeAsync(string normalizedCode, CancellationToken cancellationToken) =>
        _db.Set<Merchant>().AnyAsync(x => x.Code == normalizedCode, cancellationToken);

    public Task<bool> IsActiveMerchantAsync(Guid merchantId, CancellationToken cancellationToken) =>
        _db.Set<Merchant>().AnyAsync(t => t.Id == merchantId && t.Status == MerchantStatus.Active, cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, string>> GetCodesByIdsAsync(
        IReadOnlySet<Guid> merchantIds, CancellationToken cancellationToken)
    {
        if (merchantIds.Count == 0)
            return new Dictionary<Guid, string>();
        return await _db.Set<Merchant>()
            .Where(t => merchantIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => t.Code, cancellationToken);
    }

    // Bare id lookup (no projection, no PSP metadata) so the read seam can apply the accessible-merchant floor
    // before loading a full merchant view. Unknown code -> null (the seam treats null as inaccessible).
    public Task<Guid?> GetIdByCodeAsync(string code, CancellationToken cancellationToken) =>
        _db.Set<Merchant>()
            .Where(t => t.Code == code)
            .Select(t => (Guid?)t.Id)
            .FirstOrDefaultAsync(cancellationToken);
}
