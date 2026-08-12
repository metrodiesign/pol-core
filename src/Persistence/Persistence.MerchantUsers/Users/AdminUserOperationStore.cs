using Merchants.Application.Users;
using Merchants.Domain.Users;
using Microsoft.EntityFrameworkCore;

namespace Persistence.MerchantUsers.Users;

// Exact-key replay lookup for AdminSession idempotency; writes still pass IWriteAuthorizer.
internal sealed class AdminUserOperationStore(MerchantUserDbContext db) : IAdminUserOperationStore
{
    public Task<AdminUserOperationRecord?> FindAsync(
        Guid? merchantId, Guid actorId, string operation, string idempotencyKey,
        CancellationToken cancellationToken) =>
        db.AdminUserOperationRecords.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(x =>
            x.MerchantId == merchantId && x.ActorId == actorId && x.Operation == operation
            && x.IdempotencyKey == idempotencyKey, cancellationToken);

    public void Add(AdminUserOperationRecord record) => db.AdminUserOperationRecords.Add(record);
}
