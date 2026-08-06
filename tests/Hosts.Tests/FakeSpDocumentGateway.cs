using Products.Application.Ports;

namespace Hosts.Tests;

/// <summary>
/// The upstream document search, stubbed: it answers with exactly the rows the test hands it, so a test can
/// drive the REAL <c>ListProductsHandler</c> -> <c>SpDocumentItemMapper</c> -> <c>ProductRepository</c> chain
/// (the whole cutover path, products-sp-gateway REQ-7.1/7.2) without the two upstream procedures behind it —
/// those are proven against the live simulated databases in <c>Integration.Tests</c> instead. Naming the port
/// is legitimate here: the insulation guard (<see cref="SpInsulationTests"/>) covers production assemblies,
/// which is exactly where a wire type must not leak.
/// </summary>
internal sealed class FakeSpDocumentGateway(params SpDocumentItem[] items) : ISpDocumentGateway
{
    /// <summary>The §5.1 envelope to answer with; the default is a single full page of the given rows.</summary>
    public SpPaginationMetadata Page { get; set; } =
        new(items.Length, 1, 1, 25, HasNextPage: false, HasPreviousPage: false, "EXACT", 6);

    public Task<SpDocumentSearchResult> SearchAsync(SpDocumentSearchRequest request, CancellationToken ct) =>
        Task.FromResult(new SpDocumentSearchResult(Page, items));

    /// <summary>Finds the one row whose DocumentNo matches the request, exactly as the real adapter's
    /// SelectExactlyOne does (null when none, ambiguous when more than one) — so a test can drive the REAL
    /// LookupDocumentHandler over the fake upstream.</summary>
    public Task<SpDocumentItem?> LookupAsync(SpDocumentLookupRequest request, CancellationToken ct) =>
        Task.FromResult(SpDocumentMatch.SelectExactlyOne(items, request.DocumentNo));
}
