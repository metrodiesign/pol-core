using SharedKernel;

namespace Checkouts.Domain.Items;

/// <summary>Primitive input for <see cref="Session.Start"/> — mirrors <c>Orders.Domain.Items.OrderItemInput</c>'s
/// role and the same reason for living in the Domain project, not Application.</summary>
public sealed record CheckoutItemInput(
    Guid ProductId, int Quantity, Money UnitPrice, Money SumInsured, int CoverageDurationDays, string Insurer,
    string InsuredFirstName, string InsuredLastName, string InsuredIdNumber, DateTime InsuredDateOfBirth);
