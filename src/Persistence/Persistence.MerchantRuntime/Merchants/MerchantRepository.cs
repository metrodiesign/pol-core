using Merchants.Application;
using Merchants.Domain;
using Microsoft.EntityFrameworkCore;

namespace Persistence.MerchantRuntime.Merchants;

/// <summary>
/// The admin-console cross-context read for <see cref="Merchant"/> (task 8.5.7's <c>MerchantDirectory</c>
/// fix, <c>Api/Admins/HostWiring.cs</c>) — every method is an explicit-key escape hatch because an Admin actor
/// deliberately has no ambient merchant. <c>Admins.Application</c>'s own <c>IAdminMerchantDirectory</c> needs
/// id-keyed/bulk lookups <see cref="IMerchantRepository"/> has no reason to carry. Same adapter implements both
/// narrow ports, keeping the bypass in one allowlisted file.
/// </summary>
public interface IMerchantDirectoryReader
{
    Task<bool> IsActiveMerchantAsync(Guid merchantId, CancellationToken cancellationToken);
    Task<IReadOnlyDictionary<Guid, string>> GetCodesByIdsAsync(IReadOnlySet<Guid> merchantIds, CancellationToken cancellationToken);
    Task<Guid?> GetIdByCodeAsync(string code, CancellationToken cancellationToken);
}

/// <summary>EF Core repository for Admin provisioning/read-back over the MerchantRuntime data plane. Unkeyed —
/// this cluster has no separate RLS-bypass principal (REQ-8, "1 principal"), so these narrow Admin ports must
/// suppress the ambient merchant query filter and constrain every query by an explicit code/id/set.</summary>
internal sealed class MerchantRepository : IMerchantRepository, IMerchantDirectoryReader
{
    private readonly MerchantRuntimeDbContext _db;

    public MerchantRepository(MerchantRuntimeDbContext db) => _db = db;

    public void Add(Merchant merchant) => _db.Set<Merchant>().Add(merchant);

    public Task<Merchant?> GetByCodeAsync(string normalizedCode, CancellationToken cancellationToken) =>
        PlatformReadGuard.ReadAsync(ct => _db.Set<Merchant>().IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.Code == normalizedCode, ct), cancellationToken);

    public Task<bool> ExistsByCodeAsync(string normalizedCode, CancellationToken cancellationToken) =>
        PlatformReadGuard.ReadAsync(ct => _db.Set<Merchant>().IgnoreQueryFilters()
            .AnyAsync(x => x.Code == normalizedCode, ct), cancellationToken);

    public Task<bool> IsActiveMerchantAsync(Guid merchantId, CancellationToken cancellationToken) =>
        PlatformReadGuard.ReadAsync(ct => _db.Set<Merchant>().IgnoreQueryFilters()
            .AnyAsync(t => t.Id == merchantId && t.Status == MerchantStatus.Active, ct), cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, string>> GetCodesByIdsAsync(
        IReadOnlySet<Guid> merchantIds, CancellationToken cancellationToken)
    {
        if (merchantIds.Count == 0)
            return new Dictionary<Guid, string>();
        return await PlatformReadGuard.ReadAsync(ct => _db.Set<Merchant>().IgnoreQueryFilters()
            .Where(t => merchantIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => t.Code, ct), cancellationToken);
    }

    // Bare id lookup (no projection, no PSP metadata) so the read seam can apply the accessible-merchant floor
    // before loading a full merchant view. Unknown code -> null (the seam treats null as inaccessible).
    public Task<Guid?> GetIdByCodeAsync(string code, CancellationToken cancellationToken)
    {
        var normalizedCode = MerchantCode.Normalize(code);
        return PlatformReadGuard.ReadAsync(ct => _db.Set<Merchant>().IgnoreQueryFilters()
            .Where(t => t.Code == normalizedCode)
            .Select(t => (Guid?)t.Id)
            .FirstOrDefaultAsync(ct), cancellationToken);
    }
}
