namespace Products.Application.Ports;

/// <summary>
/// Selects the one upstream row a single-document lookup meant, out of the rows the <c>@SearchText</c> LIKE
/// returned (products-external-source-of-truth REQ-3.4/3.6/3.7). The LIKE matches many columns and returns
/// prefix hits too, so this keeps only the rows whose <c>DocumentNo</c> equals the wanted one per REQ-2.3 —
/// trimmed on both sides, case-insensitive — then answers: none -> <c>null</c>, one -> that row, more than one ->
/// <see cref="SpDocumentAmbiguousException"/>.
/// <para>Pulled out of the adapter as a pure function so REQ-2.3/3.7 can be unit-tested without SQL. The
/// comparison is ordinal-case-insensitive: a fast-path that must be at least as strict as the upstream's own
/// <c>Thai_100_CI_AS</c> collation, which is what actually decides equality on the SQL side of this repo.</para>
/// </summary>
public static class SpDocumentMatch
{
    public static SpDocumentItem? SelectExactlyOne(IReadOnlyList<SpDocumentItem> rows, string documentNo)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(documentNo);

        var wanted = documentNo.Trim();

        SpDocumentItem? match = null;
        var count = 0;
        foreach (var row in rows)
        {
            if (row.DocumentNo is null)
                continue;
            if (!string.Equals(row.DocumentNo.Trim(), wanted, StringComparison.OrdinalIgnoreCase))
                continue;

            count++;
            match ??= row;
        }

        if (count == 0)
            return null;
        if (count > 1)
            throw new SpDocumentAmbiguousException(wanted, count);
        return match;
    }
}
