using BuildingBlocks.Application;
using Merchants.Domain.Users;
using Microsoft.EntityFrameworkCore;

namespace Persistence.MerchantUsers.Users;

/// <summary>rls-to-query-filter task 5 pre-bind read port over <c>MerchantUserDbContext</c>. <c>merch.Sessions</c>
/// carries no query filter (only <c>merch.Users</c>/<c>RoleAssignments</c> are merchant-filtered in this
/// context, task 2) — a token-hash lookup happens BEFORE any actor is known, so there is nothing to
/// suppress. AsNoTracking + server-side projection: never materializes the full <see cref="Session"/> aggregate.</summary>
internal sealed class MerchantSessionByTokenHash(MerchantUserDbContext db) : ISessionByTokenHash
{
    public async Task<SessionLookup?> FindByTokenHashAsync(byte[] tokenHash, CancellationToken cancellationToken) =>
        await db.Sessions.AsNoTracking()
            .Where(s => s.TokenHash == tokenHash)
            .Select(s => new SessionLookup(
                s.Id, s.FamilyId, s.UserId, (SessionLookupStatus)s.Status,
                s.IdleExpiresAt, s.AbsoluteExpiresAt, s.SupersededAt, s.SupersededBySessionId))
            .FirstOrDefaultAsync(cancellationToken);
}
