namespace Products.Application.Ports;

/// <summary>
/// A single-document lookup matched more than one row from the upstream (products-external-source-of-truth
/// REQ-3.7): more than one row shares the requested <see cref="DocumentNo"/> (comparing per REQ-2.3). The lookup
/// filters by one <c>@SaleCode</c> and each upstream instance has its own uniqueness on the number, so this is a
/// defensive guard rather than an expected shape — if it ever does happen, the caller cannot know which document
/// it would be pricing, so the request is rejected rather than guessed.
/// <para>Derives from <see cref="ArgumentException"/> so the shared ProblemDetails handler maps it to 400 through
/// the bucket it already has. That handler emits a FIXED detail, so <see cref="Exception.Message"/> — which names
/// the document — never reaches the client; it exists for the server-side error log the adapter writes and for
/// tests.</para>
/// </summary>
public sealed class SpDocumentAmbiguousException : ArgumentException
{
    public SpDocumentAmbiguousException(string documentNo, int matchCount)
        : base($"Document lookup for '{documentNo}' matched {matchCount} rows; refusing to pick one.")
    {
        DocumentNo = documentNo;
        MatchCount = matchCount;
    }

    /// <summary>The document number that matched more than once.</summary>
    public string DocumentNo { get; }

    /// <summary>How many upstream rows matched it exactly (always > 1).</summary>
    public int MatchCount { get; }
}
