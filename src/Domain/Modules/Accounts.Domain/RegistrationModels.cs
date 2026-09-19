using System.Text.RegularExpressions;
using SharedKernel;

namespace Accounts.Domain;

public enum AgentRegistrationStatus
{
    Draft = 1,
    Pending = 2,
    Approved = 3,
    Rejected = 4,
}

public enum AgentRegistrationAttemptStatus
{
    Pending = 1,
    Approved = 2,
    Rejected = 3,
}

/// <summary>One merchant/email registration case. Identity may be bound after application.</summary>
public sealed class AgentRegistration : AggregateRoot<Guid>
{
    public Guid MerchantId { get; private set; }
    public string? Provider { get; private set; }
    public string? TenantId { get; private set; }
    public string? ExternalUserId { get; private set; }
    public string EmailNormalized { get; private set; } = default!;
    public Guid? AccountId { get; private set; }
    public string? PhoneVerifiedNumber { get; private set; }
    public DateTime? PhoneVerifiedAt { get; private set; }
    public bool PhoneVerified => PhoneVerifiedNumber is not null && PhoneVerifiedNumber == PhoneNumber;
    public Guid? CurrentAttemptId { get; private set; }
    public int CurrentAttemptNo { get; private set; }
    public AgentRegistrationStatus Status { get; private set; }
    public string SaleCode { get; private set; } = default!;
    public string Email { get; private set; } = default!;
    public string PhoneNumber { get; private set; } = default!;
    public string ProfileJson { get; private set; } = default!;
    /// <summary>Object-store key of the applicant photo (null until uploaded; required to submit).</summary>
    public string? PhotoObjectKey { get; private set; }
    public string? PhotoContentType { get; private set; }
    /// <summary>Optional KYC document photo.</summary>
    public string? KycPhotoObjectKey { get; private set; }
    public string? KycPhotoContentType { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }
    public long Version { get; private set; }

    private AgentRegistration() { }

    private AgentRegistration(Guid id, Guid merchantId, ExternalIdentity? identity, string saleCode,
        string email, string phoneNumber, string profileJson, DateTime now) : base(id)
    {
        if (merchantId == Guid.Empty)
            throw new ArgumentException("MerchantId is required.", nameof(merchantId));
        MerchantId = merchantId;
        Provider = identity?.Provider;
        TenantId = identity?.TenantId;
        ExternalUserId = identity?.ExternalUserId;
        SaleCode = Required(saleCode, nameof(saleCode), 64);
        Email = Required(email, nameof(email), 320);
        EmailNormalized = NormalizeEmail(Email);
        PhoneNumber = NormalizePhone(phoneNumber);
        ProfileJson = Required(profileJson, nameof(profileJson), 32_768);
        Status = AgentRegistrationStatus.Draft;
        CreatedAt = now;
        UpdatedAt = now;
        Version = 1;
    }

    public static AgentRegistration Create(Guid merchantId, ExternalIdentity? identity, string saleCode,
        string email, string phoneNumber, string profileJson, DateTime now) =>
        new(Guid.CreateVersion7(), merchantId, identity, saleCode, email, phoneNumber, profileJson, now);

    public ExternalIdentity? Identity => Provider is not null && TenantId is not null && ExternalUserId is not null
        ? new(Provider, TenantId, ExternalUserId) : null;

    public static string NormalizeEmail(string value) => Required(value, nameof(value), 320).ToLowerInvariant();

    public void BindIdentity(ExternalIdentity identity, DateTime now)
    {
        identity = ExternalIdentity.Create(identity.Provider, identity.TenantId, identity.ExternalUserId);
        if (Identity is { } existing)
        {
            if (existing != identity)
                throw new InvalidOperationException("The registration is already bound to another identity.");
            return;
        }
        if (Status == AgentRegistrationStatus.Draft && !PhoneVerified)
        {
            SaleCode = string.Empty;
            PhoneNumber = string.Empty;
            ProfileJson = "{}";
            PhotoObjectKey = PhotoContentType = KycPhotoObjectKey = KycPhotoContentType = null;
            PhoneVerifiedNumber = null;
            PhoneVerifiedAt = null;
        }
        Provider = identity.Provider;
        TenantId = identity.TenantId;
        ExternalUserId = identity.ExternalUserId;
        UpdatedAt = now;
        Version++;
    }

    public void MarkPhoneVerified(string number, DateTime now)
    {
        if (NormalizePhone(number) != PhoneNumber)
            throw new InvalidOperationException("The phone number has changed.");
        if (PhoneVerified)
            return;
        PhoneVerifiedNumber = number;
        PhoneVerifiedAt = now;
        UpdatedAt = now;
        Version++;
    }

    public void LinkAccount(Guid accountId)
    {
        if (accountId == Guid.Empty)
            throw new ArgumentException("AccountId is required.", nameof(accountId));
        if (AccountId is { } existing && existing != accountId)
            throw new InvalidOperationException("The registration is already linked to another account.");
        AccountId = accountId;
    }

    /// <summary>Accepts only a Thai mobile number in its exact local representation.</summary>
    public static string NormalizePhone(string value)
    {
        if (value is null || !Regex.IsMatch(value, @"\A0[689][0-9]{8}\z", RegexOptions.CultureInvariant))
            throw new ArgumentException("A Thai mobile number with exactly ten digits is required.", nameof(value));
        return value;
    }

    public void UpdateDraft(string saleCode, string email, string phoneNumber, string profileJson, DateTime now)
    {
        if (Status is AgentRegistrationStatus.Pending or AgentRegistrationStatus.Approved)
            throw new InvalidOperationException("A pending or approved registration cannot be edited.");
        var validatedPhone = NormalizePhone(phoneNumber);
        SaleCode = Required(saleCode, nameof(saleCode), 64);
        Email = Required(email, nameof(email), 320);
        EmailNormalized = NormalizeEmail(Email);
        if (PhoneNumber != validatedPhone)
        {
            PhoneVerifiedNumber = null;
            PhoneVerifiedAt = null;
        }
        PhoneNumber = validatedPhone;
        ProfileJson = Required(profileJson, nameof(profileJson), 32_768);
        Status = AgentRegistrationStatus.Draft;
        UpdatedAt = now;
        Version++;
    }

    /// <summary>Attaches the uploaded photos to the draft. Same edit gate as <see cref="UpdateDraft"/>; a null KYC
    /// pair clears the optional KYC photo.</summary>
    public void SetPhotos(string photoObjectKey, string photoContentType,
        string? kycPhotoObjectKey, string? kycPhotoContentType, DateTime now)
    {
        if (Status is AgentRegistrationStatus.Pending or AgentRegistrationStatus.Approved)
            throw new InvalidOperationException("A pending or approved registration cannot be edited.");
        PhotoObjectKey = Required(photoObjectKey, nameof(photoObjectKey), 256);
        PhotoContentType = Required(photoContentType, nameof(photoContentType), 64);
        if ((kycPhotoObjectKey is null) != (kycPhotoContentType is null))
            throw new ArgumentException("KYC photo key and content type must be supplied together.");
        KycPhotoObjectKey = kycPhotoObjectKey is null ? null : Required(kycPhotoObjectKey, nameof(kycPhotoObjectKey), 256);
        KycPhotoContentType = kycPhotoContentType is null ? null : Required(kycPhotoContentType, nameof(kycPhotoContentType), 64);
        Status = AgentRegistrationStatus.Draft;
        UpdatedAt = now;
        Version++;
    }

    public void StartAttempt(Guid attemptId, int attemptNo, DateTime now)
    {
        if (attemptId == Guid.Empty || attemptNo <= 0)
            throw new ArgumentException("Attempt identity is required.");
        if (Status is AgentRegistrationStatus.Pending or AgentRegistrationStatus.Approved)
            throw new InvalidOperationException("The registration cannot accept another attempt.");
        if (PhotoObjectKey is null)
            throw new InvalidOperationException("A photo is required before the registration can be submitted.");
        if (attemptNo != CurrentAttemptNo + 1)
            throw new InvalidOperationException("Attempt numbers must be consecutive.");
        CurrentAttemptId = attemptId;
        CurrentAttemptNo = attemptNo;
        Status = AgentRegistrationStatus.Pending;
        UpdatedAt = now;
        Version++;
    }

    public void ApplyDecision(Guid attemptId, AgentRegistrationAttemptStatus decision, DateTime now)
    {
        if (CurrentAttemptId != attemptId || Status != AgentRegistrationStatus.Pending)
            throw new InvalidOperationException("Only the current pending attempt can be decided.");
        Status = decision == AgentRegistrationAttemptStatus.Approved
            ? AgentRegistrationStatus.Approved
            : AgentRegistrationStatus.Rejected;
        UpdatedAt = now;
        Version++;
    }

    private static string Required(string value, string parameter, int maxLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameter);
        var normalized = value.Trim();
        return normalized.Length <= maxLength
            ? normalized
            : throw new ArgumentException($"{parameter} exceeds {maxLength} characters.", parameter);
    }
}

/// <summary>Submission snapshot. Form and contact fields never change after creation.</summary>
public sealed class AgentRegistrationAttempt : Entity<Guid>
{
    public Guid RegistrationId { get; private set; }
    public Guid MerchantId { get; private set; }
    public int AttemptNo { get; private set; }
    public string? Provider { get; private set; }
    public string? TenantId { get; private set; }
    public string? ExternalUserId { get; private set; }
    public string SaleCode { get; private set; } = default!;
    public Guid SaleId { get; private set; }
    public Guid BranchId { get; private set; }
    public long SaleVersion { get; private set; }
    public long BranchVersion { get; private set; }
    public string Email { get; private set; } = default!;
    public string PhoneNumber { get; private set; } = default!;
    public string ProfileJson { get; private set; } = default!;
    /// <summary>Photo snapshot taken at submit; the reviewer reads exactly what was submitted.</summary>
    public string? PhotoObjectKey { get; private set; }
    public string? PhotoContentType { get; private set; }
    public string? KycPhotoObjectKey { get; private set; }
    public string? KycPhotoContentType { get; private set; }
    public string IdempotencyKey { get; private set; } = default!;
    public string IntentHash { get; private set; } = default!;
    public AgentRegistrationAttemptStatus Status { get; private set; }
    public DateTime SubmittedAt { get; private set; }
    public DateTime? DecidedAt { get; private set; }
    public Guid? DecidedByAccountId { get; private set; }
    public string? RejectionReason { get; private set; }
    public string? InternalReviewNote { get; private set; }
    public string? ContactEvidenceReference { get; private set; }
    public Guid? ContactVerifiedByAccountId { get; private set; }
    public DateTime? ContactVerifiedAt { get; private set; }
    public string? DecisionIdempotencyKey { get; private set; }
    public string? DecisionIntentHash { get; private set; }
    public long Version { get; private set; }

    private AgentRegistrationAttempt() { }

    private AgentRegistrationAttempt(Guid id, Guid registrationId, Guid merchantId, int attemptNo,
        ExternalIdentity? identity, string saleCode, Guid saleId, Guid branchId, long saleVersion,
        long branchVersion, string email, string phoneNumber, string profileJson, string idempotencyKey,
        string intentHash, DateTime submittedAt) : base(id)
    {
        if (registrationId == Guid.Empty || merchantId == Guid.Empty || saleId == Guid.Empty || branchId == Guid.Empty)
            throw new ArgumentException("Registration, merchant, sale and branch identifiers are required.");
        if (attemptNo <= 0)
            throw new ArgumentOutOfRangeException(nameof(attemptNo));
        RegistrationId = registrationId;
        MerchantId = merchantId;
        AttemptNo = attemptNo;
        Provider = identity?.Provider;
        TenantId = identity?.TenantId;
        ExternalUserId = identity?.ExternalUserId;
        SaleCode = Required(saleCode, nameof(saleCode), 64);
        SaleId = saleId;
        BranchId = branchId;
        SaleVersion = saleVersion;
        BranchVersion = branchVersion;
        Email = Required(email, nameof(email), 320);
        PhoneNumber = AgentRegistration.NormalizePhone(phoneNumber);
        ProfileJson = Required(profileJson, nameof(profileJson), 32_768);
        IdempotencyKey = Required(idempotencyKey, nameof(idempotencyKey), 200);
        IntentHash = Required(intentHash, nameof(intentHash), 64);
        Status = AgentRegistrationAttemptStatus.Pending;
        SubmittedAt = submittedAt;
        Version = 1;
    }

    public static AgentRegistrationAttempt Create(Guid registrationId, Guid merchantId, int attemptNo,
        ExternalIdentity? identity, string saleCode, Guid saleId, Guid branchId, long saleVersion,
        long branchVersion, string email, string phoneNumber, string profileJson, string idempotencyKey,
        string intentHash, DateTime submittedAt,
        string? photoObjectKey = null, string? photoContentType = null,
        string? kycPhotoObjectKey = null, string? kycPhotoContentType = null) =>
        new(Guid.CreateVersion7(), registrationId, merchantId, attemptNo, identity, saleCode, saleId, branchId,
            saleVersion, branchVersion, email, phoneNumber, profileJson, idempotencyKey, intentHash, submittedAt)
        {
            PhotoObjectKey = photoObjectKey,
            PhotoContentType = photoContentType,
            KycPhotoObjectKey = kycPhotoObjectKey,
            KycPhotoContentType = kycPhotoContentType,
        };

    public bool MatchesIntent(string idempotencyKey, string intentHash) =>
        string.Equals(IdempotencyKey, idempotencyKey, StringComparison.Ordinal)
        && string.Equals(IntentHash, intentHash, StringComparison.Ordinal);

    public bool MatchesDecisionIntent(string idempotencyKey, string intentHash) =>
        string.Equals(DecisionIdempotencyKey, idempotencyKey, StringComparison.Ordinal)
        && string.Equals(DecisionIntentHash, intentHash, StringComparison.Ordinal);

    public bool HasDecisionIdempotencyKey(string idempotencyKey) =>
        string.Equals(DecisionIdempotencyKey, idempotencyKey, StringComparison.Ordinal);

    public void SetDecisionIdempotency(string idempotencyKey, string intentHash)
    {
        DecisionIdempotencyKey = Required(idempotencyKey, nameof(idempotencyKey), 200);
        DecisionIntentHash = Required(intentHash, nameof(intentHash), 64);
    }

    public void VerifyContact(string evidenceReference, Guid reviewerAccountId, DateTime now)
    {
        if (Status != AgentRegistrationAttemptStatus.Pending)
            throw new InvalidOperationException("Only a pending attempt can be approved.");
        ContactEvidenceReference = Required(evidenceReference, nameof(evidenceReference), 256);
        if (reviewerAccountId == Guid.Empty)
            throw new ArgumentException("Reviewer account is required.", nameof(reviewerAccountId));
        ContactVerifiedByAccountId = reviewerAccountId;
        ContactVerifiedAt = now;
        Version++;
    }

    public void Approve(Guid reviewerAccountId, string evidenceReference, DateTime now)
    {
        VerifyContact(evidenceReference, reviewerAccountId, now);
        Status = AgentRegistrationAttemptStatus.Approved;
        DecidedByAccountId = reviewerAccountId;
        DecidedAt = now;
        Version++;
    }

    public void Reject(Guid reviewerAccountId, string rejectionReason, string? internalReviewNote, DateTime now)
    {
        if (Status != AgentRegistrationAttemptStatus.Pending)
            throw new InvalidOperationException("Only a pending attempt can be rejected.");
        if (reviewerAccountId == Guid.Empty)
            throw new ArgumentException("Reviewer account is required.", nameof(reviewerAccountId));
        Status = AgentRegistrationAttemptStatus.Rejected;
        DecidedByAccountId = reviewerAccountId;
        DecidedAt = now;
        RejectionReason = Required(rejectionReason, nameof(rejectionReason), 1000);
        InternalReviewNote = string.IsNullOrWhiteSpace(internalReviewNote)
            ? null
            : Required(internalReviewNote, nameof(internalReviewNote), 4000);
        Version++;
    }

    private static string Required(string value, string parameter, int maxLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameter);
        var normalized = value.Trim();
        return normalized.Length <= maxLength
            ? normalized
            : throw new ArgumentException($"{parameter} exceeds {maxLength} characters.", parameter);
    }
}
