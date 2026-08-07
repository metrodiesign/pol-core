using Admins.Application.Roles;
using Admins.Domain.Users;
using BuildingBlocks.Application;
using Mediator;

namespace Admins.Application.Users;

/// <summary>Read models for the admin-account-management console (admin-account-management REQ-1/2/6). Read-only —
/// no transaction. The admin directory is an SFS list: it inherits <see cref="PagedQuery"/> and returns a
/// <see cref="PagedResult{T}"/>. Control-plane data (no MerchantId) -> NOT <c>IMerchantScoped</c>.</summary>
public sealed record ListAdminsQuery : PagedQuery, IQuery<PagedResult<UserListItem>>;

public sealed class ListAdminsHandler(IUserRepository admins)
    : IQueryHandler<ListAdminsQuery, PagedResult<UserListItem>>
{
    public async ValueTask<PagedResult<UserListItem>> Handle(ListAdminsQuery query, CancellationToken ct) =>
        await admins.ListAsync(query, ct);
}

/// <summary>Full detail for one admin (REQ-2): list fields + the accessible-merchant set (mirroring
/// <c>GET /admins/me</c>) + every assigned role code incl. Inactive roles. Null result -> the host maps to 404.</summary>
public sealed record GetAdminByIdQuery(Guid AdminId) : IQuery<Detail?>;

public sealed record Detail(
    Guid AdminId, string Email, Tier Tier, UserStatus Status, DateTime CreatedAt,
    bool SubjectBound, AccessibleMerchants Accessible, IReadOnlyList<string> RoleCodes,
    ProfileRef? Position, ProfileRef? Office, ProfileRef? Level, ProfileRef? Division);

public sealed class GetAdminByIdHandler(IUserRepository admins, IRoleRepository roles, IProfileLookup masters)
    : IQueryHandler<GetAdminByIdQuery, Detail?>
{
    public async ValueTask<Detail?> Handle(GetAdminByIdQuery query, CancellationToken ct)
    {
        var account = await admins.GetByIdAsync(query.AdminId, ct);
        if (account is null)
            return null;   // -> 404 (REQ-2.2)

        // Reuse the canonical accessible-set rule the sign-in pipeline uses (REQ-2.1); host maps ids -> codes.
        var accessible = await ResolveHandler.ResolveAccessibleAsync(account, admins, ct);
        var roleCodes = await roles.ListRoleCodesForAdminAsync(account.Id, ct);

        // Resolve each set org-profile FK to its display reference (null when unset).
        var position = account.PositionId is { } pid ? await masters.GetRefAsync(ProfileField.Position, pid, ct) : null;
        var office = account.OfficeId is { } oid ? await masters.GetRefAsync(ProfileField.Office, oid, ct) : null;
        var level = account.LevelId is { } lid ? await masters.GetRefAsync(ProfileField.Level, lid, ct) : null;
        var division = account.DivisionId is { } did ? await masters.GetRefAsync(ProfileField.Division, did, ct) : null;

        return new Detail(
            account.Id, account.Email, account.Tier, account.Status, account.CreatedAt,
            account.Subject is not null, accessible, roleCodes,
            position, office, level, division);
    }
}

/// <summary>The distinct union of permission keys granted through the admin's ACTIVE roles (REQ-6) — the same
/// rule as <c>GET /admins/me</c>. Existence is resolved first because the repo returns an empty set (not null)
/// for an unknown id, so an empty set alone cannot mean "not found" (REQ-6.3). Null result -> the host maps to
/// 404; the keys are ordinal-ascending so the response is deterministic (REQ-6.2). Works for a Suspended target
/// (REQ-6.4 — suspension blocks sign-in, not role grants).</summary>
public sealed record GetEffectivePermissionsQuery(Guid AdminId) : IQuery<IReadOnlyList<string>?>;

public sealed class GetEffectivePermissionsHandler(IUserRepository admins, IRoleRepository roles)
    : IQueryHandler<GetEffectivePermissionsQuery, IReadOnlyList<string>?>
{
    public async ValueTask<IReadOnlyList<string>?> Handle(GetEffectivePermissionsQuery query, CancellationToken ct)
    {
        if (!await admins.ExistsAsync(query.AdminId, ct))
            return null;   // -> 404 (REQ-6.3); existence-only, no need to load the entity
        var keys = await roles.ListEffectivePermissionsAsync(query.AdminId, ct);
        return [.. keys.OrderBy(k => k, StringComparer.Ordinal)];   // deterministic ascending (REQ-6.2)
    }
}

/// <summary>An admin's sessions for the session-management view (REQ-4). Existence is resolved first so an unknown
/// admin (404) is distinct from a real admin with no sessions (200 + empty). Null result -> the host maps to 404.
/// <see cref="SessionView.IsLive"/> is computed at read time; token material NEVER leaves the store (REQ-4.3).</summary>
public sealed record ListSessionsQuery(Guid AdminId) : IQuery<IReadOnlyList<SessionView>?>;

public sealed record SessionView(
    Guid SessionId, Guid FamilyId, SessionStatus Status, DateTime IssuedAt, DateTime IdleExpiresAt,
    DateTime AbsoluteExpiresAt, string? IpAddress, string? UserAgent, bool IsLive);

public sealed class ListSessionsHandler(IUserRepository admins, ISessionStore sessions, IClock clock)
    : IQueryHandler<ListSessionsQuery, IReadOnlyList<SessionView>?>
{
    public async ValueTask<IReadOnlyList<SessionView>?> Handle(ListSessionsQuery query, CancellationToken ct)
    {
        if (!await admins.ExistsAsync(query.AdminId, ct))
            return null;   // -> 404 (REQ-4.4); existence-only, no need to load the entity
        var now = clock.UtcNow;
        var list = await sessions.ListByAdminAsync(query.AdminId, ct);
        return [.. list.Select(s => new SessionView(
            s.Id, s.FamilyId, s.Status, s.IssuedAt, s.IdleExpiresAt, s.AbsoluteExpiresAt,
            s.IpAddress, s.UserAgent, s.IsLiveAt(now)))];
    }
}
