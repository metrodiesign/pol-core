using Microsoft.EntityFrameworkCore;

namespace Persistence.MerchantUser;

/// <summary>
/// The merchant-side half of the <c>IMerchantRoleReader</c> cross-context composition (task 8.5.7): "which
/// role ids does this user hold IN THIS MERCHANT" — a plain single-context read against
/// <c>merch.RoleAssignments</c>, paired at the host with <c>Persistence.ControlPlane.Iam.IMerchantRoleReader</c>
/// (which resolves those ids against <c>iam.Roles</c>/<c>iam.RolePermissions</c>, a different runtime
/// context this assembly may never reference). <c>IgnoreQueryFilters()</c> + the explicit <c>merchantId</c>
/// parameter (rather than the ambient actor's own <c>CurrentMerchant</c>) mirrors
/// <see cref="MerchantRoleAssignmentCountReader"/>'s established escape-hatch pattern — this port must also
/// work for a caller with no bound merchant of its own (e.g. an admin-console request resolving a target
/// merchant user's permissions).
/// </summary>
public interface IMerchantRoleAssignmentReader
{
    Task<IReadOnlySet<Guid>> ListRoleIdsForUserInMerchantAsync(
        Guid merchantUserId, Guid merchantId, CancellationToken cancellationToken);
}

internal sealed class MerchantRoleAssignmentReader : IMerchantRoleAssignmentReader
{
    private readonly MerchantUserDbContext _db;
    public MerchantRoleAssignmentReader(MerchantUserDbContext db) => _db = db;

    public async Task<IReadOnlySet<Guid>> ListRoleIdsForUserInMerchantAsync(
        Guid merchantUserId, Guid merchantId, CancellationToken cancellationToken)
    {
        var ids = await _db.RoleAssignments.IgnoreQueryFilters()
            .Where(a => a.MerchantUserId == merchantUserId && a.MerchantId == merchantId)
            .Select(a => a.RoleId)
            .ToListAsync(cancellationToken);
        return ids.ToHashSet();
    }
}
