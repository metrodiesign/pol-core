using Mediator;
using Microsoft.Extensions.Logging;
using Products.Application.Ports;
using Products.Domain;

namespace Products.Application;

/// <summary>
/// Reads one insurance document live from the upstream by its number (products-external-source-of-truth REQ-3).
/// Deliberately internal to the system — it is NEVER mapped to an HTTP route (REQ-3.8); add-item and checkout send
/// it, supplying <see cref="SaleCode"/> from the authenticated merchant user (REQ-3.1/4.8), never from the client.
/// <para><see cref="ProductGroup"/> only picks the Motor vs Non-Motor side (REQ-3.2); the document that comes back
/// carries its own authoritative <c>ProductGroup</c> (REQ-3.5).</para>
/// </summary>
public sealed record LookupDocumentQuery(string DocumentNo, ProductGroup ProductGroup, string SaleCode)
    : IQuery<DocumentView?>;

/// <summary>
/// Validates the document number at the boundary, looks the document up through the gateway, and maps the row it
/// gets back to the central <see cref="DocumentView"/>.
/// <list type="bullet">
/// <item>blank / whitespace-only or longer than 150 characters -> <see cref="ArgumentException"/> (400, REQ-2.5)</item>
/// <item>longer than 100 characters -> <see cref="ArgumentException"/> (400, REQ-3.1): <c>@SearchText</c> is
/// <c>nvarchar(100)</c> and <c>SqlParameter.Size</c> would truncate a longer value silently, so a document of
/// 101-150 characters can be listed but not looked up — the alternative is charging for the wrong document.</item>
/// <item>no matching row -> <c>null</c> (REQ-3.6): the caller turns that into a 400 on add-item or a 409 on
/// checkout</item>
/// <item>more than one matching row -> <see cref="SpDocumentAmbiguousException"/> from the gateway (400, REQ-3.7)</item>
/// </list>
/// The number is trimmed before it travels (REQ-2.6), so surrounding whitespace still finds the document.
/// </summary>
public sealed class LookupDocumentHandler(ISpDocumentGateway gateway, ILogger<LookupDocumentHandler> logger)
    : IQueryHandler<LookupDocumentQuery, DocumentView?>
{
    /// <summary><c>@SearchText</c> is declared <c>nvarchar(100)</c>; a longer value binds truncated.</summary>
    private const int SearchTextMaxLength = 100;

    /// <summary>The contract width of a document number on the wire and in every column (REQ-2.1).</summary>
    private const int DocumentNoMaxLength = 150;

    public async ValueTask<DocumentView?> Handle(LookupDocumentQuery query, CancellationToken ct)
    {
        var documentNo = query.DocumentNo?.Trim();
        if (string.IsNullOrEmpty(documentNo))
            throw new ArgumentException("documentNo is required.");
        if (documentNo.Length > DocumentNoMaxLength)
            throw new ArgumentException($"documentNo must be at most {DocumentNoMaxLength} characters.");
        if (documentNo.Length > SearchTextMaxLength)
            throw new ArgumentException(
                $"documentNo must be at most {SearchTextMaxLength} characters to be looked up (@SearchText limit).");

        var row = await gateway.LookupAsync(
            new SpDocumentLookupRequest(documentNo, query.ProductGroup, query.SaleCode), ct);
        if (row is null)
            return null;

        var view = SpDocumentItemMapper.ToView(row);
        if (view is null)
            // The upstream returned the document but the row is too incomplete to price safely (blank key/sale
            // code, non-positive premium, or a value outside the wire enums). Treat it as not found rather than
            // guessing, and leave a trace since this is a money path.
            logger.LogWarning(
                "Products: upstream document {DocumentNo} matched but could not be mapped to a usable view.",
                documentNo);

        return view;
    }
}
