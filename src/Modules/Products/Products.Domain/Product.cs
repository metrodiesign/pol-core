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

    public bool IsActive { get; private set; }

    public DateTime CreatedAt { get; private set; }

    /// <summary>Parameterless ctor for EF Core materialisation only.</summary>
    private Product() { }

    private Product(Guid id, Guid merchantId, string name, Money price, DateTime createdAt)
        : base(id)
    {
        MerchantId = merchantId;
        Name = name;
        Price = price;
        IsActive = true;
        CreatedAt = createdAt;
    }

    /// <summary>Creates a new active product for a merchant.</summary>
    public static Product Create(Guid merchantId, string name, Money price, DateTime createdAt)
    {
        if (merchantId == Guid.Empty)
            throw new ArgumentException("MerchantId is required.", nameof(merchantId));
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return new Product(Guid.NewGuid(), merchantId, name.Trim(), price, createdAt);
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
