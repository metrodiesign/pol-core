using Mediator;

namespace Contracts;

/// <summary>
/// OrderPaid v1 — emitted (via the transactional outbox) when an order transitions to Paid, so the Products
/// module can retire each sold document from the sellable catalog (mark PAID + inactive). Published
/// at-least-once; the consumer is idempotent because <c>Product.MarkPaid</c> is a state-setter safe to
/// replay. Carries the merchant, the product ids of every order line, and when the payment occurred.
/// </summary>
public sealed record OrderPaid(
    Guid MerchantId,
    IReadOnlyList<Guid> ProductIds,
    DateTime OccurredAt) : INotification
{
    public const string SchemaVersion = "v1";
}
