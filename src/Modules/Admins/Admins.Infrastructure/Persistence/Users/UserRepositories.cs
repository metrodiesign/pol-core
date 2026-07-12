using Admins.Application.Users;
using Admins.Domain.Users;
using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Admins.Infrastructure.Persistence.Users;

/// <summary>Admin realm persistence over the shared admin data plane. The host binds these to the
/// pol_admin (RLS-bypass) connection — admin tables are control-plane (no per-merchant predicate) and
/// resolution/provisioning run cross-merchant.</summary>
public sealed class UserRepository : IUserRepository
{
    private readonly PolDbContext _db;
    private readonly ILogger<UserRepository> _logger;

    public UserRepository(PolDbContext db, ILogger<UserRepository> logger)
    {
        _db = db;
        _logger = logger;
    }

    public void Add(User account) => _db.Set<User>().Add(account);
    public void AddAssignment(MerchantAccess assignment) => _db.Set<MerchantAccess>().Add(assignment);
    public void RemoveAssignment(MerchantAccess assignment) => _db.Set<MerchantAccess>().Remove(assignment);

    public Task<User?> GetBySubjectAsync(string subject, CancellationToken cancellationToken) =>
        _db.Set<User>().FirstOrDefaultAsync(x => x.Subject == subject, cancellationToken);

    public Task<User?> GetByEmailAsync(string email, CancellationToken cancellationToken) =>
        _db.Set<User>().FirstOrDefaultAsync(x => x.Email == email, cancellationToken);

    public Task<User?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        _db.Set<User>().FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

    public Task<bool> ExistsAsync(Guid id, CancellationToken cancellationToken) =>
        _db.Set<User>().AnyAsync(x => x.Id == id, cancellationToken);

    public async Task<IReadOnlySet<Guid>> ListAssignedMerchantIdsAsync(Guid adminAccountId, CancellationToken cancellationToken)
    {
        var ids = await _db.Set<MerchantAccess>()
            .Where(x => x.PlatformUserId == adminAccountId)
            .Select(x => x.MerchantId)
            .ToListAsync(cancellationToken);
        return ids.ToHashSet();
    }

    public Task<MerchantAccess?> GetAssignmentAsync(Guid adminAccountId, Guid merchantId, CancellationToken cancellationToken) =>
        _db.Set<MerchantAccess>()
            .FirstOrDefaultAsync(x => x.PlatformUserId == adminAccountId && x.MerchantId == merchantId, cancellationToken);

    public async Task<PagedResult<UserListItem>> ListAsync(PagedQuery query, CancellationToken cancellationToken)
    {
        IQueryable<User> src = _db.Set<User>().AsNoTracking()
            .ApplySearch(query.Search)
            .ApplyFilters(query.Filters, _logger);

        long total = await src.LongCountAsync(cancellationToken);   // count after filter/search, before paging

        // Offset in long so a huge page can never overflow int into a negative SQL OFFSET; the Hosts parser
        // already clamps page to the offset ceiling.
        int skip = (int)Math.Min((long)(query.Page - 1) * query.Limit, int.MaxValue);

        // User has no computed member, so project server-side directly. SubjectBound => Subject IS NOT NULL.
        var items = await src
            .ApplySort(query.Sort, _logger)
            .Skip(skip)
            .Take(query.Limit)
            .Select(a => new UserListItem(a.Id, a.Email, a.Tier, a.Status, a.CreatedAt, a.Subject != null))
            .ToListAsync(cancellationToken);

        return new PagedResult<UserListItem>(items, query.Page, query.Limit, total);
    }
}

public sealed class AuditWriter : IAuditWriter
{
    private readonly PolDbContext _db;

    public AuditWriter(PolDbContext db) => _db = db;

    public void Append(Audit entry) => _db.Set<Audit>().Add(entry);
}
