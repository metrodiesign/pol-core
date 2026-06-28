namespace Producer.Domain;

/// <summary>What the auth handler should do with a presented session token (REQ-11).</summary>
public enum ProducerSessionDecision
{
    /// <summary>Unknown / revoked / expired / superseded-but-not-immediate-predecessor-and-not-reuse — reject 401.</summary>
    Reject,

    /// <summary>Live Active session — authenticate; may rotate if past the rotation age.</summary>
    ServeActive,

    /// <summary>Immediate predecessor within the grace window — authenticate, do NOT rotate (REQ-11.2).</summary>
    ServeUnderGrace,

    /// <summary>Reuse/theft: a superseded token that is not the immediate predecessor, or past grace — revoke the
    /// whole family and reject (REQ-11.3).</summary>
    ReuseRevokeFamily,
}

/// <summary>Pure decision table over a presented session (REQ-11.1/11.2/11.3). The family's current Active session
/// id is supplied only for a Superseded token (immediate-predecessor detection); null otherwise.</summary>
// ponytail: DUPLICATE of Admin.Domain.AdminSessionDecisionPolicy — deliberate debt, do not refactor into a shared base.
public static class ProducerSessionDecisionPolicy
{
    public static ProducerSessionDecision Decide(ProducerSession session, Guid? familyActiveSessionId, DateTime now, ProducerSessionPolicy policy) =>
        session.Status switch
        {
            ProducerSessionStatus.Revoked => ProducerSessionDecision.Reject,
            ProducerSessionStatus.Active => session.IsLiveAt(now) ? ProducerSessionDecision.ServeActive : ProducerSessionDecision.Reject,
            ProducerSessionStatus.Superseded =>
                familyActiveSessionId is { } activeId && session.IsImmediatePredecessorWithinGrace(activeId, now, policy.Grace)
                    ? ProducerSessionDecision.ServeUnderGrace
                    : ProducerSessionDecision.ReuseRevokeFamily,
            _ => ProducerSessionDecision.Reject,
        };
}
