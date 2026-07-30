using SharedKernel;

namespace Products.Domain;

/// <summary>
/// A sellable insurance document in the central catalogue
/// (<c>docs/reference/vcentralpay-sp-quick-reference.pdf</c> §2/§5.2) — one row per
/// APPLICATION / POLICY / RENEWAL / ENDORSEMENT awaiting payment. <see cref="TotalPremium"/> is the
/// selling price; it and the optional premium breakdown are plain <c>decimal(19,2)</c> columns, not
/// <c>Money</c> — §5.2 carries no currency column (the source system is THB-only). Currency is minted
/// once at the cart boundary, so the scale and sign checks <c>Money.Of</c> used to provide live in
/// <see cref="Create"/> instead.
/// </summary>
public sealed class Product : AggregateRoot<Guid>
{
    public ProductGroup ProductGroup { get; private set; }

    public DocumentType DocumentType { get; private set; }

    /// <summary>Composite display document number, e.g. <c>00098-69100/รบ/042145-10</c> — a single
    /// opaque string, never parsed into parts.</summary>
    public string DocumentNo { get; private set; } = default!;

    /// <summary>Two-digit Buddhist-era year as text (e.g. "69") — kept as a string, never parsed.</summary>
    public string? PolicyYear { get; private set; }

    public string? ReferenceBranch { get; private set; }
    public string? ReferencePre { get; private set; }
    public string? PolicySequenceNo { get; private set; }
    public string? ReferenceYear { get; private set; }
    public string? ReferenceNo { get; private set; }

    public string SaleCode { get; private set; } = default!;
    public string? SaleFullName { get; private set; }
    public string? BrokerCode { get; private set; }
    public string? BrokerName { get; private set; }
    public string? PolicyBranch { get; private set; }
    public string? PolicyType { get; private set; }

    public string? PolicyNumber { get; private set; }
    public string? ApplicationNumber { get; private set; }
    public string? PreviousPolicyNumber { get; private set; }
    public string? EndorsementNumber { get; private set; }

    /// <summary>Coverage start — datetime2(0), no timezone in the source contract (naive).</summary>
    public DateTime? StartDate { get; private set; }

    /// <summary>Coverage end — datetime2(0), no timezone in the source contract (naive).</summary>
    public DateTime? EndDate { get; private set; }

    /// <summary>Insured display name (<c>docs/reference/vcentralpay-sp-quick-reference.pdf</c> §5.2
    /// <c>ShowName</c>; searched via <c>@InsuredName</c>).</summary>
    public string? ShowName { get; private set; }

    /// <summary>Motor documents only; always null for Non-Motor.</summary>
    public string? LicensePlateNumber { get; private set; }

    /// <summary>Total premium (เบี้ยรวม) — the selling price of this document, decimal(19,2) THB.</summary>
    public decimal TotalPremium { get; private set; }

    public decimal? NetPremium { get; private set; }
    public decimal? Stamp { get; private set; }
    public decimal? TaxVat { get; private set; }
    public decimal? CommissionAmount { get; private set; }

    /// <summary>Commission rate — decimal(19,6) in the source contract, not a Money.</summary>
    public decimal? CommissionPercent { get; private set; }

    public PaymentStatus PaymentStatus { get; private set; }

    /// <summary>Null while <see cref="PaymentStatus"/> is <see cref="PaymentStatus.UNPAID"/>.</summary>
    public DateTime? PaidDate { get; private set; }

    /// <summary>Motor vs Non-Motor, derived from <see cref="ProductGroup"/> — never stored.</summary>
    public InsuranceType InsuranceType =>
        ProductGroup is ProductGroup.CMI or ProductGroup.VMI ? InsuranceType.Motor : InsuranceType.NonMotor;

    /// <summary>Parameterless ctor for EF Core materialisation only.</summary>
    private Product() { }

    /// <summary>Creates a new unpaid insurance document in the catalogue.</summary>
    public static Product Create(ProductInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var saleCode = Required(input.SaleCode, 20, nameof(input.SaleCode));
        var documentNo = Required(input.DocumentNo, 150, nameof(input.DocumentNo));

        if (input.TotalPremium <= 0)
            throw new ArgumentException("TotalPremium must be greater than zero.", nameof(input));

        if (input.StartDate is { } start && input.EndDate is { } end && start > end)
            throw new ArgumentException("StartDate must not be after EndDate.", nameof(input));

        // Defence-in-depth for non-HTTP callers: the enum-to-string EF conversion would otherwise persist
        // an undefined value (e.g. (ProductGroup)99 -> "99"), outside the uppercase wire contract.
        if (!Enum.IsDefined(input.ProductGroup))
            throw new ArgumentException("Unknown ProductGroup.", nameof(input));
        if (!Enum.IsDefined(input.DocumentType))
            throw new ArgumentException("Unknown DocumentType.", nameof(input));

        if (input.ProductGroup == ProductGroup.CMI && input.DocumentType == DocumentType.APPLICATION)
            throw new ArgumentException("Motor/CMI does not support APPLICATION documents.", nameof(input));

        return new Product
        {
            Id = Guid.NewGuid(),
            ProductGroup = input.ProductGroup,
            DocumentType = input.DocumentType,
            DocumentNo = documentNo,
            PolicyYear = Optional(input.PolicyYear, 2, nameof(input.PolicyYear)),
            ReferenceBranch = Optional(input.ReferenceBranch, 3, nameof(input.ReferenceBranch)),
            ReferencePre = Optional(input.ReferencePre, 20, nameof(input.ReferencePre)),
            PolicySequenceNo = Optional(input.PolicySequenceNo, 30, nameof(input.PolicySequenceNo)),
            ReferenceYear = Optional(input.ReferenceYear, 2, nameof(input.ReferenceYear)),
            ReferenceNo = Optional(input.ReferenceNo, 30, nameof(input.ReferenceNo)),
            SaleCode = saleCode,
            SaleFullName = Optional(input.SaleFullName, 500, nameof(input.SaleFullName)),
            BrokerCode = Optional(input.BrokerCode, 20, nameof(input.BrokerCode)),
            BrokerName = Optional(input.BrokerName, 500, nameof(input.BrokerName)),
            PolicyBranch = Optional(input.PolicyBranch, 250, nameof(input.PolicyBranch)),
            PolicyType = Optional(input.PolicyType, 250, nameof(input.PolicyType)),
            PolicyNumber = Optional(input.PolicyNumber, 150, nameof(input.PolicyNumber)),
            ApplicationNumber = Optional(input.ApplicationNumber, 150, nameof(input.ApplicationNumber)),
            PreviousPolicyNumber = Optional(input.PreviousPolicyNumber, 150, nameof(input.PreviousPolicyNumber)),
            EndorsementNumber = Optional(input.EndorsementNumber, 150, nameof(input.EndorsementNumber)),
            StartDate = input.StartDate,
            EndDate = input.EndDate,
            ShowName = Optional(input.ShowName, 500, nameof(input.ShowName)),
            LicensePlateNumber = Optional(input.LicensePlateNumber, 100, nameof(input.LicensePlateNumber)),
            TotalPremium = RequireMoney(input.TotalPremium, nameof(input.TotalPremium)),
            NetPremium = RequireMoney(input.NetPremium, nameof(input.NetPremium)),
            Stamp = RequireMoney(input.Stamp, nameof(input.Stamp)),
            TaxVat = RequireMoney(input.TaxVat, nameof(input.TaxVat)),
            CommissionAmount = RequireMoney(input.CommissionAmount, nameof(input.CommissionAmount)),
            CommissionPercent = input.CommissionPercent,
            PaymentStatus = PaymentStatus.UNPAID,
            PaidDate = null,
        };
    }

    /// <summary>Marks the document paid; a paid document leaves the sellable catalog.</summary>
    public void MarkPaid(DateTime paidDate)
    {
        PaymentStatus = PaymentStatus.PAID;
        PaidDate = paidDate;
    }

    private static string Required(string value, int maxLength, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        var trimmed = value.Trim();
        if (trimmed.Length > maxLength)
            throw new ArgumentException($"{name} must not exceed {maxLength} characters.", name);
        return trimmed;
    }

    private static string? Optional(string? value, int maxLength, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        if (trimmed.Length > maxLength)
            throw new ArgumentException($"{name} must not exceed {maxLength} characters.", name);
        return trimmed;
    }

    /// <summary>Money columns are decimal(19,2): reject negatives and any scale SQL Server would round
    /// away silently. Replaces the checks <c>Money.Of</c> used to run before the currency was dropped.</summary>
    private static decimal RequireMoney(decimal value, string name)
    {
        if (value < 0)
            throw new ArgumentException($"{name} must not be negative.", name);
        if (decimal.Round(value, 2) != value)
            throw new ArgumentException($"{name} must not have more than 2 decimal places.", name);
        return value;
    }

    private static decimal? RequireMoney(decimal? value, string name) =>
        value is { } v ? RequireMoney(v, name) : null;
}
