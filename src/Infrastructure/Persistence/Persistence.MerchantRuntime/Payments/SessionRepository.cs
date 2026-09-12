using BuildingBlocks.Application;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Payments.Application.Ports;
using Payments.Domain;
using Payments.Domain.Psp;

namespace Persistence.MerchantRuntime.Payments;

/// <summary>EF Core repository for <see cref="Session"/> over the MerchantRuntime data plane.</summary>
internal sealed class SessionRepository : ISessionRepository
{
    private readonly CommerceDbContext _db;
    private readonly ILogger<SessionRepository> _logger;

    public SessionRepository(CommerceDbContext db, ILogger<SessionRepository> logger)
    {
        _db = db;
        _logger = logger;
    }

    internal SessionRepository(CommerceDbContext db)
        : this(db, NullLogger<SessionRepository>.Instance) { }

    public void Add(Session session) => _db.Set<Session>().Add(session);

    public Task<Session?> GetByIdAsync(Guid paymentSessionId, CancellationToken cancellationToken) =>
        PlatformReadGuard.ReadAsync(ct => _db.Set<Session>()
            .FirstOrDefaultAsync(x => x.Id == paymentSessionId, ct), cancellationToken);

    public async Task<Session?> GetByIdForUpdateAsync(
        Guid paymentSessionId,
        CancellationToken cancellationToken)
    {
        if (!_db.Database.IsSqlServer())
            return await GetByIdAsync(paymentSessionId, cancellationToken).ConfigureAwait(false);

        // Take the row lock with a scalar query first, then reload through the mapped tracked query. The lock
        // remains held by the ambient UoW transaction while the tracked entity is reloaded and mutated.
        var locked = await PlatformReadGuard.ReadAsync(ct => _db.Database
            .SqlQueryRaw<Guid>(
                "SELECT Id AS Value FROM txn.PaymentSessions WITH (UPDLOCK,HOLDLOCK) WHERE Id = @p0 AND MerchantId = @p1",
                new SqlParameter("@p0", paymentSessionId),
                new SqlParameter("@p1", _db.CurrentMerchant))
            .ToListAsync(ct), cancellationToken).ConfigureAwait(false);
        if (locked.Count == 0)
            return null;

        // The unlocked read that produced the prepared evidence may already be tracked on this scoped
        // context. Reload that same instance so callers never retain a stale tracked Session after apply.
        var tracked = _db.Set<Session>().Local.FirstOrDefault(x => x.Id == paymentSessionId);
        if (tracked is not null)
        {
            await _db.Entry(tracked).ReloadAsync(cancellationToken).ConfigureAwait(false);
            return tracked;
        }

        return await PlatformReadGuard.ReadAsync(ct => _db.Set<Session>()
            .FirstOrDefaultAsync(x => x.Id == paymentSessionId, ct), cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<PagedResult<Session>> ListAsync(PagedQuery query, CancellationToken cancellationToken)
    {
        var source = _db.Set<Session>()
            .AsNoTracking()
            .ApplyFilters(query.Filters, _logger);
        var total = await PlatformReadGuard.ReadAsync(
            ct => source.LongCountAsync(ct), cancellationToken).ConfigureAwait(false);
        var skip = (int)Math.Min((long)(query.Page - 1) * query.Limit, int.MaxValue);
        var items = await PlatformReadGuard.ReadAsync(ct => source
                .ApplySort(query.Sort, _logger)
                .Skip(skip)
                .Take(query.Limit)
                .ToListAsync(ct), cancellationToken)
            .ConfigureAwait(false);
        return new PagedResult<Session>(items, query.Page, query.Limit, total);
    }

    public Task<Session?> GetByExternalChargeAsync(
        Code psp,
        string externalChargeId,
        CancellationToken cancellationToken) =>
        PlatformReadGuard.ReadAsync(ct => _db.Set<Session>()
            .FirstOrDefaultAsync(
                x => x.Psp == psp && x.PspExternalChargeId == externalChargeId,
                ct), cancellationToken);

    // `||` rather than an `is ... or` pattern: an expression tree cannot contain pattern matching.
    public Task<Session?> GetOpenForOrderAsync(Guid orderId, CancellationToken cancellationToken) =>
        PlatformReadGuard.ReadAsync(ct => _db.Set<Session>()
            .FirstOrDefaultAsync(
                x => x.OrderId == orderId
                    && (x.Status == SessionStatus.Created || x.Status == SessionStatus.Redirected),
                ct), cancellationToken);
}
