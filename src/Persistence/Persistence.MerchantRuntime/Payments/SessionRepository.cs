using BuildingBlocks.Application;
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
    private readonly MerchantRuntimeDbContext _db;
    private readonly ILogger<SessionRepository> _logger;

    public SessionRepository(MerchantRuntimeDbContext db, ILogger<SessionRepository> logger)
    {
        _db = db;
        _logger = logger;
    }

    internal SessionRepository(MerchantRuntimeDbContext db)
        : this(db, NullLogger<SessionRepository>.Instance) { }

    public void Add(Session session) => _db.Set<Session>().Add(session);

    public Task<Session?> GetByIdAsync(Guid paymentSessionId, CancellationToken cancellationToken) =>
        PlatformReadGuard.ReadAsync(ct => _db.Set<Session>()
            .FirstOrDefaultAsync(x => x.Id == paymentSessionId, ct), cancellationToken);

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
