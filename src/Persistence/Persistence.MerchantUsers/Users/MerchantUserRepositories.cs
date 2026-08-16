using BuildingBlocks.Application;
using Merchants.Application.Users;
using Merchants.Domain.Users;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Persistence.MerchantUsers.Users;

/// <summary>
/// rls-to-query-filter task 8.5.2 mirror of
/// <c>Merchants.Infrastructure.Persistence.Users.UserRepositories</c> onto <see cref="MerchantUserDbContext"/>
/// (the <c>Merchants.Application.Users</c> ports). Unlike the old <c>PolDbContext</c>-bound versions — which
/// ran on the keyed pol_admin RLS-BYPASS connection and therefore saw every merchant's (and every
/// NULL-merchant pending) row regardless of the caller — these run under this context's ordinary query filter
/// (<c>MerchantId==CurrentMerchant</c>): a bound merchant actor sees only its own rows, same as every other
/// entity in this context. The pre-bind flows (login resolution, registration/correction submission, admin
/// approve/reject, session re-resolution by id) run through the dedicated filter-free seams instead —
/// <see cref="MerchantAccountResolver"/> (<c>IAccountResolver</c>) and <see cref="MerchantAccountStore"/>
/// (<c>IAccountStore</c>), wired by bugfix-merchant-prebind-wiring; this class is for ordinary BOUND-actor
/// call sites only (e.g. <c>SetUserRoles</c>).
/// </summary>
internal sealed partial class MerchantUserRepository : IUserRepository
{
    private readonly MerchantUserDbContext _db;
    private readonly Microsoft.Extensions.Logging.ILogger<MerchantUserRepository> _logger;
    public MerchantUserRepository(MerchantUserDbContext db,
        Microsoft.Extensions.Logging.ILogger<MerchantUserRepository> logger)
    {
        _db = db;
        _logger = logger;
    }
    internal MerchantUserRepository(MerchantUserDbContext db)
        : this(db, Microsoft.Extensions.Logging.Abstractions.NullLogger<MerchantUserRepository>.Instance) { }

    public Task<User?> FindByIdAsync(Guid id, CancellationToken cancellationToken) =>
        _db.Users.FirstOrDefaultAsync(u => u.Id == id, cancellationToken);

    public async Task<PagedResult<User>> ListAsync(PagedQuery query, Guid? roleId, CancellationToken cancellationToken)
    {
        IQueryable<User> source = _db.Users.AsNoTracking().ApplyFilters(query.Filters, _logger);
        if (roleId is { } id)
            source = id == Guid.Empty
                ? source.Where(_ => false)
                : source.Where(user => _db.RoleAssignments.Any(a => a.UserId == user.Id && a.RoleId == id));
        var total = await source.LongCountAsync(cancellationToken);
        var skip = (int)Math.Min((long)(query.Page - 1) * query.Limit, int.MaxValue);
        var items = await source.ApplySort(query.Sort, _logger).Skip(skip).Take(query.Limit)
            .ToListAsync(cancellationToken);
        return new PagedResult<User>(items, query.Page, query.Limit, total);
    }

    public void Add(User account) => _db.Users.Add(account);
}

internal sealed class MerchantManagementAuditWriter(MerchantUserDbContext db) : IManagementAuditWriter
{
    public void Append(MerchantUserManagementAudit audit) => db.ManagementAudits.Add(audit);
}

internal sealed class ActiveManagerGuard(MerchantUserDbContext db) : IActiveManagerGuard
{
    public Task<int> CountActiveUsersWithRoleAsync(Guid merchantId, Guid roleId, CancellationToken cancellationToken) =>
        db.Users.CountAsync(user => user.MerchantId == merchantId && user.Status == UserStatus.Active
            && db.RoleAssignments.Any(a => a.UserId == user.Id && a.RoleId == roleId), cancellationToken);
}

internal sealed class MerchantExternalLoginRepository : IExternalLoginRepository
{
    private readonly MerchantUserDbContext _db;
    public MerchantExternalLoginRepository(MerchantUserDbContext db) => _db = db;

    public void Add(ExternalLogin login) => _db.ExternalLogins.Add(login);
}

internal sealed class MerchantRegistrationAuditWriter : IRegistrationAuditWriter
{
    private readonly MerchantUserDbContext _db;
    public MerchantRegistrationAuditWriter(MerchantUserDbContext db) => _db = db;

    public void Append(RegistrationAudit audit) => _db.RegistrationAudits.Add(audit);
}

internal sealed class MerchantRegistrationAttemptWriter : IRegistrationAttemptWriter
{
    private readonly MerchantUserDbContext _db;
    public MerchantRegistrationAttemptWriter(MerchantUserDbContext db) => _db = db;

    public async Task<int> NextAttemptNoAsync(Guid merchantUserId, CancellationToken cancellationToken) =>
        await _db.RegistrationAttempts.AsNoTracking()
            .Where(a => a.UserId == merchantUserId)
            .MaxAsync(a => (int?)a.AttemptNo, cancellationToken).ConfigureAwait(false) + 1 ?? 1;

    public void Add(RegistrationAttempt attempt) => _db.RegistrationAttempts.Add(attempt);
}

/// <summary>Read side of the admin registration-history endpoint (registration-attempt-history REQ-2).
/// AsNoTracking both ways — the handler saves a reveal audit on this same scoped context and must not flush
/// tracked history rows with it. Neither table carries a merchant query filter, so these are plain reads.</summary>
internal sealed class MerchantRegistrationHistoryReader : IRegistrationHistoryReader
{
    private readonly MerchantUserDbContext _db;
    public MerchantRegistrationHistoryReader(MerchantUserDbContext db) => _db = db;

    public async Task<IReadOnlyList<RegistrationAttempt>> ListAttemptsAsync(
        Guid merchantUserId, CancellationToken cancellationToken) =>
        await _db.RegistrationAttempts.AsNoTracking()
            .Where(a => a.UserId == merchantUserId)
            .OrderBy(a => a.AttemptNo)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<RegistrationAudit>> ListAuditsAsync(
        Guid targetUserId, CancellationToken cancellationToken) =>
        await _db.RegistrationAudits.AsNoTracking()
            .Where(a => a.TargetUserId == targetUserId && a.Action != RegistrationAuditAction.Revealed)
            .OrderBy(a => a.OccurredAt)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
}

/// <summary>Mirrors the original <c>UserUnitOfWork</c> exactly (ChangeTracker.Clear() per retry attempt,
/// unique-violation -&gt; <see cref="ConflictException"/>, concurrency -&gt;
/// <see cref="ConcurrencyConflictException"/>) over <see cref="MerchantUserDbContext"/> instead of the keyed
/// pol_admin <c>PolDbContext</c>.</summary>
internal sealed class MerchantUserUnitOfWork : IRegistrationUnitOfWork, IUserUnitOfWork
{
    private readonly MerchantUserDbContext _db;
    private readonly ISecurityTelemetry _telemetry;

    public MerchantUserUnitOfWork(MerchantUserDbContext db, ISecurityTelemetry telemetry)
    {
        _db = db;
        _telemetry = telemetry;
    }

    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            Emit(DenialCategory.ConcurrencyConflict, "A stale/forged concurrency token was rejected at commit.");
            throw new ConcurrencyConflictException(
                "A concurrent change to the same record was detected; the save was rejected.", ex);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2627 or 2601 })
        {
            // SQL Server 2627/2601 = unique-violation: a duplicate subject / (provider,subject) that raced
            // past the create -> a domain conflict (409), never an opaque 500.
            Emit(DenialCategory.CheckOrForeignKeyViolation, "Unique-key violation (SQL 2627/2601) at commit.");
            throw new ConflictException(
                "A registration already exists for this identity.", ex);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 547 })
        {
            Emit(DenialCategory.CheckOrForeignKeyViolation, "CHECK/FK constraint violation (SQL 547) at commit.");
            throw new ConflictException("The record violates a database constraint; the save was rejected.", ex);
        }
    }

    private void Emit(DenialCategory category, string reason) =>
        _telemetry.Emit(new DenialEvent(
            category, "system", ActorId: null, TargetMerchant: null, nameof(MerchantUserDbContext), "Save", reason,
            CorrelationId.Current, DateTime.UtcNow));

    public async Task<T> ExecuteInTransactionAsync<T>(
        Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear(); // each attempt starts clean (the conditional ticket consume rolls back with the tx)
            await using var transaction = await _db.Database.BeginTransactionAsync(
                System.Data.IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
            var result = await operation(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }).ConfigureAwait(false);
    }
}

internal sealed class MerchantRegistrationNoticeWriter : IRegistrationNoticeWriter
{
    private readonly MerchantUserDbContext _db;
    public MerchantRegistrationNoticeWriter(MerchantUserDbContext db) => _db = db;

    public Task<bool> ExistsAsync(Guid merchantUserId, CancellationToken cancellationToken) =>
        _db.RegistrationNotices.AsNoTracking().AnyAsync(n => n.UserId == merchantUserId, cancellationToken);

    public void Add(RegistrationNotice notice) => _db.RegistrationNotices.Add(notice);

    public async Task<bool> TrySaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2627 or 2601 })
        {
            // A concurrent redelivery already recorded the notice — idempotent no-op, not a failure. Detach
            // ONLY the losing notice (not the whole tracker) so any other tracked entity the caller shares
            // this scoped context with is unaffected.
            foreach (var entry in _db.ChangeTracker.Entries<RegistrationNotice>()
                         .Where(e => e.State == EntityState.Added).ToList())
                entry.State = EntityState.Detached;
            return false;
        }
    }
}
