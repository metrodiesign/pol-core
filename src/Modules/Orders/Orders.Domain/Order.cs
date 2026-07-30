using Orders.Domain.Items;
using SharedKernel;

namespace Orders.Domain;

/// <summary>
/// An order placed by a merchant's buyer. Created <see cref="OrderStatus.AwaitingPayment"/> and
/// fulfilled when the Payments module confirms a PSP-settled charge. <see cref="Amount"/> is the
/// money seam (PLAN decision #2), mapped as an EF complex type (rf1 — decimal(19,4) + char(3)
/// columns). <see cref="MarkPaid"/> re-verifies the paid amount + currency before transitioning
/// and is idempotent, so a replayed PaymentPaid never double-fulfils (PLAN decision #10).
/// <see cref="Items"/> records which insurance plan(s) the order is for (insurance-pivot REQ-6) —
/// added to this existing aggregate without touching the state machine above.
/// </summary>
public sealed class Order : AggregateRoot<Guid>
{
    private readonly List<Item> _items = [];

    public Guid MerchantId { get; private set; }

    /// <summary>The payment session this order is awaiting confirmation from, set when checkout
    /// hands the order to Payments. Null until a session is opened.</summary>
    public Guid? PaymentSessionId { get; private set; }

    /// <summary>The checkout session this order was created from, when it came through the checkout flow.
    /// Unique (filtered) so a replayed CheckoutConfirmed event cannot create a second order.</summary>
    public Guid? CheckoutSessionId { get; private set; }

    public Money Amount { get; private set; }

    public OrderStatus Status { get; private set; }

    public DateTime CreatedAt { get; private set; }

    public DateTime? PaidAt { get; private set; }

    /// <summary>Opaque, unguessable token for the customer's summary link (capability, not a secret —
    /// just hard to guess). Rotated by <see cref="ReissueSummary"/>.</summary>
    public string SummaryToken { get; private set; } = default!;

    /// <summary>When the current <see cref="SummaryToken"/> stops working — opening the link after this
    /// is a 410 Gone. A resend rotates the token and extends this.</summary>
    public DateTime SummaryTokenExpiresAt { get; private set; }

    /// <summary>The customer contact (email/phone) captured upstream to notify with the summary link.
    /// Persisted so a merchant-user-triggered resend can re-notify the customer (REQ-2.5); null = no recipient.</summary>
    public string? NotificationRecipient { get; private set; }

    /// <summary>Default lifetime of a summary link (reference: links have a TTL; expired = error).</summary>
    public static readonly TimeSpan SummaryTokenTtl = TimeSpan.FromHours(72);

    /// <summary>The order's items, in insertion order — one per purchased plan (insurance-pivot REQ-6.1/6.2).
    /// Mutated only by <see cref="Create"/>.</summary>
    public IReadOnlyCollection<Item> Items => _items.AsReadOnly();

    private Order() { }

    private Order(Guid id, Guid merchantId, Guid? paymentSessionId, Guid? checkoutSessionId, Money amount,
        string? notificationRecipient, DateTime createdAt)
        : base(id)
    {
        MerchantId = merchantId;
        PaymentSessionId = paymentSessionId;
        CheckoutSessionId = checkoutSessionId;
        Amount = amount;
        NotificationRecipient = notificationRecipient;
        Status = OrderStatus.AwaitingPayment;
        CreatedAt = createdAt;
        SummaryToken = Guid.NewGuid().ToString("N");
        SummaryTokenExpiresAt = createdAt + SummaryTokenTtl;
    }

    /// <summary>True once the summary link's TTL has passed.</summary>
    public bool IsSummaryExpired(DateTime now) => now >= SummaryTokenExpiresAt;

    /// <summary>Rotates the summary token and extends its TTL (a resend). Only an order still awaiting
    /// payment has a link to reissue; a paid/cancelled order is rejected.</summary>
    public void ReissueSummary(DateTime now)
    {
        if (Status != OrderStatus.AwaitingPayment)
            throw new InvalidOperationException($"Cannot reissue the summary link of an order in status {Status}.");

        SummaryToken = Guid.NewGuid().ToString("N");
        SummaryTokenExpiresAt = now + SummaryTokenTtl;
    }

    /// <summary>
    /// Opens a new order awaiting payment for one or more purchased plans (insurance-pivot REQ-6). Rejects
    /// an empty <paramref name="items"/> (REQ-6.7 — an order is never opened without at least one item), an
    /// item whose <see cref="OrderItemInput.Quantity"/> isn't 1 (defense in depth — the checkout endpoint
    /// already rejects this earlier), and an item-total sum that doesn't equal <paramref name="amount"/>
    /// exactly, same currency (REQ-6.3).
    /// </summary>
    public static Order Create(Guid merchantId, Money amount, DateTime createdAt, IReadOnlyList<OrderItemInput> items,
        Guid? paymentSessionId = null, Guid? checkoutSessionId = null, string? notificationRecipient = null)
    {
        if (items is null || items.Count == 0)
            throw new ArgumentException("An order must have at least one line.", nameof(items));

        var total = Money.Zero(amount.Currency);
        foreach (var item in items)
        {
            if (item.Quantity != 1)
                throw new ArgumentException("Insurance lines must have quantity 1.", nameof(items));
            if (!item.UnitPrice.SameCurrencyAs(amount))
                throw new ArgumentException("Line currency must match the order amount currency.", nameof(items));

            total = total.Add(Money.Of(item.UnitPrice.Amount * item.Quantity, item.UnitPrice.Currency));
        }

        if (total.Amount != amount.Amount)
            throw new ArgumentException("The sum of line totals must equal the order amount.", nameof(amount));

        var order = new Order(
            Guid.NewGuid(), merchantId, paymentSessionId, checkoutSessionId, amount, notificationRecipient, createdAt);

        foreach (var item in items)
            order._items.Add(new Item(
                Guid.CreateVersion7(), order.Id, merchantId, item.ProductId, item.Quantity, item.UnitPrice,
                item.DocumentNo, item.ProductGroup, item.DocumentType, item.PolicyNumber,
                item.StartDate, item.EndDate,
                item.InsuredFirstName, item.InsuredLastName, item.InsuredIdNumber, item.InsuredDateOfBirth,
                createdAt));

        return order;
    }

    /// <summary>
    /// Binds the payment session this order awaits. Legacy link with no production writer — the
    /// PaymentPaid consumer resolves orders by the event's <c>OrderId</c>, not by this value
    /// (bugfix-order-paid-link F2).
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
    public bool MarkPaid(Money paidAmount, DateTime occurredAt)
    {
        if (Status == OrderStatus.Paid)
            return false;

        if (Status == OrderStatus.Cancelled)
            throw new InvalidOperationException("Cannot mark a cancelled order as paid.");

        if (!paidAmount.SameCurrencyAs(Amount) || paidAmount.Amount != Amount.Amount)
            throw new InvalidOperationException(
                $"Paid amount {paidAmount} does not match order amount {Amount}.");

        Status = OrderStatus.Paid;
        PaidAt = occurredAt;
        Raise(new OrderPaid(Id, occurredAt));
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
