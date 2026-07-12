namespace Merchants.Domain;

/// <summary>What the auth handler should do with a presented session token.</summary>
public enum MerchantUserSessionDecision
{
    /// <summary>Unknown / revoked / expired / superseded-but-not-immediate-predecessor-and-not-reuse — reject 401.</summary>
    Reject,

    /// <summary>Live Active session — authenticate; may rotate if past the rotation age.</summary>
    ServeActive,

    /// <summary>Immediate predecessor within the grace window — authenticate, do NOT rotate.</summary>
    ServeUnderGrace,

    /// <summary>Reuse/theft: a superseded token that is not the immediate predecessor, or past grace — revoke the
    /// whole family and reject.</summary>
    ReuseRevokeFamily,
}

/// <summary>Pure decision table over a presented session. The family's current Active session
/// id is supplied only for a Superseded token (immediate-predecessor detection); null otherwise.</summary>
// ponytail: DUPLICATE of Admins.Domain.AdminSessionDecisionPolicy — deliberate debt, do not refactor into a shared base.
public static class MerchantUserSessionDecisionPolicy
{
    public static MerchantUserSessionDecision Decide(MerchantUserSession session, Guid? familyActiveSessionId, DateTime now, MerchantUserSessionPolicy policy) =>
        session.Status switch
        {
            MerchantUserSessionStatus.Revoked => MerchantUserSessionDecision.Reject,
            MerchantUserSessionStatus.Active => session.IsLiveAt(now) ? MerchantUserSessionDecision.ServeActive : MerchantUserSessionDecision.Reject,
            MerchantUserSessionStatus.Superseded =>
                familyActiveSessionId is { } activeId && session.IsImmediatePredecessorWithinGrace(activeId, now, policy.Grace)
                    ? MerchantUserSessionDecision.ServeUnderGrace
                    : MerchantUserSessionDecision.ReuseRevokeFamily,
            _ => MerchantUserSessionDecision.Reject,
        };
}
