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

    /// <summary>The upstream sale code of the merchant user bound to this request — the catalogue search runs
    /// under this code and a client can never choose it (products-external-source-of-truth REQ-4.8); null when
    /// the actor has none (or is not a merchant user at all), which the catalogue path answers with 403
    /// (REQ-4.9). A default member on purpose: only the HTTP actor resolves a real value, and every worker /
    /// webhook / test binding stays valid unchanged.</summary>
    string? SaleCode => null;
}
