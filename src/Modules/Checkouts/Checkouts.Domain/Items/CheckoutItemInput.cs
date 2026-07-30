using SharedKernel;

namespace Checkouts.Domain.Items;

/// <summary>Primitive input for <see cref="Session.Start"/> — mirrors <c>Orders.Domain.Items.OrderItemInput</c>'s
/// role and the same reason for living in the Domain project, not Application.</summary>
public sealed record CheckoutItemInput(
    Guid ProductId, int Quantity, Money UnitPrice,
    string DocumentNo, string ProductGroup, string DocumentType, string? PolicyNumber,
    DateTime? StartDate, DateTime? EndDate,
    string InsuredFirstName, string InsuredLastName, string InsuredIdNumber, DateTime InsuredDateOfBirth);
