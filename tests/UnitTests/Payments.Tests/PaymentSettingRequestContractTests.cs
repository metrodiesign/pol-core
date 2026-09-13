using Governance.Application;
using Payments.Application.AdminControlPlane;
using Payments.Domain.Configuration;
using Payments.Domain.Psp;
using SharedKernel;

namespace Payments.Tests;

[Trait("Capability", "MerchantConfiguration")]
public sealed class PaymentSettingRequestContractTests
{
    private static readonly Guid MerchantId = Guid.Parse("f7000000-0000-4000-8000-000000000001");
    private static readonly Guid MakerId = Guid.Parse("f7000000-0000-4000-8000-000000000002");
    private static readonly Guid CheckerId = Guid.Parse("f7000000-0000-4000-8000-000000000003");
    private static readonly DateTime Now = new(2026, 9, 10, 4, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait("Requirement", "REQ-5.3")]
    [Trait("Requirement", "REQ-5.6")]
    public void Staging_keeps_active_state_out_of_the_request_and_stores_only_references()
    {
        var providerAccountId = Guid.NewGuid();
        var credentialVersionId = Guid.NewGuid();
        var request = PaymentSettingRequest.Create(
            MerchantId,
            baseVersion: 7,
            new PaymentConfigurationProposal(
                PspEnvironment.Live,
                [providerAccountId],
                [credentialVersionId],
                "{\"primaryProviderAccountId\":\"ref-only\"}"),
            MakerId,
            "rotate provider configuration",
            Now);

        Assert.Equal(PaymentSettingRequestStatus.Pending, request.Status);
        Assert.Equal(7, request.BaseVersion);
        Assert.Equal(PspEnvironment.Live, request.ProposedEnvironment);
        Assert.Contains(providerAccountId.ToString("D"), request.ProposedProviderAccountIds);
        Assert.Contains(credentialVersionId.ToString("D"), request.ProposedCredentialVersionIds);
        Assert.DoesNotContain("raw-secret", System.Text.Json.JsonSerializer.Serialize(request));
    }

    [Fact]
    [Trait("Requirement", "REQ-5.4")]
    [Trait("Requirement", "REQ-5.5")]
    public void Maker_self_decision_and_stale_base_version_are_rejected_without_mutation()
    {
        var request = NewRequest();

        Assert.Throws<InvalidOperationException>(() => request.Approve(
            MakerId, currentBaseVersion: 7, expectedVersion: 1, Now));
        Assert.Equal(PaymentSettingRequestStatus.Pending, request.Status);

        Assert.Throws<InvalidOperationException>(() => request.Approve(
            CheckerId, currentBaseVersion: 8, expectedVersion: 1, Now));
        Assert.Equal(PaymentSettingRequestStatus.Pending, request.Status);
        Assert.Null(request.CheckerId);
    }

    [Fact]
    [Trait("Requirement", "REQ-5.4")]
    [Trait("Requirement", "REQ-5.5")]
    public void Approval_then_activation_requires_the_same_base_version()
    {
        var request = NewRequest();
        request.Approve(CheckerId, currentBaseVersion: 7, expectedVersion: 1, Now);
        Assert.Equal(PaymentSettingRequestStatus.Approved, request.Status);

        Assert.Throws<InvalidOperationException>(() => request.Activate(
            currentBaseVersion: 8, expectedVersion: 2, Now));
        Assert.Equal(PaymentSettingRequestStatus.Approved, request.Status);

        request.Activate(currentBaseVersion: 7, expectedVersion: 2, Now);
        Assert.Equal(PaymentSettingRequestStatus.Activated, request.Status);
        Assert.Equal(CheckerId, request.CheckerId);
    }

    [Fact]
    [Trait("Requirement", "REQ-5.3")]
    [Trait("Requirement", "REQ-5.5")]
    public void Rejection_keeps_the_current_configuration_untouched()
    {
        var request = NewRequest();
        request.Reject(CheckerId, currentBaseVersion: 7, expectedVersion: 1, Now);

        Assert.Equal(PaymentSettingRequestStatus.Rejected, request.Status);
        Assert.Equal(CheckerId, request.CheckerId);
        Assert.Null(request.ActivatedAt);
        Assert.Throws<InvalidOperationException>(() => request.Activate(7, 2, Now));
    }

    [Fact]
    [Trait("Requirement", "REQ-5.3")]
    [Trait("Requirement", "REQ-5.6")]
    public void Typed_facade_maps_the_existing_governance_owner_without_secret_payload()
    {
        var proposal = new PaymentSettingProposal(
            PaymentSettingRequestKind.Credential,
            7,
            PspEnvironment.Live,
            [Guid.NewGuid()],
            [Guid.NewGuid()],
            "provider-account-reference");
        var approval = new ApprovalDetail(
            Guid.NewGuid(), "merchant", MerchantId, "psp.credential.change", "settings.manage",
            MakerId, "psp-credential-version", Guid.NewGuid().ToString("D"), "v7", "succeeded",
            CheckerId, "checked", Now, "psp_credentials_activated", Now, "corr", Now, 3);

        var contract = PaymentSettingRequestContract.FromApproval(approval, proposal);

        Assert.Equal(MerchantId, contract.MerchantId);
        Assert.Equal(7, contract.BaseVersion);
        Assert.Equal(MakerId, contract.MakerId);
        Assert.Equal(CheckerId, contract.CheckerId);
        Assert.Equal("succeeded", contract.Status);
        Assert.Equal(Now, contract.ActivatedAt);
        Assert.DoesNotContain("secret", System.Text.Json.JsonSerializer.Serialize(contract), StringComparison.OrdinalIgnoreCase);
    }

    private static PaymentSettingRequest NewRequest() => PaymentSettingRequest.Create(
        MerchantId,
        baseVersion: 7,
        new PaymentConfigurationProposal(
            PspEnvironment.Live,
            [Guid.NewGuid()],
            [Guid.NewGuid()],
            "{\"route\":\"provider-ref\"}"),
        MakerId,
        "change settings",
        Now);
}
