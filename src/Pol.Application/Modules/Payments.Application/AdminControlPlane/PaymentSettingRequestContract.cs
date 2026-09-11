using Governance.Application;
using Payments.Domain.Psp;
using SharedKernel;

namespace Payments.Application.AdminControlPlane;

public enum PaymentSettingRequestKind
{
    Credential = 1,
    Environment = 2,
    Routing = 3,
}

/// <summary>Secret-free proposed configuration references attached to the existing Governance approval.</summary>
public sealed record PaymentSettingProposal(
    PaymentSettingRequestKind Kind,
    long BaseVersion,
    PspEnvironment Environment,
    IReadOnlyList<Guid> ProviderAccountIds,
    IReadOnlyList<Guid> CredentialVersionIds,
    string RoutingReference);

/// <summary>
/// Typed facade over <see cref="ApprovalDetail"/>. Governance remains the sole approval owner/table; this
/// contract gives payment configuration callers the explicit Merchant/BaseVersion/proposal/maker-checker
/// shape without copying approval state or secret material into Payments persistence.
/// </summary>
public sealed record PaymentSettingRequestContract(
    Guid ApprovalId,
    Guid MerchantId,
    PaymentSettingRequestKind Kind,
    long BaseVersion,
    PspEnvironment ProposedEnvironment,
    IReadOnlyList<Guid> ProviderAccountIds,
    IReadOnlyList<Guid> CredentialVersionIds,
    string RoutingReference,
    Guid MakerId,
    Guid? CheckerId,
    string Status,
    string? Reason,
    DateTime CreatedAt,
    DateTime? DecidedAt,
    DateTime? ActivatedAt,
    long Version)
{
    public static PaymentSettingRequestContract Pending(
        Guid approvalId,
        Guid merchantId,
        PaymentSettingProposal proposal,
        Guid makerId,
        DateTime createdAt) => new(
        approvalId,
        merchantId,
        proposal.Kind,
        proposal.BaseVersion,
        proposal.Environment,
        proposal.ProviderAccountIds,
        proposal.CredentialVersionIds,
        proposal.RoutingReference,
        makerId,
        null,
        "pending",
        null,
        createdAt,
        null,
        null,
        Version: 1);

    public static PaymentSettingRequestContract FromApproval(
        ApprovalDetail approval,
        PaymentSettingProposal proposal)
    {
        if (approval.MerchantId is not { } merchantId || merchantId == Guid.Empty)
            throw new ArgumentException("Payment setting approvals require a merchant.", nameof(approval));
        if (approval.TargetType is not ("psp-credential-version" or "merchant-environment" or "routing-ruleset"))
            throw new ArgumentException("Approval target is not a payment setting request.", nameof(approval));
        if (proposal.BaseVersion < 1)
            throw new ArgumentOutOfRangeException(nameof(proposal), "BaseVersion must be positive.");
        if (proposal.ProviderAccountIds.Any(x => x == Guid.Empty)
            || proposal.CredentialVersionIds.Any(x => x == Guid.Empty))
            throw new ArgumentException("Payment setting references cannot be empty.", nameof(proposal));
        if (proposal.Kind != PaymentSettingRequestKind.Routing
            && proposal.ProviderAccountIds.Count != proposal.CredentialVersionIds.Count)
            throw new ArgumentException("Provider account and credential references must align.", nameof(proposal));
        ArgumentException.ThrowIfNullOrWhiteSpace(proposal.RoutingReference);

        return new PaymentSettingRequestContract(
            approval.ApprovalId,
            merchantId,
            proposal.Kind,
            proposal.BaseVersion,
            proposal.Environment,
            proposal.ProviderAccountIds,
            proposal.CredentialVersionIds,
            proposal.RoutingReference.Trim(),
            approval.MakerId,
            approval.CheckerId,
            approval.Status,
            approval.DecisionReason,
            approval.CreatedAt,
            approval.DecidedAt,
            approval.Status == "succeeded" ? approval.ExecutedAt : null,
            approval.Version);
    }
}
