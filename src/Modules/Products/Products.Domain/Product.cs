using SharedKernel;

namespace Products.Domain;

/// <summary>
/// A merchant-owned catalog item. <see cref="Price"/> is mapped as an EF complex type (rf1 —
/// decimal(19,4) + char(3) columns), per the EF mapping rule for <c>Money</c>.
/// </summary>
public sealed class Product : AggregateRoot<Guid>
{
    public Guid MerchantId { get; private set; }

    public string Name { get; private set; } = default!;

    public Money Price { get; private set; }

    /// <summary>Sum insured (ทุนประกัน) of this insurance plan (insurance-pivot REQ-1.1). Must share
    /// <see cref="Price"/>'s currency (REQ-1.5).</summary>
    public Money SumInsured { get; private set; }

    /// <summary>Coverage duration in days (insurance-pivot REQ-1.1) — a catalog template has no concrete
    /// start/end date, only a duration.</summary>
    public int CoverageDurationDays { get; private set; }

    /// <summary>The insurer underwriting this plan (insurance-pivot REQ-1.1/1.6) — a plain column, not a
    /// separate master-data entity (locked decision).</summary>
    public string Insurer { get; private set; } = default!;

    public bool IsActive { get; private set; }

    public DateTime CreatedAt { get; private set; }

    /// <summary>Parameterless ctor for EF Core materialisation only.</summary>
    private Product() { }

    private Product(
        Guid id, Guid merchantId, string name, Money price, Money sumInsured, int coverageDurationDays,
        string insurer, DateTime createdAt)
        : base(id)
    {
        MerchantId = merchantId;
        Name = name;
        Price = price;
        SumInsured = sumInsured;
        CoverageDurationDays = coverageDurationDays;
        Insurer = insurer;
        IsActive = true;
        CreatedAt = createdAt;
    }

    /// <summary>Creates a new active insurance plan for a merchant (insurance-pivot REQ-1).</summary>
    public static Product Create(
        Guid merchantId, string name, Money price, Money sumInsured, int coverageDurationDays, string insurer,
        DateTime createdAt)
    {
        if (merchantId == Guid.Empty)
            throw new ArgumentException("MerchantId is required.", nameof(merchantId));
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (sumInsured.Amount <= 0)
            throw new ArgumentException("SumInsured must be greater than zero.", nameof(sumInsured));
        if (!sumInsured.SameCurrencyAs(price))
            throw new ArgumentException("SumInsured currency must match Price currency.", nameof(sumInsured));
        if (coverageDurationDays <= 0)
            throw new ArgumentException("CoverageDurationDays must be greater than zero.", nameof(coverageDurationDays));
        ArgumentException.ThrowIfNullOrWhiteSpace(insurer);

        return new Product(
            Guid.NewGuid(), merchantId, name.Trim(), price, sumInsured, coverageDurationDays, insurer.Trim(),
            createdAt);
    }

    /// <summary>Renames the product.</summary>
    public void Rename(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name.Trim();
    }

    /// <summary>Marks the product inactive so it no longer appears in the active catalog.</summary>
    public void Deactivate() => IsActive = false;
}
