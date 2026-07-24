using BuildingBlocks.Application;
using Mediator;

namespace Orders.Application;

/// <summary>
/// Merchant-plane read for the sold-items policy report (REQ-4.1/4.2/4.4) — auto-scoped to the bound
/// merchant via <see cref="IMerchantScoped"/> + the query filter floor, mirrors
/// <see cref="Products.Application.ProductListItem"/>'s own <c>ListProductsQuery</c> (design.md Technology
/// Decision #3 — reuse SFS, no new paging engine).
/// </summary>
public sealed record ListPolicyReportQuery : PagedQuery, IQuery<PagedResult<PolicyReportItem>>, IMerchantScoped
{
    public required Guid MerchantId { get; init; }
}

public interface IPolicyReportRepository
{
    Task<PagedResult<PolicyReportItem>> ListAsync(ListPolicyReportQuery query, CancellationToken cancellationToken);
}

public sealed class ListPolicyReportHandler(IPolicyReportRepository reports)
    : IQueryHandler<ListPolicyReportQuery, PagedResult<PolicyReportItem>>
{
    public async ValueTask<PagedResult<PolicyReportItem>> Handle(
        ListPolicyReportQuery query, CancellationToken cancellationToken) =>
        await reports.ListAsync(query, cancellationToken);
}
