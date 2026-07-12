using BuildingBlocks.Application;

namespace BuildingBlocks.Infrastructure.Persistence;

/// <summary>
/// Scoped actor holder for the keyed "admin" (pol_admin) DbContext registration — deliberately a
/// DIFFERENT instance from the request's default <see cref="IActorContext"/> (REQ-4.5): resolving the
/// admin's own identity would be chicken-and-egg if it depended on itself. The admin session middleware
/// calls <see cref="Bind"/> once it has loaded the PlatformUser, always with <c>MerchantId = Guid.Empty</c>
/// (the admin has no merchant of its own). Left unbound on legitimate admin-less paths (admin session
/// resolution itself, anonymous merchant-user registration, worker registration handlers) — those touch
/// only identity tables outside the RLS policy, so the interceptor must NOT stamp (and must NOT throw)
/// while this is unbound.
/// </summary>
public sealed class AdminActorContext : IActorContext
{
    private Guid? _platformUserId;

    public void Bind(Guid platformUserId) => _platformUserId = platformUserId;

    public Guid MerchantId => Guid.Empty;
    public Guid? UserId => _platformUserId;
    public bool HasActor => _platformUserId.HasValue;
}
