using Payments.Domain.Psp;
using SharedKernel;

namespace Payments.Domain;

/// <summary>
/// The immutable routing and pricing record for one potentially chargeable payment attempt. The PSP is
/// called only after this row, including its order snapshot and provider request reference, is committed.
/// </summary>
public sealed class Transaction : AggregateRoot<Guid>
{
    public Guid MerchantId { get; private set; }
    public Guid OrderId { get; private set; }
    public string TransactionNo { get; private set; } = default!;
    public int AttemptNo { get; private set; }
    public Money Amount { get; private set; }
    public string PaymentMethod { get; private set; } = default!;
    public Code Provider { get; private set; }
    public Guid ProviderAccountId { get; private set; }
    public PspEnvironment Environment { get; private set; }
    public Guid CredentialVersionId { get; private set; }
    public long ConfigurationVersion { get; private set; }
    public string ProviderRequestReference { get; private set; } = default!;
    public string? ProviderReference { get; private set; }
    public string? RedirectUrl { get; private set; }
    public string? ReturnBinding { get; private set; }
    public TransactionStatus Status { get; private set; }
    public string? ProviderStatus { get; private set; }
    public string OrderSnapshot { get; private set; } = default!;
    public string? SafeProviderMetadata { get; private set; }
    public bool NeedsReview { get; private set; }
    public string? ReviewCode { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }
    public DateTime? SucceededAt { get; private set; }
    public DateTime? LastInquiryAt { get; private set; }
    public DateTime? NextInquiryAt { get; private set; }
    public int InquiryAttempts { get; private set; }
    public long Version { get; private set; }

    /// <summary>Aliases used by provider-facing code; the stored names remain provider-neutral.</summary>
    public Guid PspConnectionId => ProviderAccountId;
    public Guid ProviderCredentialVersionId => CredentialVersionId;
    public PspEnvironment PspEnvironment => Environment;

    public bool IsPotentiallyChargeable => Status is TransactionStatus.Created or TransactionStatus.PendingConfirmation;

    /// <summary>Claims the single provider create call for a short lease so concurrent tabs do not call PSP twice.</summary>
    public bool TryClaimProviderCall(DateTime now)
    {
        if (!IsPotentiallyChargeable || RedirectUrl is not null)
            return false;
        if (string.Equals(ProviderStatus, "provider_calling", StringComparison.Ordinal)
            && now - UpdatedAt < TimeSpan.FromMinutes(1))
            return false;
        ProviderStatus = "provider_calling";
        NextInquiryAt = now.AddMinutes(1);
        UpdatedAt = now;
        Version++;
        return true;
    }

    private Transaction() { }

    private Transaction(
        Guid id,
        Guid merchantId,
        Guid orderId,
        string transactionNo,
        int attemptNo,
        Money amount,
        string paymentMethod,
        Code provider,
        Guid providerAccountId,
        PspEnvironment environment,
        Guid credentialVersionId,
        long configurationVersion,
        string providerRequestReference,
        string orderSnapshot,
        DateTime createdAt)
        : base(id)
    {
        MerchantId = merchantId;
        OrderId = orderId;
        TransactionNo = transactionNo;
        AttemptNo = attemptNo;
        Amount = amount;
        PaymentMethod = paymentMethod;
        Provider = provider;
        ProviderAccountId = providerAccountId;
        Environment = environment;
        CredentialVersionId = credentialVersionId;
        ConfigurationVersion = configurationVersion;
        ProviderRequestReference = providerRequestReference;
        OrderSnapshot = orderSnapshot;
        Status = TransactionStatus.Created;
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
        Version = 1;
    }

    public static Transaction Create(
        Guid merchantId,
        Guid orderId,
        string transactionNo,
        int attemptNo,
        Money amount,
        string paymentMethod,
        Code provider,
        Guid providerAccountId,
        PspEnvironment environment,
        Guid credentialVersionId,
        long configurationVersion,
        string providerRequestReference,
        string orderSnapshot,
        DateTime createdAt)
    {
        if (merchantId == Guid.Empty)
            throw new ArgumentException("MerchantId is required.", nameof(merchantId));
        if (orderId == Guid.Empty)
            throw new ArgumentException("OrderId is required.", nameof(orderId));
        ArgumentException.ThrowIfNullOrWhiteSpace(transactionNo);
        if (attemptNo <= 0)
            throw new ArgumentOutOfRangeException(nameof(attemptNo));
        ArgumentException.ThrowIfNullOrWhiteSpace(paymentMethod);
        if (providerAccountId == Guid.Empty)
            throw new ArgumentException("ProviderAccountId is required.", nameof(providerAccountId));
        if (credentialVersionId == Guid.Empty)
            throw new ArgumentException("CredentialVersionId is required.", nameof(credentialVersionId));
        if (configurationVersion <= 0)
            throw new ArgumentOutOfRangeException(nameof(configurationVersion));
        ArgumentException.ThrowIfNullOrWhiteSpace(providerRequestReference);
        ArgumentException.ThrowIfNullOrWhiteSpace(orderSnapshot);

        var method = paymentMethod.Trim().ToLowerInvariant();
        if (method is not ("card" or "promptpay" or "installment"))
            throw new ArgumentException("Payment method is not canonical.", nameof(paymentMethod));

        var id = Guid.CreateVersion7();
        return Create(id, merchantId, orderId, transactionNo, attemptNo, amount, paymentMethod, provider,
            providerAccountId, environment, credentialVersionId, configurationVersion, providerRequestReference,
            orderSnapshot, createdAt);
    }

    public static Transaction Create(
        Guid id,
        Guid merchantId,
        Guid orderId,
        string transactionNo,
        int attemptNo,
        Money amount,
        string paymentMethod,
        Code provider,
        Guid providerAccountId,
        PspEnvironment environment,
        Guid credentialVersionId,
        long configurationVersion,
        string providerRequestReference,
        string orderSnapshot,
        DateTime createdAt)
    {
        if (id == Guid.Empty)
            throw new ArgumentException("TransactionId is required.", nameof(id));
        if (merchantId == Guid.Empty)
            throw new ArgumentException("MerchantId is required.", nameof(merchantId));
        if (orderId == Guid.Empty)
            throw new ArgumentException("OrderId is required.", nameof(orderId));
        ArgumentException.ThrowIfNullOrWhiteSpace(transactionNo);
        if (attemptNo <= 0)
            throw new ArgumentOutOfRangeException(nameof(attemptNo));
        ArgumentException.ThrowIfNullOrWhiteSpace(paymentMethod);
        if (providerAccountId == Guid.Empty)
            throw new ArgumentException("ProviderAccountId is required.", nameof(providerAccountId));
        if (credentialVersionId == Guid.Empty)
            throw new ArgumentException("CredentialVersionId is required.", nameof(credentialVersionId));
        if (configurationVersion <= 0)
            throw new ArgumentOutOfRangeException(nameof(configurationVersion));
        ArgumentException.ThrowIfNullOrWhiteSpace(providerRequestReference);
        ArgumentException.ThrowIfNullOrWhiteSpace(orderSnapshot);

        var method = paymentMethod.Trim().ToLowerInvariant();
        if (method is not ("card" or "promptpay" or "installment"))
            throw new ArgumentException("Payment method is not canonical.", nameof(paymentMethod));

        return new Transaction(
            id, merchantId, orderId, transactionNo.Trim(), attemptNo, amount, method, provider,
            providerAccountId, environment, credentialVersionId, configurationVersion,
            providerRequestReference.Trim(), orderSnapshot, createdAt);
    }

    public void BindRedirect(string providerReference, string redirectUrl, DateTime occurredAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerReference);
        ArgumentException.ThrowIfNullOrWhiteSpace(redirectUrl);
        if (Status is TransactionStatus.Succeeded or TransactionStatus.Failed
            or TransactionStatus.Cancelled or TransactionStatus.Expired)
            throw new InvalidOperationException($"Transaction {Id} cannot bind a redirect from {Status}.");
        if (ProviderReference is { } current
            && !string.Equals(current, providerReference, StringComparison.Ordinal))
            throw new InvalidOperationException("Provider reference does not match the transaction.");

        ProviderReference = providerReference.Trim();
        RedirectUrl = redirectUrl.Trim();
        ProviderStatus = "redirect_created";
        Status = TransactionStatus.Created;
        NextInquiryAt = null;
        UpdatedAt = occurredAt;
        Version++;
    }

    public void SetReturnBinding(string binding)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(binding);
        if (ReturnBinding is not null && !string.Equals(ReturnBinding, binding, StringComparison.Ordinal))
            throw new InvalidOperationException("Transaction return binding is immutable.");
        ReturnBinding = binding;
    }

    public void MarkPendingConfirmation(
        DateTime occurredAt,
        DateTime? nextInquiryAt = null,
        string? providerStatus = null,
        string? safeMetadata = null)
    {
        if (Status is TransactionStatus.Succeeded or TransactionStatus.Failed)
            return;
        if (Status is TransactionStatus.Cancelled or TransactionStatus.Expired)
            throw new InvalidOperationException($"Transaction {Id} cannot become pending from {Status}.");

        Status = TransactionStatus.PendingConfirmation;
        ProviderStatus = Bounded(providerStatus, 128);
        SafeProviderMetadata = Bounded(safeMetadata, 2000);
        LastInquiryAt = occurredAt;
        NextInquiryAt = nextInquiryAt;
        InquiryAttempts++;
        UpdatedAt = occurredAt;
        Version++;
    }

    public void MarkFailed(string providerStatus, DateTime occurredAt, string? safeMetadata = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerStatus);
        if (Status == TransactionStatus.Succeeded)
            return;
        if (Status is TransactionStatus.Cancelled or TransactionStatus.Expired)
            return;
        if (Status == TransactionStatus.Failed)
        {
            ProviderStatus = Bounded(providerStatus, 128);
            SafeProviderMetadata = Bounded(safeMetadata, 2000);
            UpdatedAt = occurredAt;
            Version++;
            return;
        }

        Status = TransactionStatus.Failed;
        ProviderStatus = Bounded(providerStatus, 128);
        SafeProviderMetadata = Bounded(safeMetadata, 2000);
        NextInquiryAt = null;
        UpdatedAt = occurredAt;
        Version++;
    }

    /// <summary>Marks verified money as successful. Success is absorbing; callers append later evidence separately.</summary>
    public bool MarkSucceeded(
        string providerReference,
        string providerStatus,
        DateTime occurredAt,
        bool needsReview = false,
        string? reviewCode = null,
        string? safeMetadata = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerReference);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerStatus);
        if (ProviderReference is { } current
            && !string.Equals(current, providerReference, StringComparison.Ordinal))
            throw new InvalidOperationException("Provider reference does not match the transaction.");
        ProviderReference ??= providerReference.Trim();

        if (Status == TransactionStatus.Succeeded)
        {
            if (needsReview)
                NeedsReview = true;
            if (reviewCode is not null)
                ReviewCode = Bounded(reviewCode, 128);
            ProviderStatus = Bounded(providerStatus, 128);
            SafeProviderMetadata = Bounded(safeMetadata, 2000);
            UpdatedAt = occurredAt;
            Version++;
            return false;
        }

        Status = TransactionStatus.Succeeded;
        ProviderStatus = Bounded(providerStatus, 128);
        SafeProviderMetadata = Bounded(safeMetadata, 2000);
        NeedsReview |= needsReview;
        ReviewCode = reviewCode is null ? ReviewCode : Bounded(reviewCode, 128);
        SucceededAt = occurredAt;
        NextInquiryAt = null;
        UpdatedAt = occurredAt;
        Version++;
        return true;
    }

    public void FlagNeedsReview(string code, string? safeMetadata, DateTime occurredAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        NeedsReview = true;
        ReviewCode = Bounded(code, 128);
        SafeProviderMetadata = Bounded(safeMetadata, 2000);
        UpdatedAt = occurredAt;
        Version++;
    }

    public void MarkCancelled(DateTime occurredAt)
    {
        if (Status == TransactionStatus.Succeeded)
            return;
        if (!IsPotentiallyChargeable)
            return;
        Status = TransactionStatus.Cancelled;
        NextInquiryAt = null;
        UpdatedAt = occurredAt;
        Version++;
    }

    private static string? Bounded(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}
