using SharedKernel;

namespace Products.Domain;

/// <summary>
/// A merchant-owned sellable insurance document (VCentralPay SP guide §2/§5.2) — one row per
/// APPLICATION / POLICY / RENEWAL / ENDORSEMENT awaiting payment. <see cref="TotalPremium"/> is the
/// selling price, mapped as an EF complex type (decimal(19,4) + char(3) columns, per the Money rule);
/// the optional premium breakdown uses the nullable Amount+Currency pair pattern (see
/// <c>Orders.Domain.Items.ItemPolicy</c>).
/// </summary>
public sealed class Product : AggregateRoot<Guid>
{
    public Guid MerchantId { get; private set; }

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

    public string BranchCode { get; private set; } = default!;
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

    /// <summary>Insured display name (SP guide §5.2 <c>ShowName</c>; searched via <c>@InsuredName</c>).</summary>
    public string? ShowName { get; private set; }

    /// <summary>Motor documents only; always null for Non-Motor.</summary>
    public string? LicensePlateNumber { get; private set; }

    /// <summary>Total premium (เบี้ยรวม) — the selling price of this document.</summary>
    public Money TotalPremium { get; private set; }

    public decimal? NetPremiumAmount { get; private set; }
    public string? NetPremiumCurrency { get; private set; }
    public decimal? StampAmount { get; private set; }
    public string? StampCurrency { get; private set; }
    public decimal? TaxVatAmount { get; private set; }
    public string? TaxVatCurrency { get; private set; }
    public decimal? CommissionAmountAmount { get; private set; }
    public string? CommissionAmountCurrency { get; private set; }

    /// <summary>Commission rate — decimal(19,6) in the source contract, not a Money.</summary>
    public decimal? CommissionPercent { get; private set; }

    public PaymentStatus PaymentStatus { get; private set; }

    /// <summary>Null while <see cref="PaymentStatus"/> is <see cref="PaymentStatus.UNPAID"/>.</summary>
    public DateTime? PaidDate { get; private set; }

    /// <summary>Still sellable (unpaid and not withdrawn) — the bridge flag the cart/checkout flow keys on.</summary>
    public bool IsActive { get; private set; }

    public DateTime CreatedAt { get; private set; }

    /// <summary>Motor vs Non-Motor, derived from <see cref="ProductGroup"/> — never stored.</summary>
    public InsuranceType InsuranceType =>
        ProductGroup is ProductGroup.CMI or ProductGroup.VMI ? InsuranceType.Motor : InsuranceType.NonMotor;

    public Money? NetPremium =>
        NetPremiumAmount is { } a && NetPremiumCurrency is { } c ? Money.Of(a, c) : null;

    public Money? Stamp =>
        StampAmount is { } a && StampCurrency is { } c ? Money.Of(a, c) : null;

    public Money? TaxVat =>
        TaxVatAmount is { } a && TaxVatCurrency is { } c ? Money.Of(a, c) : null;

    public Money? CommissionAmount =>
        CommissionAmountAmount is { } a && CommissionAmountCurrency is { } c ? Money.Of(a, c) : null;

    /// <summary>Parameterless ctor for EF Core materialisation only.</summary>
    private Product() { }

    /// <summary>Creates a new unpaid, active insurance document for a merchant.</summary>
    public static Product Create(ProductInput input, DateTime createdAt)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (input.MerchantId == Guid.Empty)
            throw new ArgumentException("MerchantId is required.", nameof(input));

        var branchCode = Required(input.BranchCode, 3, nameof(input.BranchCode));
        var saleCode = Required(input.SaleCode, 20, nameof(input.SaleCode));
        var documentNo = Required(input.DocumentNo, 150, nameof(input.DocumentNo));

        if (input.TotalPremium.Amount <= 0)
            throw new ArgumentException("TotalPremium must be greater than zero.", nameof(input));
        RequireThb(input.TotalPremium, nameof(input.TotalPremium));
        RequireThb(input.NetPremium, nameof(input.NetPremium));
        RequireThb(input.Stamp, nameof(input.Stamp));
        RequireThb(input.TaxVat, nameof(input.TaxVat));
        RequireThb(input.CommissionAmount, nameof(input.CommissionAmount));

        if (input.StartDate is { } start && input.EndDate is { } end && start > end)
            throw new ArgumentException("StartDate must not be after EndDate.", nameof(input));

        if (input.ProductGroup == ProductGroup.CMI && input.DocumentType == DocumentType.APPLICATION)
            throw new ArgumentException("Motor/CMI does not support APPLICATION documents.", nameof(input));

        return new Product
        {
            Id = Guid.NewGuid(),
            MerchantId = input.MerchantId,
            ProductGroup = input.ProductGroup,
            DocumentType = input.DocumentType,
            DocumentNo = documentNo,
            PolicyYear = Optional(input.PolicyYear, 2, nameof(input.PolicyYear)),
            ReferenceBranch = Optional(input.ReferenceBranch, 3, nameof(input.ReferenceBranch)),
            ReferencePre = Optional(input.ReferencePre, 20, nameof(input.ReferencePre)),
            PolicySequenceNo = Optional(input.PolicySequenceNo, 30, nameof(input.PolicySequenceNo)),
            ReferenceYear = Optional(input.ReferenceYear, 2, nameof(input.ReferenceYear)),
            ReferenceNo = Optional(input.ReferenceNo, 30, nameof(input.ReferenceNo)),
            BranchCode = branchCode,
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
            TotalPremium = input.TotalPremium,
            NetPremiumAmount = input.NetPremium?.Amount,
            NetPremiumCurrency = input.NetPremium?.Currency,
            StampAmount = input.Stamp?.Amount,
            StampCurrency = input.Stamp?.Currency,
            TaxVatAmount = input.TaxVat?.Amount,
            TaxVatCurrency = input.TaxVat?.Currency,
            CommissionAmountAmount = input.CommissionAmount?.Amount,
            CommissionAmountCurrency = input.CommissionAmount?.Currency,
            CommissionPercent = input.CommissionPercent,
            PaymentStatus = PaymentStatus.UNPAID,
            PaidDate = null,
            IsActive = true,
            CreatedAt = createdAt,
        };
    }

    /// <summary>Marks the document paid; a paid document leaves the sellable catalog.</summary>
    public void MarkPaid(DateTime paidDate)
    {
        PaymentStatus = PaymentStatus.PAID;
        PaidDate = paidDate;
        IsActive = false;
    }

    /// <summary>Marks the document inactive so it no longer appears in the sellable catalog.</summary>
    public void Deactivate() => IsActive = false;

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

    private static void RequireThb(Money? money, string name)
    {
        if (money is { } m && !string.Equals(m.Currency, "THB", StringComparison.Ordinal))
            throw new ArgumentException($"{name} currency must be THB.", name);
    }
}
