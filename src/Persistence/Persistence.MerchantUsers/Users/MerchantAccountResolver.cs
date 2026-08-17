using Merchants.Application.Users;
using Microsoft.EntityFrameworkCore;
using SharedKernel;

namespace Persistence.MerchantUsers.Users;

/// <summary>
/// The pre-bind read seam (<see cref="IAccountResolver"/>, bugfix-merchant-prebind-wiring F1/F6 — grown out of
/// rls-to-query-filter task 5's <c>MerchantResolveLoginBySubject</c>, now DI-wired at last). Login-by-subject
/// runs at the OIDC callback BEFORE any actor is bound (the whole point is discovering which merchant, if any,
/// this subject belongs to), and resolve-by-id runs INSIDE session authentication before the merchant_id claim
/// exists — the default query filter (<c>MerchantId==CurrentMerchant</c>, which for an unbound caller is
/// <c>Guid.Empty</c>) would hide EVERY row, including an already-approved user's real merchant. This is a
/// sanctioned <c>IgnoreQueryFilters()</c> narrow port (Architecture.Tests bypass-primitive allowlist names this
/// file); it only ever returns the <see cref="AccountSnapshot"/> projection, never the tracked aggregate.
/// </summary>
internal sealed class MerchantAccountResolver(MerchantUserDbContext db) : IAccountResolver
{
    public async Task<AccountSnapshot?> FindByIdentityAsync(ProviderIdentity identity, CancellationToken cancellationToken) =>
        await db.Users.IgnoreQueryFilters().AsNoTracking()
            .Where(u => u.Provider == identity.Provider && u.Subject == identity.Subject)
            .Select(u => new AccountSnapshot(u.Id, u.Subject, u.Email, u.MerchantId, u.Status, u.SaleCode, u.DisplayName))
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<AccountSnapshot?> FindByIdAsync(Guid id, CancellationToken cancellationToken) =>
        await db.Users.IgnoreQueryFilters().AsNoTracking()
            .Where(u => u.Id == id)
            .Select(u => new AccountSnapshot(u.Id, u.Subject, u.Email, u.MerchantId, u.Status, u.SaleCode, u.DisplayName))
            .FirstOrDefaultAsync(cancellationToken);
}
