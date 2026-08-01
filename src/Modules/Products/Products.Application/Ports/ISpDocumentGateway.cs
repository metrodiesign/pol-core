namespace Products.Application.Ports;

/// <summary>
/// The one seam to the upstream document search of the VCentralPay platform — the pair of stored
/// procedures described in §1-§6 of <c>docs/reference/vcentralpay-sp-quick-reference.pdf</c>, today
/// answered by the simulated <c>hippodb</c>/<c>mammothdb</c> catalogues and one day by the real
/// <c>motordb</c>/<c>centerdb</c> servers behind the same contract.
/// <para>Which procedure runs is decided by <see cref="SpDocumentSearchRequest.Target"/> alone, and
/// <c>@BranchCode</c> is supplied by the adapter from its own options (§1.1: both are server-side
/// concerns). A caller therefore cannot pick a connection, a procedure, or a branch.</para>
/// <para>Failures are typed so the HTTP layer needs no knowledge of SQL: a documented §6 rejection
/// (50001-50009) surfaces as <see cref="SpDocumentSearchRejectedException"/> -> 400, and every other
/// upstream fault as <c>UpstreamUnavailableException</c> -> 503, with detail logged server-side only.</para>
/// </summary>
public interface ISpDocumentGateway
{
    /// <summary>Runs one page of the §2 search and returns the §5.1 metadata plus the §5.2 rows,
    /// exactly as the procedure ordered them.</summary>
    Task<SpDocumentSearchResult> SearchAsync(SpDocumentSearchRequest request, CancellationToken cancellationToken);
}
