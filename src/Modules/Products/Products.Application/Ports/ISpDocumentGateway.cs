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

    /// <summary>Reads one document from the upstream by its number (products-external-source-of-truth REQ-3):
    /// runs the same search with <c>@SearchText = documentNo</c>, <c>@PaymentStatus = 'ALL'</c> and
    /// <c>@ProductGroup = 'ALL'</c> on the side that <see cref="SpDocumentLookupRequest.ProductGroup"/> selects,
    /// then keeps only the rows whose <c>DocumentNo</c> matches exactly per REQ-2.3 (the <c>@SearchText</c> LIKE
    /// also returns prefix matches). Returns the single match, <c>null</c> when none match (REQ-3.6), and throws
    /// <see cref="SpDocumentAmbiguousException"/> when more than one matches (REQ-3.7).</summary>
    Task<SpDocumentItem?> LookupAsync(SpDocumentLookupRequest request, CancellationToken cancellationToken);
}
