using Orders.Domain.Items;
using SharedKernel;

namespace Orders.Domain;

/// <summary>
/// An order placed by a merchant's buyer. Created <see cref="OrderStatus.Pending"/> and
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
    private DateTime? _summaryTokenExpiresAt;

    public Guid MerchantId { get; private set; }
    public Guid? OriginatorId { get; private set; }
    public OrderInitiatingAudience? InitiatingAudience { get; private set; }
    public Guid? InitiatingMerchantUserId { get; private set; }

    /// <summary>Verified caller account captured by the Task 5 command boundary.</summary>
    public Guid? CreatedByAccountId { get; private set; }

    /// <summary>Resolved owner sale. Null is a valid merchant-level owner.</summary>
    public Guid? OwnerSaleId { get; private set; }

    /// <summary>Branch snapshot captured at creation. Null is a valid merchant-level owner.</summary>
    public Guid? OwnerBranchIdAtCreation { get; private set; }

    public string? BusinessType { get; private set; }

    /// <summary>Canonical client metadata envelope, stored separately from trusted product facts.</summary>
    public string? Metadata { get; private set; }

    public Money OrderDiscountAmount { get; private set; }

    public Money OrderChargeAmount { get; private set; }

    public Money SubtotalAmount { get; private set; }

    public Money TotalAmount => Amount;

    public PaymentStatus PaymentStatus { get; private set; }

    /// <summary>First verified successful Transaction for this Order. It never moves on duplicate success.</summary>
    public Guid? SuccessfulTransactionId { get; private set; }

    public bool IsFrozen { get; private set; }

    public DateTime? IssuedAt { get; private set; }

    public DateTime? FrozenAt { get; private set; }

    /// <summary>The human-readable order number the merchant quotes to the customer (purchase-flow-completion
    /// REQ-7.1): <c>ORD</c> + 2-digit Buddhist year + 8-digit running number, minted from a SQL sequence by
    /// the creating handler (<c>IOrderNoSequence</c>) and unique across the platform.</summary>
    public string OrderNo { get; private set; } = default!;

    /// <summary>Actor sale code captured when order was created directly from cart.</summary>
    public string? SaleCode { get; private set; }

    /// <summary>Current payment attempt. Null until a payment session is opened.</summary>
    public Guid? PaymentSessionId { get; private set; }

    public Money Amount { get; private set; }

    public OrderStatus Status { get; private set; }

    public DateTime CreatedAt { get; private set; }

    public DateTime UpdatedAt { get; private set; }

    public DateTime? PaidAt { get; private set; }

    /// <summary>Application-managed resource version for Admin optimistic concurrency.</summary>
    public long Version { get; private set; }

    /// <summary>Opaque, unguessable token for the customer's summary link (capability, not a secret —
    /// just hard to guess). Rotated by <see cref="ReissueSummary"/>.</summary>
    /// <remarks>Deprecated for new writes; Task 5 uses Checkouts.Domain.PaymentLink.TokenHash.</remarks>
    public string? SummaryToken { get; private set; }

    /// <summary>When the current <see cref="SummaryToken"/> stops working — opening the link after this
    /// is a 410 Gone. A resend rotates the token and extends this.</summary>
    /// <remarks>Deprecated for new writes; Task 5 uses Checkouts.Domain.PaymentLink.ExpiresAt.</remarks>
    public DateTime? SummaryTokenExpiresAt
    {
        get => _summaryTokenExpiresAt;
        private set => _summaryTokenExpiresAt = value;
    }

    /// <summary>The customer contact (email/phone) captured upstream to notify with the summary link.
    /// Persisted so a merchant-user-triggered resend can re-notify the customer (REQ-2.5); null = no recipient.
    /// Derived at creation from <see cref="CustomerPhone"/> then <see cref="CustomerEmail"/>, and still the
    /// single source of truth for WHERE the link goes (purchase-flow-completion F-03).</summary>
    public string? NotificationRecipient { get; private set; }

    /// <summary>Canonical payment-link notification intent. Email and phone remain separate so both
    /// channels can be materialized without overloading the legacy summary recipient.</summary>
    public bool NotifyOnIssue { get; private set; }
    public string? NotificationEmail { get; private set; }
    public string? NotificationPhoneNumber { get; private set; }

    /// <summary>Canonical payment method of the currently attached attempt. Null before first attempt.</summary>
    public string? PaymentChannel { get; private set; }

    /// <summary>Buyer's name, captured by direct Cart-to-Order creation.</summary>
    public string CustomerName { get; private set; } = default!;

    /// <summary>Buyer's phone, captured by direct Cart-to-Order creation.</summary>
    public string CustomerPhone { get; private set; } = default!;

    /// <summary>Buyer's email, captured by direct Cart-to-Order creation; optional.</summary>
    public string? CustomerEmail { get; private set; }

    /// <summary>The three customer columns as the value object they came in as.</summary>
    public CustomerContact Customer => CustomerContact.FromStorage(CustomerName, CustomerPhone, CustomerEmail);

    /// <summary>Default lifetime of a summary link (reference: links have a TTL; expired = error).</summary>
    public static readonly TimeSpan SummaryTokenTtl = TimeSpan.FromHours(72);

    /// <summary>The order's items, in insertion order — one per purchased plan (insurance-pivot REQ-6.1/6.2).
    /// Mutated only by <see cref="Create"/>.</summary>
    public IReadOnlyCollection<Item> Items => _items.AsReadOnly();

    private Order() { }

    private Order(Guid id, Guid merchantId, string orderNo, string? saleCode, Guid? paymentSessionId,
        Money amount, string? paymentChannel, CustomerContact customer, string? notificationRecipient,
        DateTime createdAt, Guid? originatorId, OrderInitiatingAudience? initiatingAudience,
        Guid? initiatingMerchantUserId)
        : base(id)
    {
        if (merchantId == Guid.Empty)
            throw new ArgumentException("MerchantId is required.", nameof(merchantId));
        if (originatorId == Guid.Empty)
            throw new ArgumentException("Originator id cannot be empty.", nameof(originatorId));
        ValidateInitiator(initiatingAudience, initiatingMerchantUserId, originatorId);
        MerchantId = merchantId;
        OriginatorId = originatorId;
        InitiatingAudience = initiatingAudience;
        InitiatingMerchantUserId = initiatingMerchantUserId;
        OrderNo = orderNo;
        SaleCode = string.IsNullOrWhiteSpace(saleCode) ? null : saleCode.Trim();
        PaymentSessionId = paymentSessionId;
        Amount = amount;
        PaymentChannel = NormalizePaymentChannel(paymentChannel);
        CustomerName = customer.Name;
        CustomerPhone = customer.Phone;
        CustomerEmail = customer.Email;
        // CustomerPhone ?? CustomerEmail ?? the caller's recipient (F-03) — the last one is what a
        // pre-REQ-6.6 payload carries instead of contact fields, so the notification never loses its writer.
        NotificationRecipient = customer.NotificationRecipient ?? notificationRecipient;
        Status = OrderStatus.Pending;
        PaymentStatus = PaymentStatus.Unpaid;
        SubtotalAmount = amount;
        OrderDiscountAmount = Money.Zero(amount.Currency);
        OrderChargeAmount = Money.Zero(amount.Currency);
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
        Version = 1;
        SummaryToken = Guid.NewGuid().ToString("N");
        SummaryTokenExpiresAt = createdAt + SummaryTokenTtl;
    }

    /// <summary>True once the summary link's TTL has passed.</summary>
    public bool IsSummaryExpired(DateTime? now) => SummaryTokenExpiresAt is not { } expiry || now is null || now >= expiry;

    /// <summary>Rotates the summary token and extends its TTL (a resend). Only an order still awaiting
    /// payment has a link to reissue; a paid/cancelled order is rejected.</summary>
    public void ReissueSummary(DateTime now)
    {
        if (Status is OrderStatus.Paid or OrderStatus.Cancelled or OrderStatus.Refunded)
            throw new InvalidOperationException($"Cannot reissue the summary link of an order in status {Status}.");

        SummaryToken = Guid.NewGuid().ToString("N");
        SummaryTokenExpiresAt = now + SummaryTokenTtl;
        UpdatedAt = now;
        Version++;
    }

    /// <summary>
    /// Opens a new order awaiting payment for one or more purchased plans (insurance-pivot REQ-6). Rejects
    /// an empty <paramref name="items"/> (REQ-6.7 — an order is never opened without at least one item), an
    /// item whose <see cref="OrderItemInput.Quantity"/> isn't 1 (defense in depth — the checkout endpoint
    /// already rejects this earlier), and a NET item-total sum (gross minus discount, purchase-flow-completion
    /// REQ-7.2) that doesn't equal <paramref name="amount"/> exactly, same currency (REQ-6.3).
    /// <paramref name="orderNo"/> is required — every order has a number, minted by the caller from the
    /// platform sequence. <paramref name="customer"/> defaults to <see cref="CustomerContact.Unspecified"/>
    /// for the paths that predate REQ-6.6, which is exactly what the columns' DB DEFAULTs hold.
    /// </summary>
    public static Order Create(Guid merchantId, Money amount, DateTime createdAt, IReadOnlyList<OrderItemInput> items,
        string orderNo, Guid? paymentSessionId = null,
        string? notificationRecipient = null, string? paymentChannel = null, CustomerContact? customer = null,
        string? saleCode = null, Guid? originatorId = null,
        OrderInitiatingAudience? initiatingAudience = null, Guid? initiatingMerchantUserId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(orderNo, nameof(orderNo));
        if (items is null || items.Count == 0)
            throw new ArgumentException("An order must have at least one line.", nameof(items));

        var total = Money.Zero(amount.Currency);
        var discounts = new Money[items.Count];
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (item.Quantity <= 0)
                throw new ArgumentException("Line quantity must be positive.", nameof(items));
            if (!item.UnitPrice.SameCurrencyAs(amount))
                throw new ArgumentException("Line currency must match the order amount currency.", nameof(items));

            var gross = LineAmounts.Gross(item.UnitPrice, item.Quantity);
            discounts[i] = LineAmounts.NormaliseDiscount(item.Discount, gross);
            total = total.Add(LineAmounts.Net(gross, discounts[i]));
        }

        if (total.Amount != amount.Amount)
            throw new ArgumentException("The sum of line totals must equal the order amount.", nameof(amount));

        var order = new Order(
            Guid.NewGuid(), merchantId, orderNo.Trim(), saleCode, paymentSessionId, amount, paymentChannel,
            customer ?? CustomerContact.Unspecified, notificationRecipient, createdAt, originatorId,
            initiatingAudience, initiatingMerchantUserId);

        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            order._items.Add(new Item(
                Guid.CreateVersion7(), order.Id, merchantId, item.Quantity, item.UnitPrice, discounts[i],
                item.ProductCode, item.VariantCode, item.VariantName, item.Metadata));
        }

        return order;
    }

    /// <summary>
    /// สร้าง Order Task 5 จากราคา trusted แล้วเริ่มเป็น DRAFT. ค่า line และ adjustment มาจาก server policy
    /// เท่านั้น; factory ตรวจสูตรซ้ำเป็นด่าน domain ก่อนบันทึก.
    /// </summary>
    public static Order CreateDraft(OrderDraftInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.CreatedByAccountId == Guid.Empty)
            throw new ArgumentException("CreatedByAccountId is required.", nameof(input));
        if (input.MerchantId == Guid.Empty)
            throw new ArgumentException("MerchantId is required.", nameof(input));
        ArgumentException.ThrowIfNullOrWhiteSpace(input.OrderNo);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.BusinessType);
        var currency = Money.Zero(input.Currency).Currency;
        if (input.OrderDiscountAmount.Currency != currency
            || input.OrderChargeAmount.Currency != currency)
            throw new ArgumentException("Order adjustments must use the order currency.", nameof(input));
        if (input.Items is null || input.Items.Count == 0)
            throw new ArgumentException("An order must have at least one line.", nameof(input));
        if (input.OwnerSaleId == Guid.Empty || input.OwnerBranchId == Guid.Empty)
            throw new ArgumentException("Owner identifiers cannot be empty.", nameof(input));

        var subtotal = Money.Zero(input.Currency);
        var order = new Order(
            Guid.NewGuid(), input.MerchantId, input.OrderNo.Trim(), null, null,
            Money.Zero(currency), null, input.Customer ?? CustomerContact.Unspecified,
            input.NotificationRecipient, input.CreatedAt, null, null, null)
        {
            CreatedByAccountId = input.CreatedByAccountId,
            OwnerSaleId = input.OwnerSaleId,
            OwnerBranchIdAtCreation = input.OwnerBranchId,
            BusinessType = Required(input.BusinessType, nameof(input.BusinessType), 64),
            Metadata = input.Metadata?.ToCanonicalJson(),
            NotifyOnIssue = input.NotifyOnIssue,
            NotificationEmail = input.NotificationEmail,
            NotificationPhoneNumber = input.NotificationPhoneNumber,
            OrderDiscountAmount = input.OrderDiscountAmount,
            OrderChargeAmount = input.OrderChargeAmount,
            Status = OrderStatus.Draft,
            PaymentStatus = PaymentStatus.Unpaid,
            SummaryToken = null,
            SummaryTokenExpiresAt = null,
            IsFrozen = false,
            IssuedAt = null,
            FrozenAt = null,
            Version = 1,
        };

        for (var i = 0; i < input.Items.Count; i++)
        {
            var line = input.Items[i];
            ValidateTrustedLine(line, currency);
            subtotal = subtotal.Add(line.LineAmount);
            order._items.Add(new Item(
                Guid.CreateVersion7(), order.Id, input.MerchantId, line.Quantity, line.UnitPrice,
                line.DiscountAmount, line.TaxAmount, line.LineAmount, line.ProductCode, line.VariantCode,
                line.VariantName, line.Metadata, line.RequestMetadata));
        }

        var totalAmount = subtotal.Amount - input.OrderDiscountAmount.Amount + input.OrderChargeAmount.Amount;
        if (input.OrderDiscountAmount.Amount > subtotal.Amount)
            throw new ArgumentException("Order discount must not exceed the subtotal.", nameof(input));
        if (totalAmount < 0m)
            throw new ArgumentException("Order total cannot be negative.", nameof(input));
        order.SubtotalAmount = subtotal;
        order.Amount = Money.Of(totalAmount, input.Currency);
        return order;
    }

    /// <summary>แก้ไขได้เฉพาะ DRAFT และไม่เปลี่ยน Order หลัง freeze.</summary>
    public void PatchDraft(OrderDraftInput input, DateTime? occurredAt = null)
    {
        if (Status != OrderStatus.Draft || IsFrozen)
            throw new InvalidOperationException("Only a draft order can be patched.");
        if (input.MerchantId != MerchantId || input.CreatedByAccountId != CreatedByAccountId)
            throw new InvalidOperationException("Order ownership is immutable.");

        var replacement = CreateDraft(input with
        {
            OrderNo = OrderNo,
            Customer = Customer,
            NotificationRecipient = NotificationRecipient,
        });
        if (input.PreserveItemIdentity)
        {
            if (input.Items.Count != _items.Count)
                throw new InvalidOperationException("An omitted order item set must preserve cardinality.");
            for (var index = 0; index < _items.Count; index++)
                _items[index].ApplyTrustedSnapshot(Id, MerchantId, input.Items[index]);
        }
        else
        {
            _items.Clear();
            foreach (var replacementItem in replacement._items)
            {
                replacementItem.Reparent(Id);
                _items.Add(replacementItem);
            }
        }
        OwnerSaleId = replacement.OwnerSaleId;
        OwnerBranchIdAtCreation = replacement.OwnerBranchIdAtCreation;
        BusinessType = replacement.BusinessType;
        Metadata = replacement.Metadata;
        NotifyOnIssue = replacement.NotifyOnIssue;
        NotificationEmail = replacement.NotificationEmail;
        NotificationPhoneNumber = replacement.NotificationPhoneNumber;
        OrderDiscountAmount = replacement.OrderDiscountAmount;
        OrderChargeAmount = replacement.OrderChargeAmount;
        SubtotalAmount = replacement.SubtotalAmount;
        Amount = replacement.Amount;
        UpdatedAt = occurredAt ?? DateTime.UtcNow;
        Version++;
    }

    public void Issue(DateTime issuedAt)
    {
        if (Status != OrderStatus.Draft)
            throw new InvalidOperationException($"Order cannot be issued from status {Status}.");
        Status = OrderStatus.Open;
        IsFrozen = true;
        IssuedAt = issuedAt;
        FrozenAt = issuedAt;
        UpdatedAt = issuedAt;
        Version++;
    }

    public void RegisterLinkRotation(DateTime occurredAt)
    {
        if (Status != OrderStatus.Open || PaymentStatus == PaymentStatus.Paid)
            throw new InvalidOperationException("Only an unpaid issued order can change its payment link.");
        UpdatedAt = occurredAt;
        Version++;
    }

    public bool UsesSeparatedPaymentState => CreatedByAccountId is not null;

    public bool CanStartTransaction => Status == OrderStatus.Open && PaymentStatus != PaymentStatus.Paid;

    public void MarkPaymentProcessing(DateTime occurredAt)
    {
        if (PaymentStatus == PaymentStatus.Paid)
            throw new InvalidOperationException("A paid order cannot start another transaction.");
        if (Status != OrderStatus.Open)
            throw new InvalidOperationException($"Order cannot start payment from status {Status}.");
        if (PaymentStatus == PaymentStatus.Processing)
            return;
        PaymentStatus = PaymentStatus.Processing;
        UpdatedAt = occurredAt;
        Version++;
    }

    /// <summary>Applies a verified financial success without reopening a cancelled Order.</summary>
    public bool ApplySuccessfulTransaction(Guid transactionId, DateTime occurredAt)
    {
        if (transactionId == Guid.Empty)
            throw new ArgumentException("TransactionId is required.", nameof(transactionId));
        if (SuccessfulTransactionId is null)
            SuccessfulTransactionId = transactionId;
        var changed = PaymentStatus != PaymentStatus.Paid;
        if (!changed && SuccessfulTransactionId != transactionId)
            return false;
        PaymentStatus = PaymentStatus.Paid;
        UpdatedAt = occurredAt;
        Version++;
        return changed;
    }

    public void ReturnToUnpaidIfNoPotentialTransaction(DateTime occurredAt)
    {
        if (PaymentStatus != PaymentStatus.Processing || SuccessfulTransactionId is not null)
            return;
        PaymentStatus = PaymentStatus.Unpaid;
        UpdatedAt = occurredAt;
        Version++;
    }

    public void SetPaymentStatus(PaymentStatus status, DateTime occurredAt)
    {
        if (!Enum.IsDefined(status))
            throw new ArgumentOutOfRangeException(nameof(status));
        if (PaymentStatus == PaymentStatus.Paid && status != PaymentStatus.Paid)
            throw new InvalidOperationException("Paid payment status is terminal.");
        PaymentStatus = status;
        UpdatedAt = occurredAt;
        Version++;
    }

    /// <summary>
    /// Binds the payment session this order awaits. Legacy link with no production writer — the
    /// PaymentPaid consumer resolves orders by the event's <c>OrderId</c>, not by this value
    /// (bugfix-order-paid-link F2).
    /// </summary>
    public void AttachPaymentAttempt(Guid paymentSessionId, DateTime? occurredAt = null)
    {
        if (paymentSessionId == Guid.Empty)
            throw new ArgumentException("PaymentSessionId is required.", nameof(paymentSessionId));
        if (PaymentChannel is null)
            throw new InvalidOperationException("Order has no authoritative payment method.");

        if (Status is OrderStatus.Paid or OrderStatus.Cancelled or OrderStatus.Refunded)
            throw new InvalidOperationException(
                $"Cannot attach a payment attempt to an order in status {Status}.");

        PaymentSessionId = paymentSessionId;
        Status = OrderStatus.Pending;
        if (occurredAt is { } attachedAt)
            UpdatedAt = attachedAt;
        Version++;
    }

    /// <summary>
    /// Fulfils the order against a confirmed payment. Re-verifies amount AND currency against the
    /// order's own total (PLAN decision #2 — never trust the event's id alone). Idempotent: a
    /// second call once already <see cref="OrderStatus.Paid"/> is a no-op, so a replayed event is
    /// safe (PLAN decision #10). Returns true only on the first transition (an event was raised).
    /// </summary>
    public bool MarkPaid(Guid paymentSessionId, string method, Money paidAmount, DateTime occurredAt)
    {
        if (paymentSessionId == Guid.Empty)
            throw new ArgumentException("PaymentSessionId is required.", nameof(paymentSessionId));
        var confirmedMethod = NormalizePaymentChannel(method)
            ?? throw new ArgumentException("Payment method is required.", nameof(method));
        if (!string.Equals(PaymentChannel, confirmedMethod, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Paid method {confirmedMethod} does not match order method {PaymentChannel ?? "<missing>"}.");

        if (!paidAmount.SameCurrencyAs(Amount) || paidAmount.Amount != Amount.Amount)
            throw new InvalidOperationException(
                $"Paid amount {paidAmount} does not match order amount {Amount}.");

        if (Status == OrderStatus.Paid)
        {
            if (PaymentSessionId == paymentSessionId)
                return false;

            throw new InvalidOperationException("Order is already paid by a different payment session.");
        }

        if (Status is OrderStatus.Cancelled or OrderStatus.Refunded)
            throw new InvalidOperationException($"Cannot mark an order in status {Status} as paid.");

        if (UsesSeparatedPaymentState)
        {
            if (PaymentStatus == PaymentStatus.Paid)
                return false;
            PaymentStatus = PaymentStatus.Paid;
        }
        else
        {
            Status = OrderStatus.Paid;
            PaymentStatus = PaymentStatus.Paid;
        }
        PaymentSessionId = paymentSessionId;
        PaidAt = occurredAt;
        UpdatedAt = occurredAt;
        Version++;
        Raise(new OrderPaid(Id, occurredAt));
        return true;
    }

    private static void ValidateInitiator(
        OrderInitiatingAudience? audience, Guid? merchantUserId, Guid? originatorId)
    {
        if (merchantUserId == Guid.Empty)
            throw new ArgumentException("Initiating Merchant User id cannot be empty.", nameof(merchantUserId));
        switch (audience)
        {
            case null when merchantUserId is null:
                return; // legacy rows and migration fixtures only
            case OrderInitiatingAudience.User when merchantUserId is not null && originatorId is null:
                return;
            case OrderInitiatingAudience.PlatformAdmin when merchantUserId is null && originatorId is not null:
                return;
            default:
                throw new ArgumentException("Order initiating audience and identity are inconsistent.");
        }
    }

    private static string? NormalizePaymentChannel(string? value)
    {
        if (value is null)
            return null;
        var normalized = value.Trim().ToLowerInvariant();
        return normalized is "card" or "promptpay" or "installment"
            ? normalized
            : throw new ArgumentException("Payment method is not canonical.", nameof(value));
    }

    public bool MarkPaymentFailed(Guid paymentSessionId, DateTime? occurredAt = null)
    {
        if (Status == OrderStatus.Failed && PaymentSessionId == paymentSessionId)
            return false;
        if (Status != OrderStatus.Pending || PaymentSessionId != paymentSessionId)
            return false;

        Status = OrderStatus.Failed;
        if (occurredAt is { } failedAt)
            UpdatedAt = failedAt;
        Version++;
        return true;
    }

    public bool MarkPaymentExpired(Guid paymentSessionId, DateTime? occurredAt = null)
    {
        if (Status == OrderStatus.Expired && PaymentSessionId == paymentSessionId)
            return false;
        if (Status != OrderStatus.Pending || PaymentSessionId != paymentSessionId)
            return false;

        Status = OrderStatus.Expired;
        if (occurredAt is { } expiredAt)
            UpdatedAt = expiredAt;
        Version++;
        return true;
    }

    /// <summary>Cancels an Order only while Pending; every other status is a conflict.</summary>
    public void Cancel(DateTime? occurredAt = null)
    {
        if (PaymentStatus == PaymentStatus.Paid || Status is OrderStatus.Paid or OrderStatus.Refunded)
            throw new InvalidOperationException($"Cannot cancel an order in status {Status}.");
        if (Status is not (OrderStatus.Pending or OrderStatus.Draft or OrderStatus.Open))
            throw new InvalidOperationException($"Cannot cancel an order in status {Status}.");

        Status = OrderStatus.Cancelled;
        if (occurredAt is { } cancelledAt)
            UpdatedAt = cancelledAt;
        Version++;
    }

    private static void ValidateTrustedLine(TrustedOrderLineInput line, string currency)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(line.ProductCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(line.VariantCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(line.PriceSource);
        if (line.Quantity <= 0)
            throw new ArgumentOutOfRangeException(nameof(line.Quantity));
        if (line.UnitPrice.Currency != currency || line.DiscountAmount.Currency != currency
            || line.TaxAmount.Currency != currency || line.LineAmount.Currency != currency)
            throw new ArgumentException("Every money value must use the order currency.", nameof(line));
        var gross = LineAmounts.Gross(line.UnitPrice, line.Quantity);
        var discount = LineAmounts.NormaliseDiscount(line.DiscountAmount, gross);
        var expected = Money.Of(gross.Amount - discount.Amount + line.TaxAmount.Amount, currency);
        if (expected.Amount != line.LineAmount.Amount)
            throw new ArgumentException("Trusted line amount does not match its components.", nameof(line));
    }

    private static string Required(string value, string parameter, int maxLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameter);
        var trimmed = value.Trim();
        return trimmed.Length <= maxLength
            ? trimmed
            : throw new ArgumentException($"{parameter} exceeds {maxLength} characters.", parameter);
    }
}

public enum OrderInitiatingAudience
{
    User = 1,
    PlatformAdmin = 2,
}

public sealed record TrustedOrderLineInput(
    string ProductCode,
    string VariantCode,
    string? VariantName,
    int Quantity,
    Money UnitPrice,
    Money DiscountAmount,
    Money TaxAmount,
    Money LineAmount,
    string PriceSource,
    CommerceItemMetadata? Metadata = null,
    VersionedMetadata? RequestMetadata = null);

public sealed record OrderDraftInput(
    Guid MerchantId,
    Guid CreatedByAccountId,
    string BusinessType,
    string Currency,
    IReadOnlyList<TrustedOrderLineInput> Items,
    Money OrderDiscountAmount,
    Money OrderChargeAmount,
    Guid? OwnerSaleId,
    Guid? OwnerBranchId,
    DateTime CreatedAt,
    string OrderNo,
        CustomerContact? Customer = null,
        string? NotificationRecipient = null,
        VersionedMetadata? Metadata = null,
        bool NotifyOnIssue = false,
        string? NotificationEmail = null,
        string? NotificationPhoneNumber = null,
        bool PreserveItemIdentity = false);
