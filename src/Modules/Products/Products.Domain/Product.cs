using SharedKernel;

namespace Products.Domain;

/// <summary>
/// A tenant-owned catalog item. Holds the price as two scalar columns
/// (<see cref="PriceMinorUnits"/> + <see cref="PriceCurrency"/>) and exposes the validated
/// <see cref="Money"/> seam via <see cref="Price"/>, per the EF mapping rule for <c>Money</c>.
/// </summary>
public sealed class Product : AggregateRoot<Guid>
{
    public Guid TenantId { get; private set; }

    public string Name { get; private set; } = default!;

    public long PriceMinorUnits { get; private set; }

    /// <summary>ISO 4217 alpha-3 code backing <see cref="Price"/>.</summary>
    public string PriceCurrency { get; private set; } = default!;

    public bool IsActive { get; private set; }

    public DateTime CreatedAt { get; private set; }

    /// <summary>The validated money seam, reconstituted from the two scalar columns.</summary>
    public Money Price => Money.Of(PriceMinorUnits, PriceCurrency);

    /// <summary>Parameterless ctor for EF Core materialisation only.</summary>
    private Product() { }

    private Product(Guid id, Guid tenantId, string name, Money price, DateTime createdAt)
        : base(id)
    {
        TenantId = tenantId;
        Name = name;
        PriceMinorUnits = price.MinorUnits;
        PriceCurrency = price.Currency;
        IsActive = true;
        CreatedAt = createdAt;
    }

    /// <summary>Creates a new active product for a tenant.</summary>
    public static Product Create(Guid tenantId, string name, Money price, DateTime createdAt)
    {
        if (tenantId == Guid.Empty)
            throw new ArgumentException("TenantId is required.", nameof(tenantId));
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return new Product(Guid.NewGuid(), tenantId, name.Trim(), price, createdAt);
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
