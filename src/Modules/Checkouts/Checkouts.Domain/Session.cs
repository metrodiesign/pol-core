using Checkouts.Domain.Items;
using SharedKernel;

namespace Checkouts.Domain;

/// <summary>
/// A merchant's in-flight checkout for a single cart. Its own aggregate (Checkout owns no Cart/Orders
/// type — it references them only by id), holding the agreed <see cref="Amount"/> and the lifecycle
/// <see cref="Status"/>. <see cref="Amount"/> is mapped as an EF complex type (rf1 — decimal(19,4) +
/// char(3) columns). <see cref="Items"/> freezes the per-line commercial + insurance-term snapshot at
/// <see cref="Start"/> (insurance-pivot REQ-6.5).
/// </summary>
public sealed class Session : AggregateRoot<Guid>
{
    private readonly List<Item> _items = [];

    public Guid MerchantId { get; private set; }

    public Guid CartId { get; private set; }

    /// <summary>The net total the customer will be charged: the sum of every line's gross minus its
    /// discount (purchase-flow-completion REQ-6.3/6.5).</summary>
    public Money Amount { get; private set; }

    public SessionStatus Status { get; private set; }

    public DateTime CreatedAt { get; private set; }

    /// <summary>The channel the merchant picked for the customer to pay through (REQ-6.1).</summary>
    public PaymentChannel Channel { get; private set; }

    /// <summary>Buyer's name — required at <see cref="Start"/> (REQ-6.6).</summary>
    public string CustomerName { get; private set; } = default!;

    /// <summary>Buyer's phone — required at <see cref="Start"/> (REQ-6.6); the primary place the summary
    /// link is sent, which is why the order derives its notification recipient from it first.</summary>
    public string CustomerPhone { get; private set; } = default!;

    /// <summary>Buyer's email — optional (REQ-6.6), the fallback notification channel.</summary>
    public string? CustomerEmail { get; private set; }

    /// <summary>The items snapshotted at <see cref="Start"/>, in insertion order.</summary>
    public IReadOnlyCollection<Item> Items => _items.AsReadOnly();

    /// <summary>The three customer columns as the value object they came in as.</summary>
    public CustomerContact Customer => CustomerContact.FromStorage(CustomerName, CustomerPhone, CustomerEmail);

    private Session(
        Guid id, Guid merchantId, Guid cartId, Money amount, PaymentChannel channel, CustomerContact customer,
        DateTime createdAt)
        : base(id)
    {
        MerchantId = merchantId;
        CartId = cartId;
        Amount = amount;
        Channel = channel;
        CustomerName = customer.Name;
        CustomerPhone = customer.Phone;
        CustomerEmail = customer.Email;
        Status = SessionStatus.Started;
        CreatedAt = createdAt;
    }

    /// <summary>Parameterless ctor for EF Core materialisation only.</summary>
    private Session() { }

    /// <summary>
    /// Opens a new checkout in the <see cref="SessionStatus.Started"/> state, snapshotting
    /// <paramref name="items"/> so nothing is re-read live between start and confirm (REQ-6.5). Rejects an
    /// empty <paramref name="items"/> (defense in depth — the endpoint already rejects an empty cart), a
    /// <paramref name="channel"/> outside the supported set (REQ-6.2), a line discount that is negative,
    /// exceeds its line, or is quoted in another currency (REQ-6.4), and an <paramref name="amount"/> that
    /// is not exactly the sum of the lines' net totals (REQ-6.5 — the server's own arithmetic is the only
    /// thing that decides what the customer pays). <paramref name="customer"/> is validated by
    /// <see cref="CustomerContact.Of"/> before it gets here (REQ-6.7).
    /// </summary>
    public static Session Start(
        Guid merchantId, Guid cartId, Money amount, DateTime nowUtc, IReadOnlyList<CheckoutItemInput> items,
        PaymentChannel channel, CustomerContact customer)
    {
        if (merchantId == Guid.Empty)
            throw new ArgumentException("MerchantId is required.", nameof(merchantId));
        if (cartId == Guid.Empty)
            throw new ArgumentException("CartId is required.", nameof(cartId));
        if (items is null || items.Count == 0)
            throw new ArgumentException("A checkout must have at least one line.", nameof(items));
        if (!Enum.IsDefined(channel))
            throw new ArgumentException("Unsupported payment channel.", nameof(channel));
        ArgumentNullException.ThrowIfNull(customer);

        var session = new Session(Guid.NewGuid(), merchantId, cartId, amount, channel, customer, nowUtc);

        var total = Money.Zero(amount.Currency);
        foreach (var item in items)
        {
            var gross = LineAmounts.Gross(item.UnitPrice, item.Quantity);
            if (!gross.SameCurrencyAs(amount))
                throw new ArgumentException("Line currency must match the checkout amount currency.", nameof(items));

            var discount = LineAmounts.NormaliseDiscount(item.Discount, gross);
            total = total.Add(LineAmounts.Net(gross, discount));

            session._items.Add(new Item(
                Guid.CreateVersion7(), session.Id, merchantId, item.ProductId, item.Quantity, item.UnitPrice, discount,
                item.DocumentNo, item.ProductGroup, item.DocumentType, item.PolicyNumber,
                item.StartDate, item.EndDate,
                item.InsuredFirstName, item.InsuredLastName, item.InsuredIdNumber, item.InsuredDateOfBirth,
                nowUtc));
        }

        if (total.Amount != amount.Amount)
            throw new ArgumentException("The sum of the lines' net totals must equal the checkout amount.", nameof(amount));

        return session;
    }

    /// <summary>Transitions a started checkout to <see cref="SessionStatus.Confirmed"/>.</summary>
    public void Confirm()
    {
        if (Status != SessionStatus.Started)
            throw new InvalidOperationException($"Cannot confirm a checkout in state {Status}.");

        Status = SessionStatus.Confirmed;
    }

    /// <summary>Transitions a started checkout to <see cref="SessionStatus.Abandoned"/>. Abandoning an
    /// already-abandoned checkout is a no-op so the merchant can retry the cancel safely (REQ-2.9); a
    /// <see cref="SessionStatus.Confirmed"/> one is past the point of no return and is rejected (REQ-2.6).</summary>
    public void Abandon()
    {
        if (Status == SessionStatus.Abandoned)
            return;

        if (Status != SessionStatus.Started)
            throw new InvalidOperationException($"Cannot abandon a checkout in state {Status}.");

        Status = SessionStatus.Abandoned;
    }
}
