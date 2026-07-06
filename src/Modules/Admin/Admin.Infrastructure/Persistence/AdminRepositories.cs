using Admin.Application;
using Admin.Domain;
using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Admin.Infrastructure.Persistence;

/// <summary>Admin realm persistence over the shared producer data plane. The host binds these to the
/// pol_admin (RLS-bypass) connection — admin tables are control-plane (no per-tenant predicate) and
/// resolution/provisioning run cross-tenant.</summary>
public sealed class AdminAccountRepository : IAdminAccountRepository
{
    private readonly ProducerDbContext _db;
    private readonly ILogger<AdminAccountRepository> _logger;

    public AdminAccountRepository(ProducerDbContext db, ILogger<AdminAccountRepository> logger)
    {
        _db = db;
        _logger = logger;
    }

    public void Add(AdminAccount account) => _db.Set<AdminAccount>().Add(account);
    public void AddAssignment(AdminTenantAssignment assignment) => _db.Set<AdminTenantAssignment>().Add(assignment);
    public void RemoveAssignment(AdminTenantAssignment assignment) => _db.Set<AdminTenantAssignment>().Remove(assignment);

    public Task<AdminAccount?> GetBySubjectAsync(string subject, CancellationToken cancellationToken) =>
        _db.Set<AdminAccount>().FirstOrDefaultAsync(x => x.Subject == subject, cancellationToken);

    public Task<AdminAccount?> GetByEmailAsync(string email, CancellationToken cancellationToken) =>
        _db.Set<AdminAccount>().FirstOrDefaultAsync(x => x.Email == email, cancellationToken);

    public Task<AdminAccount?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        _db.Set<AdminAccount>().FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

    public Task<bool> ExistsAsync(Guid id, CancellationToken cancellationToken) =>
        _db.Set<AdminAccount>().AnyAsync(x => x.Id == id, cancellationToken);

    public async Task<IReadOnlySet<Guid>> ListAssignedTenantIdsAsync(Guid adminAccountId, CancellationToken cancellationToken)
    {
        var ids = await _db.Set<AdminTenantAssignment>()
            .Where(x => x.AdminAccountId == adminAccountId)
            .Select(x => x.TenantId)
            .ToListAsync(cancellationToken);
        return ids.ToHashSet();
    }

    public Task<AdminTenantAssignment?> GetAssignmentAsync(Guid adminAccountId, Guid tenantId, CancellationToken cancellationToken) =>
        _db.Set<AdminTenantAssignment>()
            .FirstOrDefaultAsync(x => x.AdminAccountId == adminAccountId && x.TenantId == tenantId, cancellationToken);

    public async Task<PagedResult<AdminAccountListItem>> ListAsync(PagedQuery query, CancellationToken cancellationToken)
    {
        IQueryable<AdminAccount> src = _db.Set<AdminAccount>().AsNoTracking()
            .ApplySearch(query.Search)
            .ApplyFilters(query.Filters, _logger);

        long total = await src.LongCountAsync(cancellationToken);   // count after filter/search, before paging

        // Offset in long so a huge page can never overflow int into a negative SQL OFFSET; the Hosts parser
        // already clamps page to the offset ceiling.
        int skip = (int)Math.Min((long)(query.Page - 1) * query.Limit, int.MaxValue);

        // AdminAccount has no computed member, so project server-side directly. SubjectBound => Subject IS NOT NULL.
        var items = await src
            .ApplySort(query.Sort, _logger)
            .Skip(skip)
            .Take(query.Limit)
            .Select(a => new AdminAccountListItem(a.Id, a.Email, a.Tier, a.Status, a.CreatedAt, a.Subject != null))
            .ToListAsync(cancellationToken);

        return new PagedResult<AdminAccountListItem>(items, query.Page, query.Limit, total);
    }
}

public sealed class AdminAccountAuditWriter : IAdminAccountAuditWriter
{
    private readonly ProducerDbContext _db;

    public AdminAccountAuditWriter(ProducerDbContext db) => _db = db;

    public void Append(AdminAccountAudit entry) => _db.Set<AdminAccountAudit>().Add(entry);
}
