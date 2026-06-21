using SharedKernel;

namespace Payments.Domain;

/// <summary>
/// The aggregate that tracks a single redirect-only payment attempt for an order. It is bound to its
/// order, amount, currency, method and tenant up-front at <see cref="Create"/> time (PLAN #15 — no
/// attach-race), then transitions through its <see cref="PaymentStatus"/> lifecycle as the hosted PSP
/// charge is attached and confirmed. The amount is held as two scalar columns
/// (<see cref="AmountMinorUnits"/> + <see cref="AmountCurrency"/>) and exposed via the validated
/// <see cref="Amount"/> seam, per the EF mapping rule for <c>Money</c>.
/// </summary>
public sealed class PaymentSession : AggregateRoot<Guid>
{
    public Guid TenantId { get; private set; }

    public Guid OrderId { get; private set; }

    public long AmountMinorUnits { get; private set; }

    /// <summary>ISO 4217 alpha-3 code backing <see cref="Amount"/>.</summary>
    public string AmountCurrency { get; private set; } = default!;

    /// <summary>Payment method code, kept verbatim ("card"/"promptpay"/"installment").</summary>
    public string Method { get; private set; } = default!;

    public PspCode Psp { get; private set; }

    public PaymentStatus Status { get; private set; }

    /// <summary>The PSP's own charge identifier, set once a hosted charge is attached. Kept verbatim.</summary>
    public string? PspExternalChargeId { get; private set; }

    /// <summary>The hosted redirect URL the browser is sent to. Set once at attach time.</summary>
    public string? RedirectUrl { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }

    public DateTime UpdatedAtUtc { get; private set; }

    /// <summary>Optimistic-concurrency token (mapped as a SQL Server rowversion). It serialises the
    /// redirect claim so two concurrent <c>StartRedirect</c> requests cannot both create a PSP charge
    /// (PLAN #11).</summary>
    public byte[] RowVersion { get; private set; } = [];

    /// <summary>The validated money seam, reconstituted from the two scalar columns.</summary>
    public Money Amount => Money.Of(AmountMinorUnits, AmountCurrency);

    /// <summary>Parameterless ctor for EF Core materialisation only.</summary>
    private PaymentSession() { }

    private PaymentSession(
        Guid id,
        Guid tenantId,
        Guid orderId,
        Money amount,
        string method,
        PspCode psp,
        DateTime createdAtUtc)
        : base(id)
    {
        TenantId = tenantId;
        OrderId = orderId;
        AmountMinorUnits = amount.MinorUnits;
        AmountCurrency = amount.Currency;
        Method = method;
        Psp = psp;
        Status = PaymentStatus.Created;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = createdAtUtc;
    }

    /// <summary>
    /// Creates a new <see cref="PaymentStatus.Created"/> session, binding order, amount, method, PSP
    /// and tenant up-front so there is no attach-race when the charge is later created (PLAN #15).
    /// </summary>
    public static PaymentSession Create(
        Guid tenantId,
        Guid orderId,
        Money amount,
        string method,
        PspCode psp,
        DateTime createdAtUtc)
    {
        if (tenantId == Guid.Empty)
            throw new ArgumentException("TenantId is required.", nameof(tenantId));
        if (orderId == Guid.Empty)
            throw new ArgumentException("OrderId is required.", nameof(orderId));
        ArgumentException.ThrowIfNullOrWhiteSpace(method);

        return new PaymentSession(Guid.NewGuid(), tenantId, orderId, amount, method.Trim(), psp, createdAtUtc);
    }

    /// <summary>
    /// Claims the redirect: moves <see cref="PaymentStatus.Created"/> to
    /// <see cref="PaymentStatus.Redirected"/>. This is saved (under the <see cref="RowVersion"/>
    /// concurrency token) BEFORE the PSP charge is created, so only one concurrent request proceeds to
    /// call the PSP — the loser's save fails the concurrency check and never creates a charge (PLAN #11).
    /// </summary>
    public void BeginRedirect(DateTime occurredAtUtc)
    {
        if (Status != PaymentStatus.Created)
            throw new InvalidOperationException(
                $"PaymentSession {Id} cannot begin a redirect from status {Status}.");

        Status = PaymentStatus.Redirected;
        UpdatedAtUtc = occurredAtUtc;
    }

    /// <summary>
    /// Binds the hosted PSP charge to a session that has already claimed the redirect, exactly once
    /// (PLAN #11 — no double-charge). Requires <see cref="PaymentStatus.Redirected"/> and throws if a
    /// charge is already attached.
    /// </summary>
    public void SetPspCharge(string externalChargeId, string redirectUrl, DateTime occurredAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(externalChargeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(redirectUrl);

        if (Status != PaymentStatus.Redirected)
            throw new InvalidOperationException(
                $"PaymentSession {Id} cannot set a PSP charge from status {Status}.");
        if (PspExternalChargeId is not null)
            throw new InvalidOperationException(
                $"PaymentSession {Id} already has a PSP charge attached ({PspExternalChargeId}).");

        PspExternalChargeId = externalChargeId;
        RedirectUrl = redirectUrl;
        UpdatedAtUtc = occurredAtUtc;
    }

    /// <summary>
    /// Guarded transition <see cref="PaymentStatus.Created"/>/<see cref="PaymentStatus.Redirected"/>
    /// to <see cref="PaymentStatus.Paid"/>. Idempotent: a repeat call with the same external charge id
    /// when already <see cref="PaymentStatus.Paid"/> is a no-op (the webhook path can re-confirm).
    /// </summary>
    public void MarkPaid(string externalChargeId, DateTime occurredAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(externalChargeId);

        if (Status == PaymentStatus.Paid)
        {
            if (!string.Equals(PspExternalChargeId, externalChargeId, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"PaymentSession {Id} is already Paid under a different charge ({PspExternalChargeId}).");
            return;
        }

        if (Status is not (PaymentStatus.Created or PaymentStatus.Redirected))
            throw new InvalidOperationException(
                $"PaymentSession {Id} cannot be marked Paid from status {Status}.");

        if (PspExternalChargeId is null)
            PspExternalChargeId = externalChargeId;
        else if (!string.Equals(PspExternalChargeId, externalChargeId, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"PaymentSession {Id} charge mismatch: attached {PspExternalChargeId}, confirmed {externalChargeId}.");

        Status = PaymentStatus.Paid;
        UpdatedAtUtc = occurredAtUtc;
    }

    /// <summary>Guarded transition to <see cref="PaymentStatus.Failed"/> from a non-terminal state.</summary>
    public void MarkFailed(string reason, DateTime occurredAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        if (Status is PaymentStatus.Paid or PaymentStatus.Failed or PaymentStatus.Expired)
            throw new InvalidOperationException(
                $"PaymentSession {Id} cannot be marked Failed from terminal status {Status}.");

        Status = PaymentStatus.Failed;
        UpdatedAtUtc = occurredAtUtc;
    }

    /// <summary>Guarded transition to <see cref="PaymentStatus.Expired"/> from a non-terminal state.</summary>
    public void MarkExpired(DateTime occurredAtUtc)
    {
        if (Status is PaymentStatus.Paid or PaymentStatus.Failed or PaymentStatus.Expired)
            throw new InvalidOperationException(
                $"PaymentSession {Id} cannot be marked Expired from terminal status {Status}.");

        Status = PaymentStatus.Expired;
        UpdatedAtUtc = occurredAtUtc;
    }
}
