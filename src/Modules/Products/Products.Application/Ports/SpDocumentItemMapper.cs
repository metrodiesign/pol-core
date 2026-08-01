using Products.Domain;

namespace Products.Application.Ports;

/// <summary>One §5.2 row after mapping, in the order the procedure returned it. <see cref="Input"/> is
/// null when the row cannot become a document, and <see cref="SkipReason"/> then says why so the caller
/// can log it (REQ-7.7) — the row still occupies its place in the list, because the caller answers on the
/// rows it kept while the totals stay the ones the procedure reported.</summary>
public sealed record MappedSpDocument(SpDocumentItem Item, ProductInput? Input, string? SkipReason);

/// <summary>Turns §5.2 rows into <see cref="ProductInput"/>s for the upsert. Nothing here repairs a row:
/// a blank key, a non-positive premium or a value outside the wire enums drops the row rather than
/// guessing (REQ-7.7). <c>ProductGroup</c> comes from <c>SourceSystem</c> and the row's own
/// <c>InsuranceType</c> is ignored — this repo always derives the side from the group (REQ-7.10).</summary>
public static class SpDocumentItemMapper
{
    public static IReadOnlyList<MappedSpDocument> Map(IReadOnlyList<SpDocumentItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        // First row wins per DocumentNo: two rows for one document would be two Adds against
        // IX_Products_DocumentNo in a single save. That index is unique under a case-insensitive
        // collation, so the claim is case-insensitive too. Only usable rows claim the key, so a broken
        // row does not shadow a good one further down the page.
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var mapped = new List<MappedSpDocument>(items.Count);

        foreach (var item in items)
            mapped.Add(MapOne(item, claimed));

        return mapped;
    }

    private static MappedSpDocument MapOne(SpDocumentItem item, HashSet<string> claimed)
    {
        var documentNo = Trimmed(item.DocumentNo);
        if (documentNo is null)
            return new MappedSpDocument(item, null, "DocumentNo is blank");

        var saleCode = Trimmed(item.SaleCode);
        if (saleCode is null)
            return new MappedSpDocument(item, null, "SaleCode is blank");

        if (item.TotalPremium is not > 0)
            return new MappedSpDocument(item, null, "TotalPremium is null or not greater than zero");

        if (ParseProductGroup(item.SourceSystem) is not { } productGroup)
            return new MappedSpDocument(item, null, $"unknown SourceSystem '{item.SourceSystem}'");

        if (ParseDocumentType(item.DocumentType) is not { } documentType)
            return new MappedSpDocument(item, null, $"unknown DocumentType '{item.DocumentType}'");

        if (ParsePaymentStatus(item.PaymentStatus) is not { } paymentStatus)
            return new MappedSpDocument(item, null, $"unknown PaymentStatus '{item.PaymentStatus}'");

        if (!claimed.Add(documentNo))
            return new MappedSpDocument(item, null, "duplicate DocumentNo within the page");

        return new MappedSpDocument(
            item,
            new ProductInput(
                productGroup,
                documentType,
                documentNo,
                saleCode,
                item.TotalPremium.Value,
                paymentStatus,
                item.PaidDate,
                PolicyYear: item.PolicyYear,
                ReferenceBranch: item.ReferenceBranch,
                ReferencePre: item.ReferencePre,
                PolicySequenceNo: item.PolicySequenceNo,
                ReferenceYear: item.ReferenceYear,
                ReferenceNo: item.ReferenceNo,
                PolicyBranch: item.PolicyBranch,
                PolicyType: item.PolicyType,
                SaleFullName: item.SaleFullName,
                BrokerCode: item.BrokerCode,
                BrokerName: item.BrokerName,
                PolicyNumber: item.PolicyNumber,
                ApplicationNumber: item.ApplicationNumber,
                PreviousPolicyNumber: item.PreviousPolicyNumber,
                EndorsementNumber: item.EndorsementNumber,
                StartDate: item.StartDate,
                EndDate: item.EndDate,
                ShowName: item.ShowName,
                LicensePlateNumber: item.LicensePlateNumber,
                NetPremium: item.NetPremium,
                Stamp: item.Stamp,
                TaxVat: item.TaxVat,
                CommissionAmount: item.CommissionAmount,
                CommissionPercent: item.CommissionPercent),
            null);
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
