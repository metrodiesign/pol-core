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

/// <summary>One immutable identity-scoped registration case. Attempts are the submission history.</summary>
public sealed class AgentRegistration : AggregateRoot<Guid>
{
    public Guid MerchantId { get; private set; }
    public string Provider { get; private set; } = default!;
    public string TenantId { get; private set; } = default!;
    public string ExternalUserId { get; private set; } = default!;
    public Guid? CurrentAttemptId { get; private set; }
    public int CurrentAttemptNo { get; private set; }
    public AgentRegistrationStatus Status { get; private set; }
    public string SaleCode { get; private set; } = default!;
    public string Email { get; private set; } = default!;
    public string PhoneNumber { get; private set; } = default!;
    public string ProfileJson { get; private set; } = default!;
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }
    public long Version { get; private set; }

    private AgentRegistration() { }

    private AgentRegistration(Guid id, Guid merchantId, ExternalIdentity identity, string saleCode,
        string email, string phoneNumber, string profileJson, DateTime now) : base(id)
    {
        if (merchantId == Guid.Empty)
            throw new ArgumentException("MerchantId is required.", nameof(merchantId));
        MerchantId = merchantId;
        Provider = identity.Provider;
        TenantId = identity.TenantId;
        ExternalUserId = identity.ExternalUserId;
        SaleCode = Required(saleCode, nameof(saleCode), 64);
        Email = Required(email, nameof(email), 320);
        PhoneNumber = Required(phoneNumber, nameof(phoneNumber), 64);
        ProfileJson = Required(profileJson, nameof(profileJson), 32_768);
        Status = AgentRegistrationStatus.Draft;
        CreatedAt = now;
        UpdatedAt = now;
        Version = 1;
    }

    public static AgentRegistration Create(Guid merchantId, ExternalIdentity identity, string saleCode,
        string email, string phoneNumber, string profileJson, DateTime now) =>
        new(Guid.CreateVersion7(), merchantId, identity, saleCode, email, phoneNumber, profileJson, now);

    public ExternalIdentity Identity => new(Provider, TenantId, ExternalUserId);

    public void UpdateDraft(string saleCode, string email, string phoneNumber, string profileJson, DateTime now)
    {
        if (Status is AgentRegistrationStatus.Pending or AgentRegistrationStatus.Approved)
            throw new InvalidOperationException("A pending or approved registration cannot be edited.");
        SaleCode = Required(saleCode, nameof(saleCode), 64);
        Email = Required(email, nameof(email), 320);
        PhoneNumber = Required(phoneNumber, nameof(phoneNumber), 64);
        ProfileJson = Required(profileJson, nameof(profileJson), 32_768);
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
    public string Provider { get; private set; } = default!;
    public string TenantId { get; private set; } = default!;
    public string ExternalUserId { get; private set; } = default!;
    public string SaleCode { get; private set; } = default!;
    public Guid SaleId { get; private set; }
    public Guid BranchId { get; private set; }
    public long SaleVersion { get; private set; }
    public long BranchVersion { get; private set; }
    public string Email { get; private set; } = default!;
    public string PhoneNumber { get; private set; } = default!;
    public string ProfileJson { get; private set; } = default!;
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
        ExternalIdentity identity, string saleCode, Guid saleId, Guid branchId, long saleVersion,
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
        Provider = identity.Provider;
        TenantId = identity.TenantId;
        ExternalUserId = identity.ExternalUserId;
        SaleCode = Required(saleCode, nameof(saleCode), 64);
        SaleId = saleId;
        BranchId = branchId;
        SaleVersion = saleVersion;
        BranchVersion = branchVersion;
        Email = Required(email, nameof(email), 320);
        PhoneNumber = Required(phoneNumber, nameof(phoneNumber), 64);
        ProfileJson = Required(profileJson, nameof(profileJson), 32_768);
        IdempotencyKey = Required(idempotencyKey, nameof(idempotencyKey), 200);
        IntentHash = Required(intentHash, nameof(intentHash), 64);
        Status = AgentRegistrationAttemptStatus.Pending;
        SubmittedAt = submittedAt;
        Version = 1;
    }

    public static AgentRegistrationAttempt Create(Guid registrationId, Guid merchantId, int attemptNo,
        ExternalIdentity identity, string saleCode, Guid saleId, Guid branchId, long saleVersion,
        long branchVersion, string email, string phoneNumber, string profileJson, string idempotencyKey,
        string intentHash, DateTime submittedAt) =>
        new(Guid.CreateVersion7(), registrationId, merchantId, attemptNo, identity, saleCode, saleId, branchId,
            saleVersion, branchVersion, email, phoneNumber, profileJson, idempotencyKey, intentHash, submittedAt);

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
