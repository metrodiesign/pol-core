using System.Security.Cryptography;
using Governance.Domain;

namespace Governance.Tests;

public sealed class GovernanceDomainTests
{
    private static readonly DateTime Now = new(2026, 8, 10, 3, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Maker_cannot_decide_own_request()
    {
        var maker = Guid.NewGuid();
        var approval = NewApproval(maker);

        var error = Assert.Throws<ApprovalRuleException>(() =>
            approval.Decide(ApprovalDecision.Approve, maker, "looks good", 1, "v7", Now));

        Assert.Equal("maker_cannot_decide", error.Code);
        Assert.Equal(ApprovalStatus.Pending, approval.Status);
    }

    [Theory]
    [InlineData(2, "v7", "approval_not_pending")]
    [InlineData(1, "v8", "target_version_changed")]
    public void Stale_decision_leaves_request_pending(long version, string targetVersion, string expectedCode)
    {
        var approval = NewApproval(Guid.NewGuid());

        var error = Assert.Throws<ApprovalRuleException>(() => approval.Decide(
            ApprovalDecision.Approve, Guid.NewGuid(), "checked", version, targetVersion, Now));

        Assert.Equal(expectedCode, error.Code);
        Assert.Equal(ApprovalStatus.Pending, approval.Status);
    }

    [Fact]
    public void Operation_record_matches_only_same_intent_and_bounds_result()
    {
        var record = OperationRecord.Create(
            Guid.NewGuid(), "ApproveRequest", "key-1", new string('a', 64),
            GovernanceScopeKind.Merchant, Guid.NewGuid(), Now, Now.AddHours(24));

        Assert.True(record.Matches(new string('a', 64)));
        Assert.False(record.Matches(new string('b', 64)));
        Assert.Throws<ArgumentException>(() => record.Complete(200, new string('x', 16_385), true, Now));
    }

    [Fact]
    public void Audit_redacts_nested_sensitive_fields_and_is_canonical()
    {
        const string first = "{\"safe\":2,\"nested\":{\"api_key\":\"raw\",\"name\":\"ok\"},\"password\":\"raw\"}";
        const string second = "{\"password\":\"different\",\"nested\":{\"name\":\"ok\",\"api_key\":\"different\"},\"safe\":2}";

        var canonical = AuditRedactor.RedactAndCanonicalize(first);

        Assert.Equal(canonical, AuditRedactor.RedactAndCanonicalize(second));
        Assert.DoesNotContain("raw", canonical, StringComparison.Ordinal);
        Assert.Equal("{\"nested\":{\"api_key\":\"[REDACTED]\",\"name\":\"ok\"},\"password\":\"[REDACTED]\",\"safe\":2}", canonical);
    }

    [Fact]
    public void Audit_chain_detects_changed_payload_and_head_mismatch()
    {
        var actor = Guid.NewGuid();
        var first = AuditRecord.Append(
            "platform", GovernanceScopeKind.Platform, null, 1, AuditRecord.Genesis, actor,
            "approval.created", "approval", "a-1", "pending", "{\"safe\":1}", null, "v1", "corr", Now);
        var second = AuditRecord.Append(
            "platform", GovernanceScopeKind.Platform, null, 2, first.Hash, actor,
            "approval.decided", "approval", "a-1", "approved", "{\"safe\":2}", Guid.NewGuid(), "v2", "corr", Now.AddSeconds(1));
        var tampered = AuditRecord.Append(
            "platform", GovernanceScopeKind.Platform, null, 2, first.Hash, actor,
            "approval.decided", "approval", "a-1", "approved", "{\"safe\":999}", second.ApprovalId, "v2", "corr", Now.AddSeconds(1));

        Assert.True(first.HasValidHash());
        Assert.True(second.HasValidHash());
        Assert.False(CryptographicOperations.FixedTimeEquals(second.Hash, tampered.Hash));

        var head = AuditHead.Create("platform", GovernanceScopeKind.Platform, null, Now);
        head.Advance(1, AuditRecord.Genesis, first.Hash, Now);
        Assert.Throws<InvalidOperationException>(() => head.Advance(3, first.Hash, second.Hash, Now));
    }

    private static ApprovalRequest NewApproval(Guid maker) => ApprovalRequest.Create(
        Guid.NewGuid(), GovernanceScopeKind.Platform, null, "routing.activate", "settings.manage", maker,
        "routing-ruleset", "rules-1", "v7", "corr", Now.AddMinutes(-1));
}
