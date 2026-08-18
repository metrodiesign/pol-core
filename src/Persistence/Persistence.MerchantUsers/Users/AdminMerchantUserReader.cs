using BuildingBlocks.Application;
using Merchants.Domain.Users;
using Microsoft.EntityFrameworkCore;

namespace Persistence.MerchantUsers.Users;

// Narrow AdminSession read port. Every bypass remains constrained by explicit Admin scope data.
internal sealed partial class MerchantUserRepository
{
    public async Task<PagedResult<User>> ListForAdminAsync(
        PagedQuery query, Guid? roleId, Guid? merchantId, bool isUnrestricted,
        IReadOnlySet<Guid> accessibleMerchantIds, CancellationToken cancellationToken)
    {
        IQueryable<User> source = _db.Users.IgnoreQueryFilters().AsNoTracking();
        source = source.Where(user => user.MerchantId.HasValue
            && (user.Status == UserStatus.Active || user.Status == UserStatus.Suspended));
        source = merchantId is { } selected
            ? source.Where(user => user.MerchantId == selected)
            : isUnrestricted
                ? source
                : source.Where(user => user.MerchantId.HasValue
                    && accessibleMerchantIds.Contains(user.MerchantId.Value));
        source = source.ApplyFilters(query.Filters, _logger);
        if (roleId is { } id)
            source = id == Guid.Empty
                ? source.Where(_ => false)
                : source.Where(user => _db.RoleAssignments.IgnoreQueryFilters()
                    .Any(a => a.UserId == user.Id && a.RoleId == id));
        var total = await source.LongCountAsync(cancellationToken);
        var skip = (int)Math.Min((long)(query.Page - 1) * query.Limit, int.MaxValue);
        var items = await source.ApplySort(query.Sort, _logger).Skip(skip).Take(query.Limit)
            .ToListAsync(cancellationToken);
        return new PagedResult<User>(items, query.Page, query.Limit, total);
    }

    public Task<User?> FindByIdForAdminAsync(
        Guid id, bool isUnrestricted, IReadOnlySet<Guid> accessibleMerchantIds,
        CancellationToken cancellationToken) =>
        _db.Users.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(user =>
            user.Id == id && (isUnrestricted || (user.MerchantId.HasValue
                && accessibleMerchantIds.Contains(user.MerchantId.Value))), cancellationToken);
}
