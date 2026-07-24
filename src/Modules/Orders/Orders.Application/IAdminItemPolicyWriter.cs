using Orders.Domain.Items;

namespace Orders.Application;

/// <summary>
/// Admin cross-merchant write escape-hatch for <see cref="ItemPolicy"/> (policy-reference-record
/// REQ-3.2-admin/3.3-admin) — the ONLY port an admin handler may use to load an item's REAL owning merchant
/// and read/write its <see cref="ItemPolicy"/> across the ordinary merchant read floor
/// (<c>IgnoreQueryFilters()</c>). Allowlisted in <c>Architecture.Tests.BypassPrimitiveTests</c>. Bound to a
/// SEPARATE <c>MerchantRuntimeDbContext</c> instance built with its own admin write authorizer (mirror
/// <c>Persistence.Provisioning</c>'s <c>AddProvisioning</c>) — never the ambient merchant-scoped context
/// <see cref="IItemPolicyRepository"/> uses, which an admin request's write floor denies unconditionally.
/// </summary>
public interface IAdminItemPolicyWriter
{
    Task<AdminItemPolicyLoad> LoadAsync(Guid orderItemId, CancellationToken cancellationToken);

    void Add(ItemPolicy policy);

    void AddAudit(ItemPolicyAudit audit);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}

/// <summary>The item's existence + its REAL owning merchant (loaded cross-merchant) + its current
/// <see cref="ItemPolicy"/> row, if any. When <see cref="ItemExists"/> is false, <see cref="MerchantId"/>/
/// <see cref="Policy"/> carry no meaning.</summary>
public sealed record AdminItemPolicyLoad(bool ItemExists, Guid MerchantId, ItemPolicy? Policy)
{
    public static readonly AdminItemPolicyLoad NotFound = new(false, Guid.Empty, null);
}
