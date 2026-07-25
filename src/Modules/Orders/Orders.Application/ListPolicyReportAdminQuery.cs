using BuildingBlocks.Application;
using Mediator;

namespace Orders.Application;

/// <summary>
/// Admin-plane cross-merchant read for the sold-items policy report (REQ-4.1/4.2/4.4) — the escape-hatch
/// twin of <see cref="ListPolicyReportQuery"/>. Deliberately does NOT implement <see cref="IMerchantScoped"/>
/// (an admin request has no bound merchant). Carries the caller's accessible-merchant decision as plain data
/// (mirrors <see cref="UpsertItemPolicyAdminCommand"/>, T5's own documented reason — no module's Application
/// project references another module's <c>Admins.Application</c>). <see cref="MerchantId"/> is the optional
/// <c>?merchantId=</c> narrowing filter — NOT part of the SFS whitelist (mirrors <c>ProductSfs.cs</c>'s own
/// exclusion of <c>merchantId</c> from the filter surface).
/// </summary>
public sealed record ListPolicyReportAdminQuery : PagedQuery, IQuery<PagedResult<PolicyReportItem>>
{
    public required bool IsUnrestrictedAdmin { get; init; }
    public required IReadOnlySet<Guid> AccessibleMerchantIds { get; init; }
    public Guid? MerchantId { get; init; }
}

/// <summary>
/// Admin cross-merchant read escape-hatch for the policy report (policy-reference-record REQ-4.2/4.4) —
/// <c>IgnoreQueryFilters()</c> over the SAME join <see cref="IPolicyReportRepository"/> uses, confined by the
/// caller's accessible-merchant set (or the optional <c>?merchantId=</c> filter). Allowlisted in
/// <c>Architecture.Tests.BypassPrimitiveTests</c>. Unlike <see cref="IAdminItemPolicyWriter"/> (T5), a read
/// never calls <c>SaveChanges</c>, so it needs no separate write-authorizer context — the AMBIENT
/// <c>MerchantRuntimeDbContext</c> is enough (mirrors <c>ConnectionRepository.ListByTenantAsync</c>).
/// </summary>
public interface IAdminItemPolicyReader
{
    Task<PagedResult<PolicyReportItem>> ListAsync(ListPolicyReportAdminQuery query, CancellationToken cancellationToken);
}

public sealed class ListPolicyReportAdminHandler(IAdminItemPolicyReader reader)
    : IQueryHandler<ListPolicyReportAdminQuery, PagedResult<PolicyReportItem>>
{
    public async ValueTask<PagedResult<PolicyReportItem>> Handle(
        ListPolicyReportAdminQuery query, CancellationToken cancellationToken) =>
        await reader.ListAsync(query, cancellationToken);
}
