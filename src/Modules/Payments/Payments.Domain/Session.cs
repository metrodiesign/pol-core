using Payments.Domain.Psp;
using SharedKernel;

namespace Payments.Domain;

/// <summary>
/// The aggregate that tracks a single redirect-only payment attempt for an order. It is bound to its
/// order, amount, currency, method and merchant up-front at <see cref="Create"/> time (PLAN #15 — no
/// attach-race), then transitions through its <see cref="SessionStatus"/> lifecycle as the hosted PSP
/// charge is attached and confirmed. <see cref="Amount"/> is mapped as an EF complex type (rf1 —
/// decimal(19,4) + char(3) columns).
/// </summary>
public sealed class Session : AggregateRoot<Guid>
{
    public Guid MerchantId { get; private set; }

    public Guid OrderId { get; private set; }

    public Money Amount { get; private set; }

    /// <summary>Payment method code, kept verbatim ("card"/"promptpay"/"installment").</summary>
    public string Method { get; private set; } = default!;

    public Code Psp { get; private set; }

    public SessionStatus Status { get; private set; }

    /// <summary>The PSP's own charge identifier, set once a hosted charge is attached. Kept verbatim.</summary>
    public string? PspExternalChargeId { get; private set; }

    /// <summary>The hosted redirect URL the browser is sent to. Set once at attach time.</summary>
    public string? RedirectUrl { get; private set; }

    public DateTime CreatedAt { get; private set; }

    public DateTime UpdatedAt { get; private set; }

    /// <summary>Optimistic-concurrency token (mapped as a SQL Server rowversion). It serialises the
    /// redirect claim so two concurrent <c>StartRedirect</c> requests cannot both create a PSP charge
    /// (PLAN #11).</summary>
    public byte[] RowVersion { get; private set; } = [];

    /// <summary>Parameterless ctor for EF Core materialisation only.</summary>
    private Session() { }

    private Session(
        Guid id,
        Guid merchantId,
        Guid orderId,
        Money amount,
        string method,
        Code psp,
        DateTime createdAt)
        : base(id)
    {
        MerchantId = merchantId;
        OrderId = orderId;
        Amount = amount;
        Method = method;
        Psp = psp;
        Status = SessionStatus.Created;
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
    }

    /// <summary>
    /// Creates a new <see cref="SessionStatus.Created"/> session, binding order, amount, method, PSP
    /// and merchant up-front so there is no attach-race when the charge is later created (PLAN #15).
    /// </summary>
    public static Session Create(
        Guid merchantId,
        Guid orderId,
        Money amount,
        string method,
        Code psp,
        DateTime createdAt)
    {
        if (merchantId == Guid.Empty)
            throw new ArgumentException("MerchantId is required.", nameof(merchantId));
        if (orderId == Guid.Empty)
            throw new ArgumentException("OrderId is required.", nameof(orderId));
        ArgumentException.ThrowIfNullOrWhiteSpace(method);

        return new Session(Guid.NewGuid(), merchantId, orderId, amount, method.Trim(), psp, createdAt);
    }

    /// <summary>
    /// Claims the redirect: moves <see cref="SessionStatus.Created"/> to
    /// <see cref="SessionStatus.Redirected"/>. This is saved (under the <see cref="RowVersion"/>
    /// concurrency token) BEFORE the PSP charge is created, so only one concurrent request proceeds to
    /// call the PSP — the loser's save fails the concurrency check and never creates a charge (PLAN #11).
    /// </summary>
    public void BeginRedirect(DateTime occurredAt)
    {
        if (Status != SessionStatus.Created)
            throw new InvalidOperationException(
                $"PaymentSession {Id} cannot begin a redirect from status {Status}.");

        Status = SessionStatus.Redirected;
        UpdatedAt = occurredAt;
    }

    /// <summary>
    /// Binds the hosted PSP charge to a session that has already claimed the redirect, exactly once
    /// (PLAN #11 — no double-charge). Requires <see cref="SessionStatus.Redirected"/> and throws if a
    /// charge is already attached.
    /// </summary>
    public void SetPspCharge(string externalChargeId, string redirectUrl, DateTime occurredAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(externalChargeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(redirectUrl);

        if (Status != SessionStatus.Redirected)
            throw new InvalidOperationException(
                $"PaymentSession {Id} cannot set a PSP charge from status {Status}.");
        if (PspExternalChargeId is not null)
            throw new InvalidOperationException(
                $"PaymentSession {Id} already has a PSP charge attached ({PspExternalChargeId}).");

        PspExternalChargeId = externalChargeId;
        RedirectUrl = redirectUrl;
        UpdatedAt = occurredAt;
    }

    /// <summary>
    /// Guarded transition <see cref="SessionStatus.Created"/>/<see cref="SessionStatus.Redirected"/>
    /// to <see cref="SessionStatus.Paid"/>. Idempotent: a repeat call with the same external charge id
    /// when already <see cref="SessionStatus.Paid"/> is a no-op (the webhook path can re-confirm).
    /// </summary>
    public void MarkPaid(string externalChargeId, DateTime occurredAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(externalChargeId);

        if (Status == SessionStatus.Paid)
        {
            if (!string.Equals(PspExternalChargeId, externalChargeId, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"PaymentSession {Id} is already Paid under a different charge ({PspExternalChargeId}).");
            return;
        }

        if (Status is not (SessionStatus.Created or SessionStatus.Redirected))
            throw new InvalidOperationException(
                $"PaymentSession {Id} cannot be marked Paid from status {Status}.");

        if (PspExternalChargeId is null)
            PspExternalChargeId = externalChargeId;
        else if (!string.Equals(PspExternalChargeId, externalChargeId, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"PaymentSession {Id} charge mismatch: attached {PspExternalChargeId}, confirmed {externalChargeId}.");

        Status = SessionStatus.Paid;
        UpdatedAt = occurredAt;
    }

    /// <summary>Guarded transition to <see cref="SessionStatus.Failed"/> from a non-terminal state.</summary>
    public void MarkFailed(string reason, DateTime occurredAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        if (Status is SessionStatus.Paid or SessionStatus.Failed or SessionStatus.Expired)
            throw new InvalidOperationException(
                $"PaymentSession {Id} cannot be marked Failed from terminal status {Status}.");

        Status = SessionStatus.Failed;
        UpdatedAt = occurredAt;
    }

    /// <summary>Guarded transition to <see cref="SessionStatus.Expired"/> from a non-terminal state.</summary>
    public void MarkExpired(DateTime occurredAt)
    {
        if (Status is SessionStatus.Paid or SessionStatus.Failed or SessionStatus.Expired)
            throw new InvalidOperationException(
                $"PaymentSession {Id} cannot be marked Expired from terminal status {Status}.");

        Status = SessionStatus.Expired;
        UpdatedAt = occurredAt;
    }
}
