using Merchants.Domain;

namespace Merchants.Tests;

/// <summary>The pure session decision table (REQ-11.1/11.2/11.3): an Active+live token serves, an expired/revoked
/// token rejects, a superseded immediate predecessor serves within grace, and any other superseded token is
/// reuse/theft that revokes the family.</summary>
public sealed class MerchantUserSessionDecisionTests
{
    private static readonly DateTime Now = new(2026, 6, 28, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid UserId = Guid.Parse("a1111111-1111-1111-1111-111111111111");
    private static readonly MerchantUserSessionPolicy Policy =
        new(TimeSpan.FromMinutes(30), TimeSpan.FromHours(8), TimeSpan.FromMinutes(15), TimeSpan.FromSeconds(60));

    private static byte[] Hash(byte fill)
    {
        var h = new byte[32];
        Array.Fill(h, fill);
        return h;
    }

    [Fact]
    public void Active_and_live_serves()
    {
        var s = MerchantUserSession.Start(UserId, Hash(1), Now, Policy);
        Assert.Equal(MerchantUserSessionDecision.ServeActive,
            MerchantUserSessionDecisionPolicy.Decide(s, null, Now, Policy));
    }

    [Fact]
    public void Active_but_expired_rejects()
    {
        var s = MerchantUserSession.Start(UserId, Hash(1), Now, Policy);
        Assert.Equal(MerchantUserSessionDecision.Reject,
            MerchantUserSessionDecisionPolicy.Decide(s, null, Now.AddHours(9), Policy));
    }

    [Fact]
    public void Immediate_predecessor_within_grace_serves_under_grace()
    {
        var original = MerchantUserSession.Start(UserId, Hash(1), Now, Policy);
        var rotateAt = Now.AddMinutes(15);
        var successor = original.Rotate(Hash(2), rotateAt, Policy);

        Assert.Equal(MerchantUserSessionDecision.ServeUnderGrace,
            MerchantUserSessionDecisionPolicy.Decide(original, successor.Id, rotateAt.AddSeconds(30), Policy));
    }

    [Fact]
    public void Superseded_past_grace_is_reuse_revoke_family()
    {
        var original = MerchantUserSession.Start(UserId, Hash(1), Now, Policy);
        var rotateAt = Now.AddMinutes(15);
        var successor = original.Rotate(Hash(2), rotateAt, Policy);

        Assert.Equal(MerchantUserSessionDecision.ReuseRevokeFamily,
            MerchantUserSessionDecisionPolicy.Decide(original, successor.Id, rotateAt.AddSeconds(120), Policy));
    }

    [Fact]
    public void Superseded_that_is_not_the_immediate_predecessor_is_reuse_revoke_family()
    {
        var original = MerchantUserSession.Start(UserId, Hash(1), Now, Policy);
        var rotateAt = Now.AddMinutes(15);
        original.Rotate(Hash(2), rotateAt, Policy);

        // A different family-active id (replayed more than one rotation back / a fork) is reuse (REQ-11.3).
        Assert.Equal(MerchantUserSessionDecision.ReuseRevokeFamily,
            MerchantUserSessionDecisionPolicy.Decide(original, Guid.NewGuid(), rotateAt.AddSeconds(1), Policy));
    }
}
