using Mediator;
using SharedKernel;

namespace Contracts;

/// <summary>One purchased item of a confirmed checkout (insurance-pivot REQ-6/7) — the commercial +
/// insurance-term snapshot plus the insured person, frozen at checkout-start.</summary>
public sealed record CheckoutConfirmedItem(
    Guid ProductId, int Quantity, Money UnitPrice, Money SumInsured, int CoverageDurationDays, string Insurer,
    string InsuredFirstName, string InsuredLastName, string InsuredIdNumber, DateTime InsuredDateOfBirth);

/// <summary>
/// CheckoutConfirmed v1 — emitted (via the transactional outbox) when a checkout is confirmed, so the
/// Orders module can open the order out-of-band. Published at-least-once; the consumer is idempotent on
/// <see cref="CheckoutSessionId"/> (one order per checkout). Carries the agreed <see cref="Amount"/> +
/// the optional notification recipient so the created order can notify the customer. <see cref="Items"/>
/// is an additive v1 field (insurance-pivot REQ-6.6) — required, not nullable (see design.md's Technology
/// Decisions for the explicit rebuttal of making it optional for in-flight-message compatibility: this
/// project has never shipped a live-traffic event-compatibility scenario to protect).
/// </summary>
public sealed record CheckoutConfirmed(
    Guid MerchantId,
    Guid CheckoutSessionId,
    Money Amount,
    string? Recipient,
    DateTime OccurredAt,
    IReadOnlyList<CheckoutConfirmedItem> Items) : INotification
{
    public const string SchemaVersion = "v1";
}
