using Microsoft.EntityFrameworkCore;

namespace Persistence.MerchantUsers;

/// <summary>
/// The merchant half of <c>Api.Iam.HostRoleAssignmentCounter</c>'s combined admin+merchant count (task
/// 8.5.2) — mirrors <c>Persistence.ControlPlane.IAdminRoleAssignmentCountReader</c>'s precedent for the
/// admin side, widened with an explicit merchant dimension because (unlike <c>admin.RoleAssignments</c>)
/// <c>merch.RoleAssignments</c> genuinely needs to be scoped per-merchant OR summed across every merchant,
/// matching <c>HostRoleAssignmentCounter</c>'s existing Merchant-scope (own-merchant only) vs Platform-scope
/// (merchant HALF = summed across every merchant) branches. A later step re-derives the host's counter by
/// combining this with <c>Persistence.ControlPlane</c>'s admin-side reader, so neither host-level type needs
/// to name <see cref="MerchantUserDbContext"/> directly.
/// <para>
/// <see cref="CountGlobalAsync"/>/<see cref="CountManyGlobalAsync"/> deliberately <c>IgnoreQueryFilters()</c>:
/// they read across every merchant by design (a shared/platform role's total assignment count), which the
/// ordinary <c>MerchantId==CurrentMerchant</c> filter would otherwise collapse to zero for any caller that is
/// not itself bound to a merchant (e.g. an admin-console request). <see cref="CountForMerchantAsync"/>/
/// <see cref="CountManyForMerchantAsync"/> also <c>IgnoreQueryFilters()</c> so they filter by the EXPLICIT
/// <c>merchantId</c> parameter the caller supplies rather than the ambient actor's own
/// <c>CurrentMerchant</c> — this port must work correctly for a caller with no bound merchant of its own
/// (the admin console counting a target merchant's assignments). Every method still filters by an explicit
/// roleId/merchantId parameter, never by ambient state, so this is a narrow escape-hatch port, not a
/// widening (Architecture.Tests bypass-primitive allowlist names this file).
/// </para>
/// </summary>
public interface IMerchantRoleAssignmentCountReader
{
    Task<int> CountForMerchantAsync(Guid roleId, Guid merchantId, CancellationToken cancellationToken);
    Task<int> CountGlobalAsync(Guid roleId, CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<Guid, int>> CountManyForMerchantAsync(
        IReadOnlyCollection<Guid> roleIds, Guid merchantId, CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<Guid, int>> CountManyGlobalAsync(
        IReadOnlyCollection<Guid> roleIds, CancellationToken cancellationToken);
}

internal sealed class MerchantRoleAssignmentCountReader : IMerchantRoleAssignmentCountReader
{
    private readonly MerchantUserDbContext _db;
    public MerchantRoleAssignmentCountReader(MerchantUserDbContext db) => _db = db;

    public Task<int> CountForMerchantAsync(Guid roleId, Guid merchantId, CancellationToken cancellationToken) =>
        _db.RoleAssignments.IgnoreQueryFilters()
            .CountAsync(a => a.RoleId == roleId && a.MerchantId == merchantId, cancellationToken);

    public Task<int> CountGlobalAsync(Guid roleId, CancellationToken cancellationToken) =>
        _db.RoleAssignments.IgnoreQueryFilters()
            .CountAsync(a => a.RoleId == roleId, cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, int>> CountManyForMerchantAsync(
        IReadOnlyCollection<Guid> roleIds, Guid merchantId, CancellationToken cancellationToken)
    {
        if (roleIds.Count == 0)
            return new Dictionary<Guid, int>();

        var counts = await _db.RoleAssignments.IgnoreQueryFilters()
            .Where(a => roleIds.Contains(a.RoleId) && a.MerchantId == merchantId)
            .GroupBy(a => a.RoleId).Select(g => new { RoleId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.RoleId, x => x.Count, cancellationToken);
        return roleIds.ToDictionary(id => id, id => counts.GetValueOrDefault(id));
    }

    public async Task<IReadOnlyDictionary<Guid, int>> CountManyGlobalAsync(
        IReadOnlyCollection<Guid> roleIds, CancellationToken cancellationToken)
    {
        if (roleIds.Count == 0)
            return new Dictionary<Guid, int>();

        var counts = await _db.RoleAssignments.IgnoreQueryFilters()
            .Where(a => roleIds.Contains(a.RoleId))
            .GroupBy(a => a.RoleId).Select(g => new { RoleId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.RoleId, x => x.Count, cancellationToken);
        return roleIds.ToDictionary(id => id, id => counts.GetValueOrDefault(id));
    }
}
