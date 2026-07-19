using Merchants.Application.Users.Roles;
using Merchants.Domain.Users.Roles;
using Microsoft.EntityFrameworkCore;

namespace Persistence.MerchantUser.Users;

/// <summary>
/// rls-to-query-filter task 8.5.2 PARTIAL mirror of
/// <c>Merchants.Infrastructure.Persistence.Users.Roles.RoleRepository</c> onto <see cref="MerchantUserDbContext"/>.
/// Only the 5 members that touch <c>merch.RoleAssignments</c> alone are implemented here
/// (<see cref="AddAssignment"/>/<see cref="RemoveAssignment"/>/<see cref="ListRoleIdsForUserAsync"/>/
/// <see cref="GetAssignmentAsync"/>/<see cref="AssignmentExistsAsync"/>) — they resolve against this
/// context's own <c>RoleAssignments</c> set and nothing else, so the ordinary query filter
/// (<c>MerchantId==CurrentMerchant</c>) now scopes them to the bound actor's own merchant. That is a
/// defense-in-depth IMPROVEMENT over the old <c>PolDbContext</c>/RLS-bypass version for these call sites, not
/// a behavior loss.
/// <para>
/// The remaining 4 members (<see cref="GetRoleIdsByCodesAsync"/>/<see cref="GetActiveRoleIdsByCodesAsync"/>/
/// <see cref="ListEffectivePermissionsAsync"/>/<see cref="ListActiveRoleCodesForUserAsync"/>) join
/// <c>merch.RoleAssignments</c> against <c>iam.Roles</c>/<c>iam.RolePermissions</c> — but <c>iam.*</c> now
/// lives in <c>ControlPlaneDbContext</c>, a DIFFERENT runtime context this assembly may never reference
/// (<c>Persistence.MerchantUser.csproj</c>: "NEVER Iam.Domain"; <c>Directory.Build.props</c>'s
/// <c>RlsToQueryFilter_EnforcePersistenceBoundaries</c> target forbids a ProjectReference to
/// <c>Persistence.ControlPlane</c> from here; <c>ModelDisjointnessTests</c> would fail if <c>Role</c>/
/// <c>RolePermission</c> were mapped into BOTH contexts anyway). This is a genuine cross-context read —
/// design.md's cross-cutting section names exactly this shape as a port (<c>IMerchantRoleReader</c>), the
/// same pattern <c>IRoleAssignmentCounter</c>/<c>HostRoleAssignmentCounter</c> (<c>Api/Iam/RoleHostWiring.cs</c>)
/// already uses to combine two contexts' counts at the host. These 4 members throw
/// <see cref="NotSupportedException"/> until a host-composed reader exists to replace them — flagged in task
/// 8.5.2's report as a real gap, not fixed here (out of this assembly's reach by design).
/// </para>
/// </summary>
internal sealed class MerchantUserRoleRepository : IRoleRepository
{
    private readonly MerchantUserDbContext _db;
    public MerchantUserRoleRepository(MerchantUserDbContext db) => _db = db;

    public void AddAssignment(RoleAssignment assignment) => _db.RoleAssignments.Add(assignment);
    public void RemoveAssignment(RoleAssignment assignment) => _db.RoleAssignments.Remove(assignment);

    public async Task<IReadOnlySet<Guid>> ListRoleIdsForUserAsync(Guid merchantUserId, CancellationToken cancellationToken)
    {
        var ids = await _db.RoleAssignments
            .Where(a => a.MerchantUserId == merchantUserId)
            .Select(a => a.RoleId)
            .ToListAsync(cancellationToken);
        return ids.ToHashSet();
    }

    public Task<RoleAssignment?> GetAssignmentAsync(Guid merchantUserId, Guid roleId, CancellationToken cancellationToken) =>
        _db.RoleAssignments
            .FirstOrDefaultAsync(a => a.MerchantUserId == merchantUserId && a.RoleId == roleId, cancellationToken);

    public Task<bool> AssignmentExistsAsync(Guid merchantUserId, Guid roleId, CancellationToken cancellationToken) =>
        _db.RoleAssignments.AnyAsync(a => a.MerchantUserId == merchantUserId && a.RoleId == roleId, cancellationToken);

    public Task<IReadOnlyDictionary<string, Guid>> GetRoleIdsByCodesAsync(
        Guid merchantId, IReadOnlyCollection<string> codes, CancellationToken cancellationToken) =>
        throw CrossContextNotSupported();

    public Task<IReadOnlyDictionary<string, Guid>> GetActiveRoleIdsByCodesAsync(
        Guid merchantId, IReadOnlyCollection<string> codes, CancellationToken cancellationToken) =>
        throw CrossContextNotSupported();

    public Task<IReadOnlySet<string>> ListEffectivePermissionsAsync(
        Guid merchantUserId, Guid merchantId, CancellationToken cancellationToken) =>
        throw CrossContextNotSupported();

    public Task<IReadOnlyList<string>> ListActiveRoleCodesForUserAsync(
        Guid merchantUserId, Guid merchantId, CancellationToken cancellationToken) =>
        throw CrossContextNotSupported();

    private static NotSupportedException CrossContextNotSupported() => new(
        "This IRoleRepository member needs iam.Roles/RolePermissions, which live in ControlPlaneDbContext — a " +
        "different runtime context Persistence.MerchantUser may never reference (rls-to-query-filter " +
        "design.md \"iam read via port\", REQ-11.8 boundary). Not implementable here; needs a host-composed " +
        "IMerchantRoleReader-shaped port (same pattern as HostRoleAssignmentCounter, Api/Iam/RoleHostWiring.cs) " +
        "before this method can be called.");
}
