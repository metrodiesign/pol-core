using Mediator;

namespace Contracts;

/// <summary>
/// OrderPaid v1 — emitted (via the transactional outbox) when an order transitions to Paid, so the Products
/// module can retire each sold document from the sellable catalog (mark PAID + inactive). Published
/// at-least-once; the consumer is idempotent because <c>Product.MarkPaid</c> is a state-setter safe to
/// replay. Carries the merchant, the product ids of every order line, and when the payment occurred.
/// </summary>
/// <param name="OrderId">Which order bought these documents — additive, so a payload enqueued before this
/// field existed still deserializes, with <see cref="Guid.Empty"/> here. The consumer needs it to tell a
/// redelivery of this order apart from a second order paying for a document already sold (REQ-5.4), and
/// skips that check entirely when it is empty. Every emitter must fill it.</param>
public sealed record OrderPaid(
    Guid MerchantId,
    IReadOnlyList<Guid> ProductIds,
    DateTime OccurredAt,
    Guid OrderId = default) : INotification
{
    public const string SchemaVersion = "v1";
}
