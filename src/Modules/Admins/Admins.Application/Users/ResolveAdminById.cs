using Admins.Application.Roles;
using Admins.Domain.Users;
using Mediator;

namespace Admins.Application.Users;

/// <summary>Per-request, READ-ONLY admin resolution by account id (REQ-9). The session carries the
/// <c>PlatformUserId</c>; the auth handler re-resolves the admin's current Status/Tier/accessible set + Subject
/// fresh on every request so suspension and assignment changes take effect without re-login (REQ-9.1/9.2/9.3).
/// The write path (bind/self-provision) runs ONLY at callback, never here (REQ-9.4).</summary>
public sealed record ResolveByIdQuery(Guid PlatformUserId) : IQuery<ByIdResult>;

public sealed record ByIdResult(ResolveOutcome Outcome, Resolution? Resolution, string? Subject)
{
    public static readonly ByIdResult NotFound = new(ResolveOutcome.NotFound, null, null);
    public static readonly ByIdResult Suspended = new(ResolveOutcome.Suspended, null, null);
    public static ByIdResult Of(Resolution resolution, string? subject) =>
        new(ResolveOutcome.Resolved, resolution, subject);
}

public sealed class ResolveByIdHandler : IQueryHandler<ResolveByIdQuery, ByIdResult>
{
    private readonly IUserRepository _admins;
    private readonly IRoleRepository _roles;

    public ResolveByIdHandler(IUserRepository admins, IRoleRepository roles)
    {
        _admins = admins;
        _roles = roles;
    }

    public async ValueTask<ByIdResult> Handle(ResolveByIdQuery query, CancellationToken cancellationToken)
    {
        var account = await _admins.GetByIdAsync(query.PlatformUserId, cancellationToken);
        if (account is null)
            return ByIdResult.NotFound;
        if (account.Status == UserStatus.Suspended)
            return ByIdResult.Suspended;

        var accessible = await ResolveHandler.ResolveAccessibleAsync(account, _admins, cancellationToken);
        var permissions = await _roles.ListEffectivePermissionsAsync(account.Id, cancellationToken);
        return ByIdResult.Of(
            new Resolution(account.Id, account.Email, account.Tier, accessible) { Permissions = permissions },
            account.Subject);
    }
}
