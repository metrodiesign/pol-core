using SharedKernel;

namespace Checkouts.Domain.Lines;

/// <summary>
/// A line snapshotted onto a <see cref="Session"/> at <see cref="Session.Start"/> (insurance-pivot REQ-6.5) —
/// freezes the commercial + insurance terms and the insured person for one purchased plan, so nothing is
/// re-read live between checkout-start and confirm. A DIFFERENT CLR type from <c>Orders.Domain.Lines.Line</c>
/// (no cross-module domain reference — the two modules only share data via the <c>Contracts</c> DTO); a
/// plain snapshot holder with no validation of its own (the insured-person validation lives in
/// <c>Orders.Domain.Lines.Line</c>'s constructor, per design.md's error-handling table).
/// </summary>
public sealed class Line : Entity<Guid>
{
    public Guid SessionId { get; private set; }

    /// <summary>Denormalized from the parent <see cref="Session"/>, mirrors <c>Carts.Domain.Items.Item.MerchantId</c>.</summary>
    public Guid MerchantId { get; private set; }

    public Guid ProductId { get; private set; }
    public int Quantity { get; private set; }
    public Money UnitPrice { get; private set; }
    public Money SumInsured { get; private set; }
    public int CoverageDurationDays { get; private set; }
    public string Insurer { get; private set; } = default!;

    public string InsuredFirstName { get; private set; } = default!;
    public string InsuredLastName { get; private set; } = default!;
    public string InsuredIdNumber { get; private set; } = default!;
    public DateTime InsuredDateOfBirth { get; private set; }

    /// <summary>Parameterless ctor for EF Core materialisation only.</summary>
    private Line() { }

    internal Line(
        Guid id, Guid sessionId, Guid merchantId, Guid productId, int quantity, Money unitPrice,
        Money sumInsured, int coverageDurationDays, string insurer,
        string insuredFirstName, string insuredLastName, string insuredIdNumber, DateTime insuredDateOfBirth)
        : base(id)
    {
        SessionId = sessionId;
        MerchantId = merchantId;
        ProductId = productId;
        Quantity = quantity;
        UnitPrice = unitPrice;
        SumInsured = sumInsured;
        CoverageDurationDays = coverageDurationDays;
        Insurer = insurer;
        InsuredFirstName = insuredFirstName;
        InsuredLastName = insuredLastName;
        InsuredIdNumber = insuredIdNumber;
        InsuredDateOfBirth = insuredDateOfBirth;
    }
}
