using SharedKernel;

namespace Orders.Domain.Items;

/// <summary>
/// An item in an <see cref="Order"/> — one purchased insurance plan, quantity constrained to 1 for this
/// spec (insurance-pivot REQ-6/7). Owned by its order — never loaded or mutated on its own. Carries a
/// purchase-time snapshot of the plan's insurance terms (<see cref="SumInsured"/>/
/// <see cref="CoverageDurationDays"/>/<see cref="Insurer"/>, copied from <c>Product</c> at checkout-start,
/// never re-read live) plus the insured person's data (REQ-7.1) — 1 person per item.
/// </summary>
public sealed class Item : Entity<Guid>
{
    public Guid OrderId { get; private set; }

    /// <summary>Denormalized from the parent <see cref="Order"/> at construction (mirrors
    /// <c>Carts.Domain.Items.Item.MerchantId</c>) — enforced against drift by a composite FK.</summary>
    public Guid MerchantId { get; private set; }

    public Guid ProductId { get; private set; }

    /// <summary>Always 1 for this spec — one insured person per item (insurance-pivot locked decision).</summary>
    public int Quantity { get; private set; }

    /// <summary>Premium per unit, from <c>Cart.Item</c> at checkout-start (server, never client).</summary>
    public Money UnitPrice { get; private set; }

    /// <summary>Sum insured, snapshotted from <c>Product.SumInsured</c> at checkout-start.</summary>
    public Money SumInsured { get; private set; }

    /// <summary>Coverage duration in days, snapshotted from <c>Product.CoverageDurationDays</c>.</summary>
    public int CoverageDurationDays { get; private set; }

    /// <summary>Insurer, snapshotted from <c>Product.Insurer</c>.</summary>
    public string Insurer { get; private set; } = default!;

    public string InsuredFirstName { get; private set; } = default!;
    public string InsuredLastName { get; private set; } = default!;
    public string InsuredIdNumber { get; private set; } = default!;
    public DateTime InsuredDateOfBirth { get; private set; }

    /// <summary>Parameterless ctor for EF Core materialisation only.</summary>
    private Item() { }

    internal Item(
        Guid id, Guid orderId, Guid merchantId, Guid productId, int quantity, Money unitPrice,
        Money sumInsured, int coverageDurationDays, string insurer,
        string insuredFirstName, string insuredLastName, string insuredIdNumber, DateTime insuredDateOfBirth,
        DateTime nowUtc)
        : base(id)
    {
        // REQ-7.3: none of these messages echo the invalid value — only the field name.
        ArgumentException.ThrowIfNullOrWhiteSpace(insuredFirstName, nameof(insuredFirstName));
        ArgumentException.ThrowIfNullOrWhiteSpace(insuredLastName, nameof(insuredLastName));
        ArgumentException.ThrowIfNullOrWhiteSpace(insuredIdNumber, nameof(insuredIdNumber));
        if (insuredDateOfBirth > nowUtc)
            throw new ArgumentException("Date of birth must not be in the future.", nameof(insuredDateOfBirth));

        OrderId = orderId;
        MerchantId = merchantId;
        ProductId = productId;
        Quantity = quantity;
        UnitPrice = unitPrice;
        SumInsured = sumInsured;
        CoverageDurationDays = coverageDurationDays;
        Insurer = insurer.Trim();
        InsuredFirstName = insuredFirstName.Trim();
        InsuredLastName = insuredLastName.Trim();
        InsuredIdNumber = insuredIdNumber.Trim();
        InsuredDateOfBirth = insuredDateOfBirth;
    }
}
