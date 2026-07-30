using SharedKernel;

namespace Orders.Domain.Items;

/// <summary>
/// Primitive input for <see cref="Order.Create"/> — mirrors how <c>Cart.AddItem</c> takes scalars rather
/// than an <c>Item</c>, so a caller building an order doesn't have to construct the domain <see cref="Item"/>
/// entity by hand. Lives in <c>Orders.Domain</c> (not <c>Orders.Application</c>, despite design.md's
/// wording) — a Domain factory method cannot take an Application-layer parameter type without an illegal
/// reverse project reference (<c>Orders.Domain.csproj</c> has none to <c>Orders.Application.csproj</c>,
/// and never should); this is the same primitive-input shape, just placed where it can actually compile.
/// </summary>
public sealed record OrderItemInput(
    Guid ProductId, int Quantity, Money UnitPrice,
    string DocumentNo, string ProductGroup, string DocumentType, string? PolicyNumber,
    DateTime? StartDate, DateTime? EndDate,
    string InsuredFirstName, string InsuredLastName, string InsuredIdNumber, DateTime InsuredDateOfBirth);
