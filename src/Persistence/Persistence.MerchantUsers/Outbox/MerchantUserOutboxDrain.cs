using BuildingBlocks.Infrastructure.Outbox;
using Microsoft.EntityFrameworkCore;

namespace Persistence.MerchantUsers.Outbox;

/// <summary>
/// rls-to-query-filter task 5 (design.md "suppressed-op read ports" — <c>IOutboxDrain</c>): the ONE
/// sanctioned way to see <c>merch.UserOutbox</c> rows across every owner, for the dispatcher's lease-claim
/// scan. <c>merch.UserOutbox</c> carries the same per-merchant read filter as every other
/// <c>MerchantUserDbContext</c> entity (REQ-1.6) — a lease scan is inherently cross-merchant (the dispatcher
/// has no merchant of its own), so this is a sanctioned <c>IgnoreQueryFilters()</c> narrow port (design.md
/// §"Escape-hatch allowlist" — the Architecture.Tests bypass-primitive allowlist names this file).
/// <para>
/// Portable EF LINQ, not the raw <c>UPDATE ... OUTPUT WITH (READPAST, UPDLOCK, ROWLOCK)</c> the existing
/// <c>OutboxDispatcher</c> uses for <c>txn.OutboxMessages</c> — this table does not exist in the real
/// database yet (task 8's migration creates it), so there is no live throughput/contention to optimize for.
/// Hardening this to the same raw-SQL lease pattern is task 8's job once a real dispatcher instance is
/// wired onto it.
/// </para>
/// </summary>
internal interface IMerchantUserOutboxDrain
{
    Task<IReadOnlyList<MerchantUserOutbox>> LeaseNextBatchAsync(
        int batchSize, string owner, DateTime now, TimeSpan leaseDuration, int maxAttempts, CancellationToken cancellationToken);

    /// <summary>
    /// Re-fetches ONE row this dispatcher already leased, for the per-message publish/mark step. Same
    /// sanctioned cross-owner bypass as the lease scan: the dispatcher is unbound, so an ordinary
    /// <c>UserOutbox</c> lookup would be filtered to nothing and the message would never publish.
    /// Scoped to the caller's lease (<paramref name="owner"/>) so it cannot read rows it does not hold.
    /// </summary>
    Task<MerchantUserOutbox?> FindLeasedAsync(Guid id, string owner, CancellationToken cancellationToken);
}

internal sealed class MerchantUserOutboxDrain(MerchantUserDbContext db) : IMerchantUserOutboxDrain
{
    public async Task<IReadOnlyList<MerchantUserOutbox>> LeaseNextBatchAsync(
        int batchSize, string owner, DateTime now, TimeSpan leaseDuration, int maxAttempts, CancellationToken cancellationToken)
    {
        var candidates = await db.UserOutbox.IgnoreQueryFilters()
            .Where(m => m.ProcessedAt == null && (m.LeaseExpiresAt == null || m.LeaseExpiresAt < now))
            .OrderBy(m => m.OccurredAt)
            .Take(batchSize)
            .ToListAsync(cancellationToken);

        var leased = candidates.Where(m => m.Attempts < maxAttempts).ToList();
        foreach (var message in leased)
            message.Lease(owner, now.Add(leaseDuration));

        if (leased.Count > 0)
            await db.SaveChangesAsync(cancellationToken);

        return leased;
    }

    public Task<MerchantUserOutbox?> FindLeasedAsync(Guid id, string owner, CancellationToken cancellationToken) =>
        db.UserOutbox.IgnoreQueryFilters()
            .FirstOrDefaultAsync(m => m.Id == id && m.LeaseOwner == owner, cancellationToken);
}
