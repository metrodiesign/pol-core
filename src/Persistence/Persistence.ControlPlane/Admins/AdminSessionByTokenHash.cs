using Admins.Domain.Users;
using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;

namespace Persistence.ControlPlane.Admins;

/// <summary>rls-to-query-filter task 5 pre-bind read port over <c>ControlPlaneDbContext</c> (no query filter
/// on <c>admin.Sessions</c> to suppress — control-plane, cross-merchant by design). AsNoTracking +
/// server-side projection: never materializes the full <see cref="Session"/> aggregate.</summary>
internal sealed class AdminSessionByTokenHash(ControlPlaneDbContext db) : ISessionByTokenHash
{
    public async Task<SessionLookup?> FindByTokenHashAsync(byte[] tokenHash, CancellationToken cancellationToken) =>
        await db.Sessions.AsNoTracking()
            .Where(s => s.TokenHash == tokenHash)
            .Select(s => new SessionLookup(
                s.Id, s.FamilyId, s.AdminUserId, (SessionLookupStatus)s.Status,
                s.IdleExpiresAt, s.AbsoluteExpiresAt, s.SupersededAt, s.SupersededBySessionId))
            .FirstOrDefaultAsync(cancellationToken);
}
