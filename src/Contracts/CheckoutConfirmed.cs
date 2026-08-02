using Mediator;
using SharedKernel;

namespace Contracts;

/// <summary>One purchased item of a confirmed checkout (insurance-pivot REQ-6/7) — the commercial +
/// insurance-document snapshot plus the insured person, frozen at checkout-start. <c>ProductGroup</c> and
/// <c>DocumentType</c> carry the wire value of the Products enums as plain strings (no cross-module
/// reference to <c>Products.Domain</c>). <c>DiscountAmount</c>/<c>DiscountCurrency</c> are the line's
/// <c>Money</c> discount in exploded form (purchase-flow-completion REQ-6.3), defaulted so a v1 payload
/// still in the outbox at deploy time deserialises to "no discount" (REQ-7.5).</summary>
public sealed record CheckoutConfirmedItem(
    Guid ProductId, int Quantity, Money UnitPrice,
    string DocumentNo, string ProductGroup, string DocumentType, string? PolicyNumber,
    DateTime? StartDate, DateTime? EndDate,
    string InsuredFirstName, string InsuredLastName, string InsuredIdNumber, DateTime InsuredDateOfBirth,
    decimal DiscountAmount = 0m, string DiscountCurrency = "THB");

/// <summary>
/// CheckoutConfirmed v1 — emitted (via the transactional outbox) when a checkout is confirmed, so the
/// Orders module can open the order out-of-band. Published at-least-once; the consumer is idempotent on
/// <see cref="CheckoutSessionId"/> (one order per checkout). Carries the agreed <see cref="Amount"/> +
/// the optional notification recipient so the created order can notify the customer. <see cref="Items"/>
/// is an additive v1 field (insurance-pivot REQ-6.6) — required, not nullable (see design.md's Technology
/// Decisions for the explicit rebuttal of making it optional for in-flight-message compatibility: this
/// project has never shipped a live-traffic event-compatibility scenario to protect).
/// <para>
/// purchase-flow-completion REQ-6.8/7.5 adds the payment channel and the buyer's three contact fields,
/// ADDITIVELY: every one is nullable with a default, so a v1 payload sitting in the outbox at deploy time
/// still deserialises and still opens an order. New emitters fill all of them; <see cref="Recipient"/> is
/// kept as the pre-REQ-6.6 fallback the consumer falls back to when the new fields are absent.
/// </para>
/// </summary>
public sealed record CheckoutConfirmed(
    Guid MerchantId,
    Guid CheckoutSessionId,
    Money Amount,
    string? Recipient,
    DateTime OccurredAt,
    IReadOnlyList<CheckoutConfirmedItem> Items,
    string? PaymentChannel = null,
    string? CustomerName = null,
    string? CustomerPhone = null,
    string? CustomerEmail = null) : INotification
{
    public const string SchemaVersion = "v1";
}
