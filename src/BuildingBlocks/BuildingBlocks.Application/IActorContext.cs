namespace BuildingBlocks.Application;

/// <summary>
/// Ambient per-request actor identity (merchant + optional user). Resolved at the host edge from the
/// authenticated principal (never from a URL path before signature verification — PLAN decision #4) and
/// used by the data layer to set <c>SESSION_CONTEXT('MerchantId'/'UserId')</c> at connection open
/// (PLAN decision #3). Registered Scoped; anything depending on it must also be Scoped.
/// </summary>
public interface IActorContext
{
    /// <summary>The active merchant. Throws if <see cref="HasActor"/> is false.</summary>
    Guid MerchantId { get; }

    /// <summary>The active user, if any — a session-authenticated actor always carries one; a
    /// merchant-less worker/webhook bind may leave it null.</summary>
    Guid? UserId { get; }

    /// <summary>True when a concrete actor is bound to this request.</summary>
    bool HasActor { get; }
}
