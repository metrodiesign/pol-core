using Admin.Application.ResolveAdmin;
using Admin.Domain;
using BuildingBlocks.Application;
using Mediator;

namespace Admin.Application.AdminAccountQueries;

/// <summary>Read models for the admin-account-management console (admin-account-management REQ-1/2/6). Read-only —
/// no transaction. The admin directory is an SFS list: it inherits <see cref="PagedQuery"/> and returns a
/// <see cref="PagedResult{T}"/>. Control-plane data (no TenantId) -> NOT <c>ITenantScoped</c>.</summary>
public sealed record ListAdminsQuery : PagedQuery, IQuery<PagedResult<AdminAccountListItem>>;

public sealed class ListAdminsHandler(IAdminAccountRepository admins)
    : IQueryHandler<ListAdminsQuery, PagedResult<AdminAccountListItem>>
{
    public async ValueTask<PagedResult<AdminAccountListItem>> Handle(ListAdminsQuery query, CancellationToken ct) =>
        await admins.ListAsync(query, ct);
}

/// <summary>Full detail for one admin (REQ-2): list fields + the accessible-tenant set (mirroring
/// <c>GET /admins/me</c>) + every assigned role code incl. Inactive roles. Null result -> the host maps to 404.</summary>
public sealed record GetAdminByIdQuery(Guid AdminId) : IQuery<AdminAccountDetail?>;

public sealed record AdminAccountDetail(
    Guid AdminId, string Email, AdminTier Tier, AdminStatus Status, DateTime CreatedAt,
    bool SubjectBound, AccessibleTenants Accessible, IReadOnlyList<string> RoleCodes);

public sealed class GetAdminByIdHandler(IAdminAccountRepository admins, IAdminRoleRepository roles)
    : IQueryHandler<GetAdminByIdQuery, AdminAccountDetail?>
{
    public async ValueTask<AdminAccountDetail?> Handle(GetAdminByIdQuery query, CancellationToken ct)
    {
        var account = await admins.GetByIdAsync(query.AdminId, ct);
        if (account is null)
            return null;   // -> 404 (REQ-2.2)

        // Reuse the canonical accessible-set rule the sign-in pipeline uses (REQ-2.1); host maps ids -> codes.
        var accessible = await ResolveAdminHandler.ResolveAccessibleAsync(account, admins, ct);
        var roleCodes = await roles.ListRoleCodesForAdminAsync(account.Id, ct);
        return new AdminAccountDetail(
            account.Id, account.Email, account.Tier, account.Status, account.CreatedAt,
            account.Subject is not null, accessible, roleCodes);
    }
}

/// <summary>The distinct union of permission keys granted through the admin's ACTIVE roles (REQ-6) — the same
/// rule as <c>GET /admins/me</c>. Existence is resolved first because the repo returns an empty set (not null)
/// for an unknown id, so an empty set alone cannot mean "not found" (REQ-6.3). Null result -> the host maps to
/// 404; the keys are ordinal-ascending so the response is deterministic (REQ-6.2). Works for a Suspended target
/// (REQ-6.4 — suspension blocks sign-in, not role grants).</summary>
public sealed record GetAdminEffectivePermissionsQuery(Guid AdminId) : IQuery<IReadOnlyList<string>?>;

public sealed class GetAdminEffectivePermissionsHandler(IAdminAccountRepository admins, IAdminRoleRepository roles)
    : IQueryHandler<GetAdminEffectivePermissionsQuery, IReadOnlyList<string>?>
{
    public async ValueTask<IReadOnlyList<string>?> Handle(GetAdminEffectivePermissionsQuery query, CancellationToken ct)
    {
        if (!await admins.ExistsAsync(query.AdminId, ct))
            return null;   // -> 404 (REQ-6.3); existence-only, no need to load the entity
        var keys = await roles.ListEffectivePermissionsAsync(query.AdminId, ct);
        return [.. keys.OrderBy(k => k, StringComparer.Ordinal)];   // deterministic ascending (REQ-6.2)
    }
}

/// <summary>An admin's sessions for the session-management view (REQ-4). Existence is resolved first so an unknown
/// admin (404) is distinct from a real admin with no sessions (200 + empty). Null result -> the host maps to 404.
/// <see cref="AdminSessionView.IsLive"/> is computed at read time; token material NEVER leaves the store (REQ-4.3).</summary>
public sealed record ListAdminSessionsQuery(Guid AdminId) : IQuery<IReadOnlyList<AdminSessionView>?>;

public sealed record AdminSessionView(
    Guid SessionId, Guid FamilyId, AdminSessionStatus Status, DateTime IssuedAt, DateTime IdleExpiresAt,
    DateTime AbsoluteExpiresAt, string? CreatedIp, string? UserAgent, bool IsLive);

public sealed class ListAdminSessionsHandler(IAdminAccountRepository admins, IAdminSessionStore sessions, IClock clock)
    : IQueryHandler<ListAdminSessionsQuery, IReadOnlyList<AdminSessionView>?>
{
    public async ValueTask<IReadOnlyList<AdminSessionView>?> Handle(ListAdminSessionsQuery query, CancellationToken ct)
    {
        if (!await admins.ExistsAsync(query.AdminId, ct))
            return null;   // -> 404 (REQ-4.4); existence-only, no need to load the entity
        var now = clock.UtcNow;
        var list = await sessions.ListByAdminAsync(query.AdminId, ct);
        return [.. list.Select(s => new AdminSessionView(
            s.Id, s.FamilyId, s.Status, s.IssuedAt, s.IdleExpiresAt, s.AbsoluteExpiresAt,
            s.CreatedIp, s.UserAgent, s.IsLiveAt(now)))];
    }
}
