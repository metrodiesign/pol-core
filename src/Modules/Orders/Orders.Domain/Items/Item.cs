using SharedKernel;

namespace Orders.Domain.Items;

/// <summary>
/// An item in an <see cref="Order"/> — one purchased insurance plan, quantity constrained to 1 for this
/// spec (insurance-pivot REQ-6/7). Owned by its order — never loaded or mutated on its own. Carries a
/// purchase-time snapshot of the insurance document's terms (<see cref="DocumentNo"/>/
/// <see cref="ProductGroup"/>/<see cref="DocumentType"/>/<see cref="PolicyNumber"/>/<see cref="StartDate"/>/
/// <see cref="EndDate"/>, copied from <c>Product</c> at checkout-start, never re-read live) plus the insured
/// person's data (REQ-7.1) — 1 person per item.
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

    /// <summary>Discount on this line, in the line's own currency, carried from the checkout snapshot
    /// (purchase-flow-completion REQ-7.2). Zero when none was given — never null.</summary>
    public Money Discount { get; private set; }

    /// <summary>Document number, snapshotted from <c>Product.DocumentNo</c> at checkout-start.</summary>
    public string DocumentNo { get; private set; } = default!;

    /// <summary>Wire value of <c>Product.ProductGroup</c> (no cross-module reference to the enum).</summary>
    public string ProductGroup { get; private set; } = default!;

    /// <summary>Wire value of <c>Product.DocumentType</c> (no cross-module reference to the enum).</summary>
    public string DocumentType { get; private set; } = default!;

    public string? PolicyNumber { get; private set; }
    public DateTime? StartDate { get; private set; }
    public DateTime? EndDate { get; private set; }

    public string InsuredFirstName { get; private set; } = default!;
    public string InsuredLastName { get; private set; } = default!;
    public string InsuredIdNumber { get; private set; } = default!;
    public DateTime InsuredDateOfBirth { get; private set; }

    /// <summary>Parameterless ctor for EF Core materialisation only.</summary>
    private Item() { }

    internal Item(
        Guid id, Guid orderId, Guid merchantId, Guid productId, int quantity, Money unitPrice, Money discount,
        string documentNo, string productGroup, string documentType, string? policyNumber,
        DateTime? startDate, DateTime? endDate,
        string insuredFirstName, string insuredLastName, string insuredIdNumber, DateTime insuredDateOfBirth,
        DateTime nowUtc)
        : base(id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentNo, nameof(documentNo));
        ArgumentException.ThrowIfNullOrWhiteSpace(productGroup, nameof(productGroup));
        ArgumentException.ThrowIfNullOrWhiteSpace(documentType, nameof(documentType));
        if (startDate is not null && endDate is not null && startDate > endDate)
            throw new ArgumentException("Start date must not be after the end date.", nameof(startDate));

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
        Discount = discount;
        DocumentNo = documentNo.Trim();
        ProductGroup = productGroup.Trim();
        DocumentType = documentType.Trim();
        PolicyNumber = policyNumber;
        StartDate = startDate;
        EndDate = endDate;
        InsuredFirstName = insuredFirstName.Trim();
        InsuredLastName = insuredLastName.Trim();
        InsuredIdNumber = insuredIdNumber.Trim();
        InsuredDateOfBirth = insuredDateOfBirth;
    }
}
