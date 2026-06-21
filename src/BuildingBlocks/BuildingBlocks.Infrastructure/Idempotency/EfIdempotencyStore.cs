using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BuildingBlocks.Infrastructure.Idempotency;

/// <summary>
/// Claims idempotency keys by inserting one row per key; the primary-key unique constraint is the
/// real guard against replays. Intended to be called INSIDE the handler's transaction so the claim
/// commits atomically with the state transition it protects (PLAN decision #9). On a duplicate the
/// pending inserts are detached so the caller can roll back its transaction and ack the replay.
/// </summary>
public sealed class EfIdempotencyStore : IIdempotencyStore
{
    private readonly ProducerDbContext _db;
    private readonly IClock _clock;

    public EfIdempotencyStore(ProducerDbContext db, IClock clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task<bool> TryBeginAsync(IReadOnlyCollection<string> keys, string context, CancellationToken cancellationToken)
    {
        var distinct = keys.Where(k => !string.IsNullOrWhiteSpace(k)).Distinct().ToList();
        if (distinct.Count == 0)
            return true;

        foreach (var key in distinct)
            _db.IdempotencyRecords.Add(new IdempotencyRecord(key, context, _clock.UtcNow));

        try
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateException)
        {
            // Any key already present => this is a replay. Drop the pending inserts so the caller's
            // DbContext is clean to roll back.
            foreach (var entry in _db.ChangeTracker.Entries<IdempotencyRecord>().ToList())
                entry.State = EntityState.Detached;

            return false;
        }
    }
}
