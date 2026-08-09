namespace BuildingBlocks.Application;

/// <summary>rls-to-query-filter task 5 (design.md "Pre-owner-bind READS vs WRITES"): the pre-bind session
/// lookup — resolves a session by its token hash BEFORE any actor is bound, so it cannot itself depend on
/// <see cref="IActorContext"/>. Projection-limited (this DTO, not the tracked Session aggregate) and
/// AsNoTracking by every implementation. One shared shape across the admin (ControlPlane) and merchant-user
/// (MerchantUser) realms — their <c>Session</c> AGGREGATE types stay deliberately separate (ponytail note on
/// <c>Merchants.Domain.Users.Session</c>: "do not refactor into a shared base"), but a read-only lookup
/// result carries no such coupling risk.</summary>
public interface ISessionByTokenHash
{
    Task<SessionLookup?> FindByTokenHashAsync(byte[] tokenHash, CancellationToken cancellationToken);
}

public enum SessionLookupStatus
{
    // Mirrors persisted admin.Sessions.Status and merch.Sessions.Status for direct projections.
    Active = 1,
    Superseded = 2,
    Revoked = 3,
}

public sealed record SessionLookup(
    Guid SessionId, Guid FamilyId, Guid OwnerId, SessionLookupStatus Status,
    DateTime IdleExpiresAt, DateTime AbsoluteExpiresAt, DateTime? SupersededAt, Guid? SupersededBySessionId);
