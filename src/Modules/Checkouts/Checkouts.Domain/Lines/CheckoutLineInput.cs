using SharedKernel;

namespace Checkouts.Domain.Lines;

/// <summary>Primitive input for <see cref="Session.Start"/> — mirrors <c>Orders.Domain.Lines.OrderLineInput</c>'s
/// role and the same reason for living in the Domain project, not Application.</summary>
public sealed record CheckoutLineInput(
    Guid ProductId, int Quantity, Money UnitPrice, Money SumInsured, int CoverageDurationDays, string Insurer,
    string InsuredFirstName, string InsuredLastName, string InsuredIdNumber, DateTime InsuredDateOfBirth);
