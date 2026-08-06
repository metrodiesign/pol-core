using Products.Domain;

namespace Products.Application.Ports;

/// <summary>One §5.2 row after mapping, in the order the procedure returned it. <see cref="View"/> is
/// null when the row cannot become a document, and <see cref="SkipReason"/> then says why so the caller
/// can log it (REQ-1.6) — the row still occupies its place in the list, because the caller answers on the
/// rows it kept while the totals stay the ones the procedure reported.</summary>
public sealed record MappedSpDocument(SpDocumentItem Item, DocumentView? View, string? SkipReason);

/// <summary>Turns §5.2 rows into <see cref="DocumentView"/>s — the one shape the whole buy path reads a
/// document through (products-external-source-of-truth). Nothing here repairs a row: a blank key, a blank
/// sale code, a non-positive premium or a value outside the wire enums drops the row rather than guessing
/// (REQ-1.6). <c>ProductGroup</c> comes from <c>SourceSystem</c> and the row's own <c>InsuranceType</c> is
/// ignored — this repo always derives the side from the group.</summary>
public static class SpDocumentItemMapper
{
    public static IReadOnlyList<MappedSpDocument> Map(IReadOnlyList<SpDocumentItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        // First row wins per DocumentNo (REQ-1.7): DocumentNo is now the identifier the cart and the order
        // carry, so two rows claiming one number would be two different prices for one document. The claim is
        // case-insensitive to match REQ-2.3. Only usable rows claim the key, so a broken row does not shadow
        // a good one further down the page.
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var mapped = new List<MappedSpDocument>(items.Count);

        foreach (var item in items)
            mapped.Add(MapOne(item, claimed));

        return mapped;
    }

    private static MappedSpDocument MapOne(SpDocumentItem item, HashSet<string> claimed)
    {
        if (Trimmed(item.DocumentNo) is null)
            return new MappedSpDocument(item, null, "DocumentNo is blank");

        if (Trimmed(item.SaleCode) is null)
            return new MappedSpDocument(item, null, "SaleCode is blank");

        if (item.TotalPremium is not > 0)
            return new MappedSpDocument(item, null, "TotalPremium is null or not greater than zero");

        if (ParseProductGroup(item.SourceSystem) is null)
            return new MappedSpDocument(item, null, $"unknown SourceSystem '{item.SourceSystem}'");

        if (ParseDocumentType(item.DocumentType) is null)
            return new MappedSpDocument(item, null, $"unknown DocumentType '{item.DocumentType}'");

        if (ParsePaymentStatus(item.PaymentStatus) is null)
            return new MappedSpDocument(item, null, $"unknown PaymentStatus '{item.PaymentStatus}'");

        var view = ToView(item)!;

        if (!claimed.Add(view.DocumentNo))
            return new MappedSpDocument(item, null, "duplicate DocumentNo within the page");

        return new MappedSpDocument(item, view, null);
    }

    /// <summary>Maps a single §5.2 row read by a lookup (REQ-3) into the central <see cref="DocumentView"/>,
    /// applying the same rules as <see cref="Map"/>: a blank key, a blank sale code, a non-positive premium or a
    /// value outside the wire enums makes the row unusable and returns <c>null</c> (fail-closed — the caller then
    /// treats it as "not found"). Every field on the view is the value the upstream returned, including
    /// <see cref="DocumentView.ProductGroup"/> derived from <c>SourceSystem</c> (REQ-3.5); the row's own
    /// <c>InsuranceType</c> is ignored exactly as the list path ignores it.</summary>
    public static DocumentView? ToView(SpDocumentItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        var documentNo = Trimmed(item.DocumentNo);
        if (documentNo is null)
            return null;

        var saleCode = Trimmed(item.SaleCode);
        if (saleCode is null)
            return null;

        if (item.TotalPremium is not > 0)
            return null;

        if (ParseProductGroup(item.SourceSystem) is not { } productGroup)
            return null;

        if (ParseDocumentType(item.DocumentType) is not { } documentType)
            return null;

        if (ParsePaymentStatus(item.PaymentStatus) is not { } paymentStatus)
            return null;

        return new DocumentView(
            productGroup,
            documentType,
            documentNo,
            item.PolicyYear,
            item.ReferenceBranch,
            item.ReferencePre,
            item.PolicySequenceNo,
            item.ReferenceYear,
            item.ReferenceNo,
            item.PolicyBranch,
            item.PolicyType,
            saleCode,
            item.SaleFullName,
            item.BrokerCode,
            item.BrokerName,
            item.PolicyNumber,
            item.ApplicationNumber,
            item.PreviousPolicyNumber,
            item.EndorsementNumber,
            item.StartDate,
            item.EndDate,
            item.ShowName,
            item.NetPremium,
            item.Stamp,
            item.TaxVat,
            item.TotalPremium.Value,
            item.CommissionPercent,
            item.CommissionAmount,
            item.PaidDate,
            item.LicensePlateNumber,
            paymentStatus);
    }

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // Spelled out rather than Enum.TryParse: the wire values are case-sensitive (§2), and TryParse would
    // also accept "0" and "CMI, VMI" as members, turning upstream garbage into a plausible document.
    private static ProductGroup? ParseProductGroup(string? value) => value switch
    {
        "CMI" => ProductGroup.CMI,
        "VMI" => ProductGroup.VMI,
        "FIRE" => ProductGroup.FIRE,
        "MISC" => ProductGroup.MISC,
        _ => null,
    };

    private static DocumentType? ParseDocumentType(string? value) => value switch
    {
        "APPLICATION" => DocumentType.APPLICATION,
        "POLICY" => DocumentType.POLICY,
        "RENEWAL" => DocumentType.RENEWAL,
        "ENDORSEMENT" => DocumentType.ENDORSEMENT,
        _ => null,
    };

    private static PaymentStatus? ParsePaymentStatus(string? value) => value switch
    {
        "UNPAID" => PaymentStatus.UNPAID,
        "PAID" => PaymentStatus.PAID,
        _ => null,
    };
}
