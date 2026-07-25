using Orders.Domain.Items;

namespace Orders.Application;

/// <summary>
/// Persistence port for <see cref="ItemPolicy"/>/<see cref="ItemPolicyAudit"/>, scoped to the request's bound
/// merchant. Unlike <see cref="IRevealAuditWriter"/> (a standalone port for a handler that has no repository
/// of its own), this one bundles the audit write alongside the policy load/add — design.md's write sequence
/// treats "add/update policy + add audit row" as one repository responsibility, since both entities are
/// written together on every call. Saving is the caller's job via <c>IUnitOfWork</c>.
/// </summary>
public interface IItemPolicyRepository
{
    /// <summary>True when <paramref name="orderItemId"/> names an <c>OrderItem</c> under the bound merchant —
    /// query-filtered, so another merchant's item reads as not-existing (REQ-3.3, no existence leak).</summary>
    Task<bool> ItemExistsAsync(Guid orderItemId, CancellationToken cancellationToken);

    /// <summary>The item's existing policy record, or null if none has been entered yet (REQ-1.7).</summary>
    Task<ItemPolicy?> GetPolicyByItemAsync(Guid orderItemId, CancellationToken cancellationToken);

    /// <summary>Tracks a newly-created policy for insertion. Not needed for an existing policy — it is
    /// already tracked by the context that loaded it via <see cref="GetPolicyByItemAsync"/>.</summary>
    void Add(ItemPolicy policy);

    /// <summary>Tracks a new audit row for insertion.</summary>
    void AddAudit(ItemPolicyAudit audit);
}
