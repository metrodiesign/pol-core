using Admin.Application;
using Admin.Domain;
using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Admin.Infrastructure.Persistence;

/// <summary>Admin realm persistence over the shared producer data plane. The host binds these to the
/// pol_admin (RLS-bypass) connection — admin tables are control-plane (no per-merchant predicate) and
/// resolution/provisioning run cross-merchant.</summary>
public sealed class PlatformUserRepository : IPlatformUserRepository
{
    private readonly PolDbContext _db;
    private readonly ILogger<PlatformUserRepository> _logger;

    public PlatformUserRepository(PolDbContext db, ILogger<PlatformUserRepository> logger)
    {
        _db = db;
        _logger = logger;
    }

    public void Add(PlatformUser account) => _db.Set<PlatformUser>().Add(account);
    public void AddAssignment(PlatformMerchantAccess assignment) => _db.Set<PlatformMerchantAccess>().Add(assignment);
    public void RemoveAssignment(PlatformMerchantAccess assignment) => _db.Set<PlatformMerchantAccess>().Remove(assignment);

    public Task<PlatformUser?> GetBySubjectAsync(string subject, CancellationToken cancellationToken) =>
        _db.Set<PlatformUser>().FirstOrDefaultAsync(x => x.Subject == subject, cancellationToken);

    public Task<PlatformUser?> GetByEmailAsync(string email, CancellationToken cancellationToken) =>
        _db.Set<PlatformUser>().FirstOrDefaultAsync(x => x.Email == email, cancellationToken);

    public Task<PlatformUser?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        _db.Set<PlatformUser>().FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

    public Task<bool> ExistsAsync(Guid id, CancellationToken cancellationToken) =>
        _db.Set<PlatformUser>().AnyAsync(x => x.Id == id, cancellationToken);

    public async Task<IReadOnlySet<Guid>> ListAssignedMerchantIdsAsync(Guid adminAccountId, CancellationToken cancellationToken)
    {
        var ids = await _db.Set<PlatformMerchantAccess>()
            .Where(x => x.PlatformUserId == adminAccountId)
            .Select(x => x.MerchantId)
            .ToListAsync(cancellationToken);
        return ids.ToHashSet();
    }

    public Task<PlatformMerchantAccess?> GetAssignmentAsync(Guid adminAccountId, Guid merchantId, CancellationToken cancellationToken) =>
        _db.Set<PlatformMerchantAccess>()
            .FirstOrDefaultAsync(x => x.PlatformUserId == adminAccountId && x.MerchantId == merchantId, cancellationToken);

    public async Task<PagedResult<PlatformUserListItem>> ListAsync(PagedQuery query, CancellationToken cancellationToken)
    {
        IQueryable<PlatformUser> src = _db.Set<PlatformUser>().AsNoTracking()
            .ApplySearch(query.Search)
            .ApplyFilters(query.Filters, _logger);

        long total = await src.LongCountAsync(cancellationToken);   // count after filter/search, before paging

        // Offset in long so a huge page can never overflow int into a negative SQL OFFSET; the Hosts parser
        // already clamps page to the offset ceiling.
        int skip = (int)Math.Min((long)(query.Page - 1) * query.Limit, int.MaxValue);

        // PlatformUser has no computed member, so project server-side directly. SubjectBound => Subject IS NOT NULL.
        var items = await src
            .ApplySort(query.Sort, _logger)
            .Skip(skip)
            .Take(query.Limit)
            .Select(a => new PlatformUserListItem(a.Id, a.Email, a.Tier, a.Status, a.CreatedAt, a.Subject != null))
            .ToListAsync(cancellationToken);

        return new PagedResult<PlatformUserListItem>(items, query.Page, query.Limit, total);
    }
}

public sealed class PlatformUserAuditWriter : IPlatformUserAuditWriter
{
    private readonly PolDbContext _db;

    public PlatformUserAuditWriter(PolDbContext db) => _db = db;

    public void Append(PlatformUserAudit entry) => _db.Set<PlatformUserAudit>().Add(entry);
}
