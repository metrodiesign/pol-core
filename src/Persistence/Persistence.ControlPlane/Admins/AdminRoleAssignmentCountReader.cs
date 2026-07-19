using Microsoft.EntityFrameworkCore;

namespace Persistence.ControlPlane;

/// <summary>
/// The admin half of <c>Api.Iam.HostRoleAssignmentCounter</c>'s combined admin+merchant count (task 8.5.1) —
/// counts <c>admin.RoleAssignments</c> only. A later step re-derives the host's counter by combining this with
/// the Persistence.MerchantUsers side's equivalent reader, so neither host-level type needs to name
/// <see cref="ControlPlaneDbContext"/> directly.
/// </summary>
public interface IAdminRoleAssignmentCountReader
{
    Task<int> CountAsync(Guid roleId, CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<Guid, int>> CountManyAsync(IReadOnlyCollection<Guid> roleIds, CancellationToken cancellationToken);
}

internal sealed class AdminRoleAssignmentCountReader : IAdminRoleAssignmentCountReader
{
    private readonly ControlPlaneDbContext _db;

    public AdminRoleAssignmentCountReader(ControlPlaneDbContext db) => _db = db;

    public Task<int> CountAsync(Guid roleId, CancellationToken cancellationToken) =>
        _db.RoleAssignments.CountAsync(a => a.RoleId == roleId, cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, int>> CountManyAsync(
        IReadOnlyCollection<Guid> roleIds, CancellationToken cancellationToken)
    {
        if (roleIds.Count == 0)
            return new Dictionary<Guid, int>();

        var counts = await _db.RoleAssignments.Where(a => roleIds.Contains(a.RoleId))
            .GroupBy(a => a.RoleId).Select(g => new { RoleId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.RoleId, x => x.Count, cancellationToken);
        return roleIds.ToDictionary(id => id, id => counts.GetValueOrDefault(id));
    }
}
