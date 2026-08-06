using SharedKernel;

namespace Checkouts.Domain.Items;

/// <summary>Primitive input for <see cref="Session.Start"/> — mirrors <c>Orders.Domain.Items.OrderItemInput</c>'s
/// role and the same reason for living in the Domain project, not Application. <paramref name="Discount"/> is
/// optional (purchase-flow-completion REQ-6.3): null means no discount, which <see cref="Session.Start"/>
/// normalises to zero in the line's own currency.</summary>
public sealed record CheckoutItemInput(
    int Quantity, Money UnitPrice,
    string DocumentNo, string ProductGroup, string DocumentType, string? PolicyNumber,
    DateTime? StartDate, DateTime? EndDate,
    string InsuredFirstName, string InsuredLastName, string InsuredIdNumber, DateTime InsuredDateOfBirth,
    Money? Discount = null);
