namespace Admins.Domain.Users;

/// <summary>What the auth handler should do with a presented session token (REQ-5).</summary>
public enum SessionDecision
{
    /// <summary>Unknown / revoked / expired / superseded-but-not-immediate-predecessor-and-not-reuse — reject 401.</summary>
    Reject,

    /// <summary>Live Active session — authenticate; may rotate if past the rotation age.</summary>
    ServeActive,

    /// <summary>Immediate predecessor within the grace window — authenticate, do NOT rotate (REQ-5.2).</summary>
    ServeUnderGrace,

    /// <summary>Reuse/theft: a superseded token that is not the immediate predecessor, or past grace — revoke the
    /// whole family and reject (REQ-5.3).</summary>
    ReuseRevokeFamily,
}

/// <summary>Pure decision table over a presented session (REQ-4.2/5.2/5.3/5.4). The family's current Active
/// session id is supplied only for a Superseded token (immediate-predecessor detection); null otherwise.</summary>
public static class SessionDecisionPolicy
{
    public static SessionDecision Decide(Session session, Guid? familyActiveSessionId, DateTime now, SessionPolicy policy) =>
        session.Status switch
        {
            SessionStatus.Revoked => SessionDecision.Reject,
            SessionStatus.Active => session.IsLiveAt(now) ? SessionDecision.ServeActive : SessionDecision.Reject,
            SessionStatus.Superseded =>
                familyActiveSessionId is { } activeId && session.IsImmediatePredecessorWithinGrace(activeId, now, policy.Grace)
                    ? SessionDecision.ServeUnderGrace
                    : SessionDecision.ReuseRevokeFamily,
            _ => SessionDecision.Reject,
        };
}
