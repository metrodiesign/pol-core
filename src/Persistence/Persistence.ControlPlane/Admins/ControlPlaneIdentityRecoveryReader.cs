using Admins.Application.Users;
using Admins.Domain.Users;
using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Persistence.ControlPlane.Governance;
using SharedKernel;

namespace Persistence.ControlPlane.Admins;

/// <summary>Re-resolves a raced Microsoft identity using a context created after the failed transaction.
/// The read path returns the same typed outcome as the normal application resolver.</summary>
internal sealed class ControlPlaneIdentityRecoveryReader : IAdminIdentityRecoveryReader
{
    private readonly IDbContextFactory<ControlPlaneDbContext> _contexts;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ISecurityTelemetry _telemetry;

    public ControlPlaneIdentityRecoveryReader(
        IDbContextFactory<ControlPlaneDbContext> contexts,
        ILoggerFactory loggerFactory,
        ISecurityTelemetry telemetry)
    {
        _contexts = contexts;
        _loggerFactory = loggerFactory;
        _telemetry = telemetry;
    }

    public async Task<ResolveResult> ResolveAfterConflictAsync(
        ProviderIdentity identity, CancellationToken cancellationToken)
    {
        await using var db = await _contexts.CreateDbContextAsync(cancellationToken);
        var admins = new UserRepository(
            db,
            _loggerFactory.CreateLogger<UserRepository>(),
            _telemetry,
            new GovernanceSqlLockManager(db));
        var roles = new RoleRepository(db);
        var account = await admins.GetByIdentityAsync(identity, cancellationToken);
        if (account is null)
            return ResolveResult.IdentityConflict;
        if (account.Status == UserStatus.Suspended)
            return ResolveResult.Suspended;

        var accessible = account.Tier == Tier.Super
            ? AccessibleMerchants.All
            : AccessibleMerchants.Of(await admins.ListAssignedMerchantIdsAsync(account.Id, cancellationToken));
        var permissions = await roles.ListEffectivePermissionsAsync(account.Id, cancellationToken);
        return ResolveResult.Of(new Resolution(account.Id, account.Email, account.Tier, accessible)
        {
            Permissions = permissions,
            AuthorizationVersion = account.AuthorizationVersion
        });
    }
}
