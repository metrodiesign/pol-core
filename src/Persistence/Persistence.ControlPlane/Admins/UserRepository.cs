using Admins.Application.Users;
using Admins.Domain.Users;
using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Persistence.ControlPlane.Governance;
using SharedKernel;

namespace Persistence.ControlPlane.Admins;

/// <summary>Admin realm persistence over <see cref="ControlPlaneDbContext"/> (task 8.5.1) — moved from
/// <c>Admins.Infrastructure.Persistence.Users.UserRepository</c>, same behavior, bound to the ControlPlane
/// context instead of the keyed pol_admin <c>PolDbContext</c>.</summary>
internal sealed class UserRepository : IUserRepository
{
    private readonly ControlPlaneDbContext _db;
    private readonly ILogger<UserRepository> _logger;
    private readonly ISecurityTelemetry _telemetry;
    private readonly GovernanceSqlLockManager _locks;

    public UserRepository(
        ControlPlaneDbContext db,
        ILogger<UserRepository> logger,
        ISecurityTelemetry telemetry,
        GovernanceSqlLockManager locks)
    {
        _db = db;
        _logger = logger;
        _telemetry = telemetry;
        _locks = locks;
    }

    public void Add(User account) => _db.Users.Add(account);
    public void AddAssignment(MerchantAccess assignment) => _db.MerchantAccess.Add(assignment);
    public void RemoveAssignment(MerchantAccess assignment) => _db.MerchantAccess.Remove(assignment);

    // ponytail: global lock keeps rare admin identity/email mutations deterministic and prevents sensitive
    // unique-key values reaching EF logs; split into hashed per-identity locks only if onboarding throughput matters.
    public Task AcquireIdentityMutationLockAsync(CancellationToken cancellationToken) =>
        _locks.AcquireAsync("admin-user-identity-mutation", cancellationToken);

    public async Task<IReadOnlyList<User>> ListTier0CandidatesAsync(
        string canonicalEmail, CancellationToken cancellationToken) =>
        await _db.Users
            .Where(account =>
                account.Provider == User.MicrosoftProvider && account.Subject == canonicalEmail
                || account.WorkforceEmailKey == canonicalEmail)
            .Take(2)
            .ToListAsync(cancellationToken);

    public Task<User?> GetByIdentityAsync(ProviderIdentity identity, CancellationToken cancellationToken) =>
        _db.Users.FirstOrDefaultAsync(x => x.Provider == identity.Provider && x.Subject == identity.Subject, cancellationToken);

    public Task<User?> GetByEmailAsync(string email, CancellationToken cancellationToken) =>
        _db.Users.FirstOrDefaultAsync(x => x.Email == email, cancellationToken);

    public Task<User?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        _db.Users.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

    public Task VerifyActiveSuperAsync(
        Guid callerId, long expectedAuthorizationVersion, CancellationToken cancellationToken) =>
        AuthorizationLease.VerifyActiveSuperAsync(
            _db, callerId, expectedAuthorizationVersion, _telemetry, cancellationToken);

    public Task<bool> ExistsAsync(Guid id, CancellationToken cancellationToken) =>
        _db.Users.AnyAsync(x => x.Id == id, cancellationToken);

    public async Task<IReadOnlySet<Guid>> ListAssignedMerchantIdsAsync(Guid adminAccountId, CancellationToken cancellationToken)
    {
        var ids = await _db.MerchantAccess
            .Where(x => x.AdminUserId == adminAccountId)
            .Select(x => x.MerchantId)
            .ToListAsync(cancellationToken);
        return ids.ToHashSet();
    }

    public Task<MerchantAccess?> GetAssignmentAsync(Guid adminAccountId, Guid merchantId, CancellationToken cancellationToken) =>
        _db.MerchantAccess
            .FirstOrDefaultAsync(x => x.AdminUserId == adminAccountId && x.MerchantId == merchantId, cancellationToken);

    public async Task<PagedResult<UserListItem>> ListAsync(PagedQuery query, CancellationToken cancellationToken)
    {
        IQueryable<User> src = _db.Users.AsNoTracking()
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
            .Select(a => new UserListItem(a.Id, a.Email, a.Tier, a.Status, a.CreatedAt, a.Subject != null, a.Version))
            .ToListAsync(cancellationToken);

        return new PagedResult<UserListItem>(items, query.Page, query.Limit, total);
    }
}

/// <summary>Moved from <c>Admins.Infrastructure.Persistence.Users.AuditWriter</c> (task 8.5.1).</summary>
internal sealed class AuditWriter : IAuditWriter
{
    private readonly ControlPlaneDbContext _db;

    public AuditWriter(ControlPlaneDbContext db) => _db = db;

    public void Append(Audit entry) => _db.UserAudits.Add(entry);
}
