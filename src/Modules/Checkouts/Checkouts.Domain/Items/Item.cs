using SharedKernel;

namespace Checkouts.Domain.Items;

/// <summary>
/// An item snapshotted onto a <see cref="Session"/> at <see cref="Session.Start"/> (insurance-pivot REQ-6.5) —
/// freezes the commercial + insurance terms and the insured person for one purchased plan, so nothing is
/// re-read live between checkout-start and confirm. A DIFFERENT CLR type from <c>Orders.Domain.Items.Item</c>
/// (no cross-module domain reference — the two modules only share data via the <c>Contracts</c> DTO), but
/// validates the insured-person fields the same way that type does (REQ-7.2: "WHEN confirming checkout" —
/// enforced here, at <see cref="Session.Start"/>, which happens strictly before confirm is even reachable,
/// so a bad request never reaches a successful confirm response at all; the later <c>Order.Create</c>
/// validation stays as defense in depth, same shape as the quantity==1 check being enforced at both layers).
/// </summary>
public sealed class Item : Entity<Guid>
{
    public Guid SessionId { get; private set; }

    /// <summary>Denormalized from the parent <see cref="Session"/>, mirrors <c>Carts.Domain.Items.Item.MerchantId</c>.</summary>
    public Guid MerchantId { get; private set; }

    public Guid ProductId { get; private set; }
    public int Quantity { get; private set; }
    public Money UnitPrice { get; private set; }

    /// <summary>Discount on this line, in the line's own currency (purchase-flow-completion REQ-6.3). Zero
    /// when none was given — never null, so the net is always <c>UnitPrice x Quantity - Discount</c>.</summary>
    public Money Discount { get; private set; }

    public string DocumentNo { get; private set; } = default!;
    public string ProductGroup { get; private set; } = default!;
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
        Guid id, Guid sessionId, Guid merchantId, Guid productId, int quantity, Money unitPrice, Money discount,
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

        // REQ-7.2/7.3: same checks as Orders.Domain.Items.Item's constructor, enforced here instead so a
        // bad request fails at checkout-start (before confirm is even reachable); none of these messages
        // echo the invalid value — only the field name.
        ArgumentException.ThrowIfNullOrWhiteSpace(insuredFirstName, nameof(insuredFirstName));
        ArgumentException.ThrowIfNullOrWhiteSpace(insuredLastName, nameof(insuredLastName));
        ArgumentException.ThrowIfNullOrWhiteSpace(insuredIdNumber, nameof(insuredIdNumber));
        if (insuredDateOfBirth > nowUtc)
            throw new ArgumentException("Date of birth must not be in the future.", nameof(insuredDateOfBirth));

        SessionId = sessionId;
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
