using Admins.Application;
using Admins.Application.MasterData;
using Admins.Application.Permissions;
using Admins.Application.Roles;
using Admins.Application.Users;
using Admins.Domain.MasterData;
using Admins.Domain.Permissions;
using Admins.Domain.Roles;
using Admins.Domain.Users;

namespace Admins.Tests;

/// <summary>The pure session decision table (REQ-5): the family's current Active id is supplied only for a
/// Superseded token. These pin the branch boundaries (idle/absolute expiry, the grace edge, reuse) that the
/// auth handler composes.</summary>
public sealed class PlatformUserSessionDecisionTests
{
    private static readonly DateTime T0 = new(2026, 6, 24, 8, 0, 0, DateTimeKind.Utc);
    private static readonly SessionPolicy Policy =
        new(TimeSpan.FromMinutes(30), TimeSpan.FromHours(8), TimeSpan.FromMinutes(15), TimeSpan.FromSeconds(60));

    private static Session Active() =>
        Session.Start(Guid.NewGuid(), new byte[32], T0, Policy);

    private static (Session predecessor, Session successor) Rotated()
    {
        var predecessor = Session.Start(Guid.NewGuid(), new byte[32], T0, Policy);
        var successor = predecessor.Rotate(new byte[32], T0.AddMinutes(15), Policy);
        return (predecessor, successor);
    }

    [Fact]
    public void Live_active_serves()
    {
        Assert.Equal(SessionDecision.ServeActive,
            SessionDecisionPolicy.Decide(Active(), null, T0.AddMinutes(5), Policy));
    }

    [Fact]
    public void Active_past_idle_is_rejected()
    {
        Assert.Equal(SessionDecision.Reject,
            SessionDecisionPolicy.Decide(Active(), null, T0.AddMinutes(31), Policy));
    }

    [Fact]
    public void Active_past_absolute_is_rejected()
    {
        // slide idle far out, but the absolute cap (8h) still bites
        Assert.Equal(SessionDecision.Reject,
            SessionDecisionPolicy.Decide(Active(), null, T0.AddHours(9), Policy));
    }

    [Fact]
    public void Immediate_predecessor_within_grace_serves_under_grace()
    {
        var (predecessor, successor) = Rotated();
        Assert.Equal(SessionDecision.ServeUnderGrace,
            SessionDecisionPolicy.Decide(predecessor, successor.Id, T0.AddMinutes(15).AddSeconds(30), Policy));
    }

    [Fact]
    public void Immediate_predecessor_at_the_exact_grace_edge_still_serves()
    {
        var (predecessor, successor) = Rotated();
        // now == supersededAt + grace -> inclusive (<=)
        Assert.Equal(SessionDecision.ServeUnderGrace,
            SessionDecisionPolicy.Decide(predecessor, successor.Id, T0.AddMinutes(15).AddSeconds(60), Policy));
    }

    [Fact]
    public void Immediate_predecessor_past_grace_is_reuse()
    {
        var (predecessor, successor) = Rotated();
        Assert.Equal(SessionDecision.ReuseRevokeFamily,
            SessionDecisionPolicy.Decide(predecessor, successor.Id, T0.AddMinutes(15).AddSeconds(61), Policy));
    }

    [Fact]
    public void Superseded_but_not_the_immediate_predecessor_is_reuse()
    {
        var (predecessor, _) = Rotated();
        // the family's Active is some OTHER session (a fork / replay more than one rotation back)
        Assert.Equal(SessionDecision.ReuseRevokeFamily,
            SessionDecisionPolicy.Decide(predecessor, Guid.NewGuid(), T0.AddMinutes(15).AddSeconds(5), Policy));
    }

    [Fact]
    public void Superseded_with_no_single_active_family_member_is_reuse()
    {
        var (predecessor, _) = Rotated();
        Assert.Equal(SessionDecision.ReuseRevokeFamily,
            SessionDecisionPolicy.Decide(predecessor, familyActiveSessionId: null, T0.AddMinutes(15).AddSeconds(5), Policy));
    }

    [Fact]
    public void Revoked_is_rejected()
    {
        var session = Active();
        // Revoked is reached in production via the store's set-based UPDATE, never an entity method — set it
        // directly here to exercise the decision arm.
        typeof(Session).GetProperty(nameof(Session.Status))!.SetValue(session, SessionStatus.Revoked);

        Assert.Equal(SessionDecision.Reject,
            SessionDecisionPolicy.Decide(session, null, T0.AddMinutes(5), Policy));
    }
}
