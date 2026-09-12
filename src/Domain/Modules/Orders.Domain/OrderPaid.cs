using SharedKernel;

namespace Orders.Domain;

/// <summary>In-aggregate event recorded when an <see cref="Order"/> first transitions to
/// <see cref="OrderStatus.Paid"/>. Stays inside the module — cross-module facts travel as
/// <c>Contracts</c> notifications, not this.</summary>
public sealed record OrderPaid(Guid OrderId, DateTime PaidAt) : IDomainEvent;
