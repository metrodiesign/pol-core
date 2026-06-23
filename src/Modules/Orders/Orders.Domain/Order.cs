using SharedKernel;

namespace Orders.Domain;

/// <summary>
/// An order placed by a tenant's buyer. Created <see cref="OrderStatus.AwaitingPayment"/> and
/// fulfilled when the Payments module confirms a PSP-settled charge. <see cref="Amount"/> is the
/// money seam (PLAN decision #2): stored as two scalar columns and recomputed via
/// <see cref="Money.Of"/>, never mapped as an owned type (avoids EF friction with the validating
/// struct ctor). <see cref="MarkPaid"/> re-verifies the paid amount + currency before transitioning
/// and is idempotent, so a replayed PaymentPaid never double-fulfils (PLAN decision #10).
/// </summary>
public sealed class Order : AggregateRoot<Guid>
{
    public Guid TenantId { get; private set; }

    /// <summary>The payment session this order is awaiting confirmation from, set when checkout
    /// hands the order to Payments. Null until a session is opened.</summary>
    public Guid? PaymentSessionId { get; private set; }

    /// <summary>The checkout session this order was created from, when it came through the checkout flow.
    /// Unique (filtered) so a replayed CheckoutConfirmed event cannot create a second order.</summary>
    public Guid? CheckoutSessionId { get; private set; }

    public long AmountMinorUnits { get; private set; }

    public string AmountCurrency { get; private set; } = default!;

    /// <summary>The order total, recomposed from the two scalar columns. Not mapped by EF.</summary>
    public Money Amount => Money.Of(AmountMinorUnits, AmountCurrency);

    public OrderStatus Status { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }

    public DateTime? PaidAtUtc { get; private set; }

    /// <summary>Opaque, unguessable token for the customer's summary link (capability, not a secret —
    /// just hard to guess). Rotated by <see cref="ReissueSummary"/>.</summary>
    public string SummaryToken { get; private set; } = default!;

    /// <summary>When the current <see cref="SummaryToken"/> stops working — opening the link after this
    /// is a 410 Gone. A resend rotates the token and extends this.</summary>
    public DateTime SummaryTokenExpiresAtUtc { get; private set; }

    /// <summary>The customer contact (email/phone) captured upstream to notify with the summary link.
    /// Persisted so a producer-triggered resend can re-notify the customer (REQ-2.5); null = no recipient.</summary>
    public string? NotificationRecipient { get; private set; }

    /// <summary>Default lifetime of a summary link (reference: links have a TTL; expired = error).</summary>
    public static readonly TimeSpan SummaryTokenTtl = TimeSpan.FromHours(72);

    private Order() { }

    private Order(Guid id, Guid tenantId, Guid? paymentSessionId, Guid? checkoutSessionId, Money amount,
        string? notificationRecipient, DateTime createdAtUtc)
        : base(id)
    {
        TenantId = tenantId;
        PaymentSessionId = paymentSessionId;
        CheckoutSessionId = checkoutSessionId;
        AmountMinorUnits = amount.MinorUnits;
        AmountCurrency = amount.Currency;
        NotificationRecipient = notificationRecipient;
        Status = OrderStatus.AwaitingPayment;
        CreatedAtUtc = createdAtUtc;
        SummaryToken = Guid.NewGuid().ToString("N");
        SummaryTokenExpiresAtUtc = createdAtUtc + SummaryTokenTtl;
    }

    /// <summary>True once the summary link's TTL has passed.</summary>
    public bool IsSummaryExpired(DateTime now) => now >= SummaryTokenExpiresAtUtc;

    /// <summary>Rotates the summary token and extends its TTL (a resend). Only an order still awaiting
    /// payment has a link to reissue; a paid/cancelled order is rejected.</summary>
    public void ReissueSummary(DateTime now)
    {
        if (Status != OrderStatus.AwaitingPayment)
            throw new InvalidOperationException($"Cannot reissue the summary link of an order in status {Status}.");

        SummaryToken = Guid.NewGuid().ToString("N");
        SummaryTokenExpiresAtUtc = now + SummaryTokenTtl;
    }

    /// <summary>Opens a new order awaiting payment.</summary>
    public static Order Create(Guid tenantId, Money amount, DateTime createdAtUtc,
        Guid? paymentSessionId = null, Guid? checkoutSessionId = null, string? notificationRecipient = null) =>
        new(Guid.NewGuid(), tenantId, paymentSessionId, checkoutSessionId, amount, notificationRecipient, createdAtUtc);

    /// <summary>
    /// Binds the payment session this order awaits. The session is the join key the
    /// PaymentPaid consumer loads the order by.
    /// </summary>
    public void AttachPaymentSession(Guid paymentSessionId)
    {
        if (Status != OrderStatus.AwaitingPayment)
            throw new InvalidOperationException(
                $"Cannot attach a payment session to an order in status {Status}.");

        PaymentSessionId = paymentSessionId;
    }

    /// <summary>
    /// Fulfils the order against a confirmed payment. Re-verifies amount AND currency against the
    /// order's own total (PLAN decision #2 — never trust the event's id alone). Idempotent: a
    /// second call once already <see cref="OrderStatus.Paid"/> is a no-op, so a replayed event is
    /// safe (PLAN decision #10). Returns true only on the first transition (an event was raised).
    /// </summary>
    public bool MarkPaid(Money paidAmount, DateTime occurredAtUtc)
    {
        if (Status == OrderStatus.Paid)
            return false;

        if (Status == OrderStatus.Cancelled)
            throw new InvalidOperationException("Cannot mark a cancelled order as paid.");

        if (!paidAmount.SameCurrencyAs(Amount) || paidAmount.MinorUnits != Amount.MinorUnits)
            throw new InvalidOperationException(
                $"Paid amount {paidAmount} does not match order amount {Amount}.");

        Status = OrderStatus.Paid;
        PaidAtUtc = occurredAtUtc;
        Raise(new OrderPaid(Id, occurredAtUtc));
        return true;
    }

    /// <summary>Cancels an unpaid order. No-op if already cancelled; rejected once paid.</summary>
    public void Cancel()
    {
        if (Status == OrderStatus.Cancelled)
            return;

        if (Status == OrderStatus.Paid)
            throw new InvalidOperationException("Cannot cancel a paid order.");

        Status = OrderStatus.Cancelled;
    }
}
