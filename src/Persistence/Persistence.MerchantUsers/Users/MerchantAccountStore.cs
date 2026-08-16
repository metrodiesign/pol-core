using Merchants.Application.Users;
using Microsoft.EntityFrameworkCore;
using SharedKernel;
using MerchantUserAccount = Merchants.Domain.Users.User;

namespace Persistence.MerchantUsers.Users;

/// <summary>
/// The pre-bind TRACKED account seam (<see cref="IAccountStore"/>, bugfix-merchant-prebind-wiring F2/F3/F4):
/// registration/correction submission is anonymous (ticket-gated) and the admin approve/reject target load has
/// no bound merchant actor either — in both flows the target row's <c>MerchantId</c> is NULL until approval, so
/// the ordinary filtered <c>IUserRepository</c> can never see it. Reads tracked (the handlers mutate the
/// aggregate through its domain methods and commit via their unit of work); every staged change still passes
/// the sealed write floor (<c>GuardedRuntimeDbContext</c> + <c>IWriteAuthorizer</c>) at SaveChanges — this port
/// bypasses only the READ filter, never the write guard. Sanctioned <c>IgnoreQueryFilters()</c> port
/// (Architecture.Tests bypass-primitive allowlist names this file).
/// </summary>
internal sealed class MerchantAccountStore(MerchantUserDbContext db) : IAccountStore
{
    public Task<MerchantUserAccount?> FindByIdentityAsync(ProviderIdentity identity, CancellationToken cancellationToken) =>
        db.Users.IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Provider == identity.Provider && u.Subject == identity.Subject, cancellationToken);

    public Task<MerchantUserAccount?> FindByIdAsync(Guid id, CancellationToken cancellationToken) =>
        db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Id == id, cancellationToken);

    public void Add(MerchantUserAccount account) => db.Users.Add(account);
}
