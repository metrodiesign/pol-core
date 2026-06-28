using Producer.Domain;

namespace Producer.Tests;

/// <summary>
/// Domain state machine for the producer BFF session (REQ-10/11). Pure, clock-injected: Start opens a family,
/// Rotate issues a same-family successor that inherits the absolute cap, and immediate-predecessor + grace decides
/// the lag-tolerance vs reuse boundary.
/// </summary>
public sealed class ProducerSessionTests
{
    private static readonly DateTime Now = new(2026, 6, 28, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid UserId = Guid.Parse("a1111111-1111-1111-1111-111111111111");
    private static readonly ProducerSessionPolicy Policy =
        new(TimeSpan.FromMinutes(30), TimeSpan.FromHours(8), TimeSpan.FromMinutes(15), TimeSpan.FromSeconds(60));

    private static byte[] Hash(byte fill)
    {
        var h = new byte[32];
        Array.Fill(h, fill);
        return h;
    }

    [Fact]
    public void Start_opens_an_active_session_in_a_new_family()
    {
        var s = ProducerSession.Start(UserId, Hash(1), Now, Policy);

        Assert.Equal(ProducerSessionStatus.Active, s.Status);
        Assert.NotEqual(Guid.Empty, s.Id);
        Assert.NotEqual(Guid.Empty, s.FamilyId);
        Assert.Equal(UserId, s.TenantUserId);
        Assert.Equal(Now, s.IssuedAt);
        Assert.Equal(Now.AddMinutes(30), s.IdleExpiresAt);
        Assert.Equal(Now.AddHours(8), s.AbsoluteExpiresAt);
        Assert.Null(s.SupersededAt);
        Assert.Null(s.SupersededBySessionId);
        Assert.True(s.IsLiveAt(Now));
    }

    [Fact]
    public void Start_rejects_an_empty_user_or_a_wrong_sized_hash()
    {
        Assert.Throws<ArgumentException>(() => ProducerSession.Start(Guid.Empty, Hash(1), Now, Policy));
        Assert.Throws<ArgumentException>(() => ProducerSession.Start(UserId, new byte[16], Now, Policy));
    }

    [Fact]
    public void IsLiveAt_is_false_past_idle_or_absolute()
    {
        var s = ProducerSession.Start(UserId, Hash(1), Now, Policy);

        Assert.True(s.IsLiveAt(Now.AddMinutes(29)));
        Assert.False(s.IsLiveAt(Now.AddMinutes(31)));            // past idle
        var slidIdle = ProducerSession.Start(UserId, Hash(1), Now, Policy with { Idle = TimeSpan.FromHours(9) });
        Assert.False(slidIdle.IsLiveAt(Now.AddHours(8).AddMinutes(1))); // past absolute even if idle would allow
    }

    [Fact]
    public void Rotate_issues_a_same_family_successor_that_inherits_the_absolute_cap()
    {
        var original = ProducerSession.Start(UserId, Hash(1), Now, Policy);
        var rotateAt = Now.AddMinutes(15);

        var successor = original.Rotate(Hash(2), rotateAt, Policy);

        Assert.NotEqual(original.Id, successor.Id);
        Assert.Equal(original.FamilyId, successor.FamilyId);
        Assert.Equal(ProducerSessionStatus.Active, successor.Status);
        Assert.Equal(rotateAt.AddMinutes(30), successor.IdleExpiresAt);
        Assert.Equal(original.AbsoluteExpiresAt, successor.AbsoluteExpiresAt);

        Assert.Equal(ProducerSessionStatus.Superseded, original.Status);
        Assert.Equal(rotateAt, original.SupersededAt);
        Assert.Equal(successor.Id, original.SupersededBySessionId);
    }

    [Fact]
    public void Immediate_predecessor_is_accepted_within_grace_and_rejected_after()
    {
        var original = ProducerSession.Start(UserId, Hash(1), Now, Policy);
        var rotateAt = Now.AddMinutes(15);
        var successor = original.Rotate(Hash(2), rotateAt, Policy);
        var grace = TimeSpan.FromSeconds(60);

        Assert.True(original.IsImmediatePredecessorWithinGrace(successor.Id, rotateAt.AddSeconds(30), grace));
        Assert.False(original.IsImmediatePredecessorWithinGrace(successor.Id, rotateAt.AddSeconds(61), grace)); // past grace
        Assert.False(original.IsImmediatePredecessorWithinGrace(Guid.NewGuid(), rotateAt, grace));               // not the family's active
    }

    [Fact]
    public void An_active_session_is_never_an_immediate_predecessor()
    {
        var s = ProducerSession.Start(UserId, Hash(1), Now, Policy);
        Assert.False(s.IsImmediatePredecessorWithinGrace(s.Id, Now, TimeSpan.FromSeconds(60)));
    }

    [Fact]
    public void AuthAudit_allows_a_missing_user_but_requires_event_type_and_correlation()
    {
        var denied = ProducerAuthAudit.For(ProducerAuthEventType.AuthDenied, "corr-1", Now, reason: "state-mismatch");
        Assert.Null(denied.TenantUserId);
        Assert.Equal("state-mismatch", denied.Reason);

        Assert.Throws<ArgumentException>(() => ProducerAuthAudit.For("", "corr-1", Now));
        Assert.Throws<ArgumentException>(() => ProducerAuthAudit.For(ProducerAuthEventType.LoginSuccess, "  ", Now));
    }
}
