using Admins.Domain;
using Mediator;

namespace Admins.Application.ResolveAdmin;

/// <summary>Per-request, READ-ONLY admin resolution by account id (REQ-9). The session carries the
/// <c>PlatformUserId</c>; the auth handler re-resolves the admin's current Status/Tier/accessible set + Subject
/// fresh on every request so suspension and assignment changes take effect without re-login (REQ-9.1/9.2/9.3).
/// The write path (bind/self-provision) runs ONLY at callback, never here (REQ-9.4).</summary>
public sealed record ResolveAdminByIdQuery(Guid PlatformUserId) : IQuery<AdminByIdResult>;

public sealed record AdminByIdResult(AdminResolveOutcome Outcome, AdminResolution? Resolution, string? Subject)
{
    public static readonly AdminByIdResult NotFound = new(AdminResolveOutcome.NotFound, null, null);
    public static readonly AdminByIdResult Suspended = new(AdminResolveOutcome.Suspended, null, null);
    public static AdminByIdResult Of(AdminResolution resolution, string? subject) =>
        new(AdminResolveOutcome.Resolved, resolution, subject);
}

public sealed class ResolveAdminByIdHandler : IQueryHandler<ResolveAdminByIdQuery, AdminByIdResult>
{
    private readonly IPlatformUserRepository _admins;
    private readonly IAdminRoleRepository _roles;

    public ResolveAdminByIdHandler(IPlatformUserRepository admins, IAdminRoleRepository roles)
    {
        _admins = admins;
        _roles = roles;
    }

    public async ValueTask<AdminByIdResult> Handle(ResolveAdminByIdQuery query, CancellationToken cancellationToken)
    {
        var account = await _admins.GetByIdAsync(query.PlatformUserId, cancellationToken);
        if (account is null)
            return AdminByIdResult.NotFound;
        if (account.Status == AdminStatus.Suspended)
            return AdminByIdResult.Suspended;

        var accessible = await ResolveAdminHandler.ResolveAccessibleAsync(account, _admins, cancellationToken);
        var permissions = await _roles.ListEffectivePermissionsAsync(account.Id, cancellationToken);
        return AdminByIdResult.Of(
            new AdminResolution(account.Id, account.Email, account.Tier, accessible) { Permissions = permissions },
            account.Subject);
    }
}
