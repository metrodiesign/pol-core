using SharedKernel;

namespace Checkouts.Domain;

/// <summary>
/// A merchant's in-flight checkout for a single cart. Its own aggregate (Checkout owns no Cart/Orders
/// type — it references them only by id), holding the agreed <see cref="Amount"/> and the lifecycle
/// <see cref="Status"/>. <see cref="Amount"/> is mapped as an EF complex type (rf1 — decimal(19,4) +
/// char(3) columns).
/// </summary>
public sealed class Session : AggregateRoot<Guid>
{
    public Guid MerchantId { get; private set; }

    public Guid CartId { get; private set; }

    public Money Amount { get; private set; }

    public SessionStatus Status { get; private set; }

    public DateTime CreatedAt { get; private set; }

    /// <summary>Where to notify the customer (email/phone), captured at checkout. Optional; flows to the
    /// order on confirm so the customer is sent the summary link.</summary>
    public string? NotificationRecipient { get; private set; }

    private Session(Guid id, Guid merchantId, Guid cartId, Money amount, string? notificationRecipient, DateTime createdAt)
        : base(id)
    {
        MerchantId = merchantId;
        CartId = cartId;
        Amount = amount;
        NotificationRecipient = notificationRecipient;
        Status = SessionStatus.Started;
        CreatedAt = createdAt;
    }

    /// <summary>Parameterless ctor for EF Core materialisation only.</summary>
    private Session() { }

    /// <summary>Opens a new checkout in the <see cref="SessionStatus.Started"/> state.</summary>
    public static Session Start(Guid merchantId, Guid cartId, Money amount, DateTime nowUtc, string? notificationRecipient = null)
    {
        if (merchantId == Guid.Empty)
            throw new ArgumentException("MerchantId is required.", nameof(merchantId));
        if (cartId == Guid.Empty)
            throw new ArgumentException("CartId is required.", nameof(cartId));

        return new Session(Guid.NewGuid(), merchantId, cartId, amount, notificationRecipient, nowUtc);
    }

    /// <summary>Transitions a started checkout to <see cref="SessionStatus.Confirmed"/>.</summary>
    public void Confirm()
    {
        if (Status != SessionStatus.Started)
            throw new InvalidOperationException($"Cannot confirm a checkout in state {Status}.");

        Status = SessionStatus.Confirmed;
    }

    /// <summary>Transitions a started checkout to <see cref="SessionStatus.Abandoned"/>.</summary>
    public void Abandon()
    {
        if (Status != SessionStatus.Started)
            throw new InvalidOperationException($"Cannot abandon a checkout in state {Status}.");

        Status = SessionStatus.Abandoned;
    }
}
