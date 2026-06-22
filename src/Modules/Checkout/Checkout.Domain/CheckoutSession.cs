using SharedKernel;

namespace Checkout.Domain;

/// <summary>
/// A tenant's in-flight checkout for a single cart. Its own aggregate (Checkout owns no Cart/Orders
/// type — it references them only by id), holding the agreed <see cref="Amount"/> and the lifecycle
/// <see cref="Status"/>. Money is stored as two scalar columns to avoid EF friction with the
/// validating <see cref="Money"/> ctor; <see cref="Amount"/> reconstructs it.
/// </summary>
public sealed class CheckoutSession : AggregateRoot<Guid>
{
    public Guid TenantId { get; private set; }

    public Guid CartId { get; private set; }

    public long AmountMinorUnits { get; private set; }

    public string AmountCurrency { get; private set; } = default!;

    public CheckoutStatus Status { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }

    /// <summary>Where to notify the customer (email/phone), captured at checkout. Optional; flows to the
    /// order on confirm so the customer is sent the summary link.</summary>
    public string? NotificationRecipient { get; private set; }

    /// <summary>The agreed total, reconstructed from the two scalar columns.</summary>
    public Money Amount => Money.Of(AmountMinorUnits, AmountCurrency);

    private CheckoutSession(Guid id, Guid tenantId, Guid cartId, Money amount, string? notificationRecipient, DateTime createdAtUtc)
        : base(id)
    {
        TenantId = tenantId;
        CartId = cartId;
        AmountMinorUnits = amount.MinorUnits;
        AmountCurrency = amount.Currency;
        NotificationRecipient = notificationRecipient;
        Status = CheckoutStatus.Started;
        CreatedAtUtc = createdAtUtc;
    }

    /// <summary>Parameterless ctor for EF Core materialisation only.</summary>
    private CheckoutSession() { }

    /// <summary>Opens a new checkout in the <see cref="CheckoutStatus.Started"/> state.</summary>
    public static CheckoutSession Start(Guid tenantId, Guid cartId, Money amount, DateTime nowUtc, string? notificationRecipient = null)
    {
        if (tenantId == Guid.Empty)
            throw new ArgumentException("TenantId is required.", nameof(tenantId));
        if (cartId == Guid.Empty)
            throw new ArgumentException("CartId is required.", nameof(cartId));

        return new CheckoutSession(Guid.NewGuid(), tenantId, cartId, amount, notificationRecipient, nowUtc);
    }

    /// <summary>Transitions a started checkout to <see cref="CheckoutStatus.Confirmed"/>.</summary>
    public void Confirm()
    {
        if (Status != CheckoutStatus.Started)
            throw new InvalidOperationException($"Cannot confirm a checkout in state {Status}.");

        Status = CheckoutStatus.Confirmed;
    }

    /// <summary>Transitions a started checkout to <see cref="CheckoutStatus.Abandoned"/>.</summary>
    public void Abandon()
    {
        if (Status != CheckoutStatus.Started)
            throw new InvalidOperationException($"Cannot abandon a checkout in state {Status}.");

        Status = CheckoutStatus.Abandoned;
    }
}
