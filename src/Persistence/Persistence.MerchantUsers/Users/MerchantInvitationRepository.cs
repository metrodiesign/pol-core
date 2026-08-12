using Merchants.Application.Users;
using Merchants.Domain.Users;
using Microsoft.EntityFrameworkCore;

namespace Persistence.MerchantUsers.Users;

/// <summary>
/// Tenant-filtered invitation store plus two exact pre-bind reads. Bypass queries accept only token hash or
/// invitation id; callers recheck pending state, expiry, and verified email before binding merchant scope.
/// </summary>
internal sealed class MerchantInvitationRepository(MerchantUserDbContext db) : IInvitationRepository
{
    public Task<MerchantUserInvitation?> FindByIdAsync(Guid id, CancellationToken cancellationToken) =>
        db.Invitations.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

    public Task<MerchantUserInvitation?> FindPendingByNormalizedEmailAsync(
        string normalizedEmail, CancellationToken cancellationToken) =>
        db.Invitations.FirstOrDefaultAsync(x => x.NormalizedEmail == normalizedEmail
            && x.AcceptedAt == null && x.RevokedAt == null, cancellationToken);

    public Task<MerchantUserInvitation?> FindByTokenHashUnfilteredAsync(
        string tokenHash, CancellationToken cancellationToken) =>
        db.Invitations.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(x => x.TokenHash == tokenHash, cancellationToken);

    public Task<MerchantUserInvitation?> FindByIdUnfilteredAsync(Guid id, CancellationToken cancellationToken) =>
        db.Invitations.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

    public Task<MerchantUserInvitation?> FindAcceptedByUserIdAsync(Guid userId, CancellationToken cancellationToken) =>
        db.Invitations.FirstOrDefaultAsync(x => x.AcceptedByUserId == userId, cancellationToken);

    public void Add(MerchantUserInvitation invitation) => db.Invitations.Add(invitation);
}
