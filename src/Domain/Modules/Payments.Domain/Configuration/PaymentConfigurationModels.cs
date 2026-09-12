using Payments.Domain.Psp;
using SharedKernel;

namespace Payments.Domain.Configuration;

public enum PaymentSettingRequestStatus
{
    Pending = 1,
    Approved = 2,
    Rejected = 3,
    Activated = 4,
}

/// <summary>Secret-free proposed routing state. Credential values stay in the protected store.</summary>
public sealed record PaymentConfigurationProposal(
    PspEnvironment Environment,
    IReadOnlyList<Guid> ProviderAccountIds,
    IReadOnlyList<Guid> CredentialVersionIds,
    string RoutingJson)
{
    public PaymentConfigurationProposal Validate()
    {
        if (!Enum.IsDefined(Environment))
            throw new ArgumentOutOfRangeException(nameof(Environment));
        if (ProviderAccountIds is null || CredentialVersionIds is null)
            throw new ArgumentNullException(nameof(ProviderAccountIds));
        if (ProviderAccountIds.Count != CredentialVersionIds.Count)
            throw new ArgumentException("Provider accounts and credential versions must have the same count.");
        if (ProviderAccountIds.Any(x => x == Guid.Empty) || CredentialVersionIds.Any(x => x == Guid.Empty))
            throw new ArgumentException("Configuration identifiers cannot be empty.");
        ArgumentException.ThrowIfNullOrWhiteSpace(RoutingJson);
        return this;
    }
}

/// <summary>
/// Durable maker-checker state for a merchant payment configuration change. The existing Governance
/// projection carries this state across the control-plane transaction; this owner model defines the
/// invariant once so every writer applies the same BaseVersion and maker/checker rules.
/// </summary>
public sealed class PaymentSettingRequest : AggregateRoot<Guid>
{
    public Guid MerchantId { get; private set; }
    public long BaseVersion { get; private set; }
    public PspEnvironment ProposedEnvironment { get; private set; }
    public string ProposedProviderAccountIds { get; private set; } = default!;
    public string ProposedCredentialVersionIds { get; private set; } = default!;
    public string ProposedConfiguration { get; private set; } = default!;
    public Guid MakerId { get; private set; }
    public Guid? CheckerId { get; private set; }
    public PaymentSettingRequestStatus Status { get; private set; }
    public string Reason { get; private set; } = default!;
    public DateTime CreatedAt { get; private set; }
    public DateTime? DecidedAt { get; private set; }
    public DateTime? ActivatedAt { get; private set; }
    public long Version { get; private set; }

    private PaymentSettingRequest() { }

    public static PaymentSettingRequest Create(
        Guid merchantId,
        long baseVersion,
        PaymentConfigurationProposal proposal,
        Guid makerId,
        string reason,
        DateTime now)
    {
        RequireId(merchantId, nameof(merchantId));
        RequireId(makerId, nameof(makerId));
        if (baseVersion < 1)
            throw new ArgumentOutOfRangeException(nameof(baseVersion));
        proposal.Validate();
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new PaymentSettingRequest
        {
            Id = Guid.CreateVersion7(),
            MerchantId = merchantId,
            BaseVersion = baseVersion,
            ProposedEnvironment = proposal.Environment,
            ProposedProviderAccountIds = JoinIds(proposal.ProviderAccountIds),
            ProposedCredentialVersionIds = JoinIds(proposal.CredentialVersionIds),
            ProposedConfiguration = proposal.RoutingJson.Trim(),
            MakerId = makerId,
            Status = PaymentSettingRequestStatus.Pending,
            Reason = reason.Trim(),
            CreatedAt = now,
            Version = 1,
        };
    }

    public void Approve(Guid checkerId, long currentBaseVersion, long expectedVersion, DateTime now)
    {
        EnsurePending(expectedVersion);
        RequireId(checkerId, nameof(checkerId));
        if (checkerId == MakerId)
            throw new InvalidOperationException("The maker cannot decide this payment setting request.");
        EnsureBaseVersion(currentBaseVersion);
        CheckerId = checkerId;
        DecidedAt = now;
        Status = PaymentSettingRequestStatus.Approved;
        Version++;
    }

    public void Reject(Guid checkerId, long currentBaseVersion, long expectedVersion, DateTime now)
    {
        EnsurePending(expectedVersion);
        RequireId(checkerId, nameof(checkerId));
        if (checkerId == MakerId)
            throw new InvalidOperationException("The maker cannot decide this payment setting request.");
        EnsureBaseVersion(currentBaseVersion);
        CheckerId = checkerId;
        DecidedAt = now;
        Status = PaymentSettingRequestStatus.Rejected;
        Version++;
    }

    /// <summary>Marks activation only after the same BaseVersion is still current.</summary>
    public void Activate(long currentBaseVersion, long expectedVersion, DateTime now)
    {
        if (Status != PaymentSettingRequestStatus.Approved)
            throw new InvalidOperationException("Only an approved payment setting request can activate.");
        if (expectedVersion != Version)
            throw new InvalidOperationException("Payment setting request version is stale.");
        EnsureBaseVersion(currentBaseVersion);
        Status = PaymentSettingRequestStatus.Activated;
        ActivatedAt = now;
        Version++;
    }

    private void EnsurePending(long expectedVersion)
    {
        if (Status != PaymentSettingRequestStatus.Pending)
            throw new InvalidOperationException("Payment setting request is no longer pending.");
        if (expectedVersion != Version)
            throw new InvalidOperationException("Payment setting request version is stale.");
    }

    private void EnsureBaseVersion(long currentBaseVersion)
    {
        if (currentBaseVersion != BaseVersion)
            throw new InvalidOperationException("Payment setting BaseVersion is stale.");
    }

    private static void RequireId(Guid value, string parameter)
    {
        if (value == Guid.Empty)
            throw new ArgumentException("Identifier is required.", parameter);
    }

    private static string JoinIds(IReadOnlyList<Guid> ids) =>
        string.Join(',', ids.Select(x => x.ToString("D")));
}

public enum PaymentEligibilityDenial
{
    None = 0,
    BusinessPolicy = 1,
    Merchant = 2,
    Creator = 3,
    ProviderAccount = 4,
    AdapterCapability = 5,
    ProviderContractEvidence = 6,
    EmergencyDisabled = 7,
    Amount = 8,
    Currency = 9,
}

public sealed record PaymentEligibilityRequest(
    string Method,
    Money Amount,
    IReadOnlySet<string> AllowedCurrencies,
    decimal? MinimumAmount,
    decimal? MaximumAmount,
    bool BusinessPolicyEnabled,
    bool MerchantEnabled,
    bool CreatorEnabled,
    bool ProviderAccountEnabled,
    bool AdapterCapabilityVerified,
    bool ProviderContractEvidence,
    bool EmergencyDisabled);

public sealed record PaymentEligibilityDecision(bool Allowed, PaymentEligibilityDenial Denial)
{
    public static PaymentEligibilityDecision Allow() => new(true, PaymentEligibilityDenial.None);
    public static PaymentEligibilityDecision Deny(PaymentEligibilityDenial denial) => new(false, denial);
}

/// <summary>Pure intersection gate used before a new payment start. Existing transaction inquiry bypasses it.</summary>
public static class PaymentEligibilityPolicy
{
    public static PaymentEligibilityDecision Evaluate(PaymentEligibilityRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        _ = PaymentMethods.Normalize(request.Method);

        if (!request.BusinessPolicyEnabled)
            return PaymentEligibilityDecision.Deny(PaymentEligibilityDenial.BusinessPolicy);
        if (!request.MerchantEnabled)
            return PaymentEligibilityDecision.Deny(PaymentEligibilityDenial.Merchant);
        if (!request.CreatorEnabled)
            return PaymentEligibilityDecision.Deny(PaymentEligibilityDenial.Creator);
        if (!request.ProviderAccountEnabled)
            return PaymentEligibilityDecision.Deny(PaymentEligibilityDenial.ProviderAccount);
        if (request.EmergencyDisabled)
            return PaymentEligibilityDecision.Deny(PaymentEligibilityDenial.EmergencyDisabled);
        if (!request.AdapterCapabilityVerified)
            return PaymentEligibilityDecision.Deny(PaymentEligibilityDenial.AdapterCapability);
        if (!request.ProviderContractEvidence)
            return PaymentEligibilityDecision.Deny(PaymentEligibilityDenial.ProviderContractEvidence);
        if (!request.AllowedCurrencies.Contains(request.Amount.Currency, StringComparer.OrdinalIgnoreCase))
            return PaymentEligibilityDecision.Deny(PaymentEligibilityDenial.Currency);
        if (request.MinimumAmount is { } min && request.Amount.Amount < min
            || request.MaximumAmount is { } max && request.Amount.Amount > max)
            return PaymentEligibilityDecision.Deny(PaymentEligibilityDenial.Amount);
        return PaymentEligibilityDecision.Allow();
    }
}
