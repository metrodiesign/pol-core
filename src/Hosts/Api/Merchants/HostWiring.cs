using Merchants.Application;
using Merchants.Application.Users;
using Merchants.Application.Users.Roles;
using Merchants.Domain.Users.Roles;
using Merchants.Infrastructure;
using Persistence.ControlPlane.Iam;
using Persistence.MerchantUser;

namespace Api.Merchants;

/// <summary>
/// Composes <c>Merchants.Application.Users.Roles.IRoleRepository</c> for the host (task 8.5.7): the 5 members
/// that touch only <c>merch.RoleAssignments</c> delegate to the keyed "merchantUserPartial" adapter
/// <c>Persistence.MerchantUser</c> already builds; the 4 members that also need <c>iam.Roles</c>/
/// <c>iam.RolePermissions</c> (a DIFFERENT runtime context that assembly may never reference) compose
/// <see cref="IMerchantRoleReader"/> (<c>Persistence.ControlPlane</c>) with
/// <see cref="IMerchantRoleAssignmentReader"/> (<c>Persistence.MerchantUser</c>) — first resolve the user's
/// role ids IN THIS MERCHANT, then resolve those ids against the iam catalog. Same two-port-composition shape
/// as <see cref="Api.Iam.HostRoleAssignmentCounter"/>.
/// </summary>
internal sealed class HostMerchantRoleRepository : IRoleRepository
{
    private readonly IRoleRepository _partial;
    private readonly IMerchantRoleReader _roles;
    private readonly IMerchantRoleAssignmentReader _assignments;

    public HostMerchantRoleRepository(
        IRoleRepository partial, IMerchantRoleReader roles, IMerchantRoleAssignmentReader assignments)
    {
        _partial = partial;
        _roles = roles;
        _assignments = assignments;
    }

    public void AddAssignment(RoleAssignment assignment) => _partial.AddAssignment(assignment);
    public void RemoveAssignment(RoleAssignment assignment) => _partial.RemoveAssignment(assignment);

    public Task<IReadOnlySet<Guid>> ListRoleIdsForUserAsync(Guid merchantUserId, CancellationToken cancellationToken) =>
        _partial.ListRoleIdsForUserAsync(merchantUserId, cancellationToken);

    public Task<RoleAssignment?> GetAssignmentAsync(Guid merchantUserId, Guid roleId, CancellationToken cancellationToken) =>
        _partial.GetAssignmentAsync(merchantUserId, roleId, cancellationToken);

    public Task<bool> AssignmentExistsAsync(Guid merchantUserId, Guid roleId, CancellationToken cancellationToken) =>
        _partial.AssignmentExistsAsync(merchantUserId, roleId, cancellationToken);

    public Task<IReadOnlyDictionary<string, Guid>> GetRoleIdsByCodesAsync(
        Guid merchantId, IReadOnlyCollection<string> codes, CancellationToken cancellationToken) =>
        _roles.GetRoleIdsByCodesAsync(merchantId, codes, cancellationToken);

    public Task<IReadOnlyDictionary<string, Guid>> GetActiveRoleIdsByCodesAsync(
        Guid merchantId, IReadOnlyCollection<string> codes, CancellationToken cancellationToken) =>
        _roles.GetActiveRoleIdsByCodesAsync(merchantId, codes, cancellationToken);

    public async Task<IReadOnlySet<string>> ListEffectivePermissionsAsync(
        Guid merchantUserId, Guid merchantId, CancellationToken cancellationToken)
    {
        var roleIds = await _assignments.ListRoleIdsForUserInMerchantAsync(merchantUserId, merchantId, cancellationToken);
        return await _roles.ListEffectivePermissionsAsync(merchantId, roleIds, cancellationToken);
    }

    public async Task<IReadOnlyList<string>> ListActiveRoleCodesForUserAsync(
        Guid merchantUserId, Guid merchantId, CancellationToken cancellationToken)
    {
        var roleIds = await _assignments.ListRoleIdsForUserInMerchantAsync(merchantUserId, merchantId, cancellationToken);
        return await _roles.ListActiveRoleCodesAsync(merchantId, roleIds, cancellationToken);
    }
}

/// <summary>
/// Binds the host-only Merchants identity seams (task 8.5.7 — every repository/session-store/audit seam now
/// binds directly to <c>MerchantUserDbContext</c> via <c>AddMerchantUserPersistence</c>, shared unkeyed by
/// both hosts). Overrides ONLY <see cref="IRoleRepository"/> onto the cross-context composition above; the
/// rest are host-only concerns (photo store, session cookies, per-request scope, ticket protector) with no
/// persistence dependency of their own. Call AFTER AddMerchantUserPersistence + AddControlPlanePersistence.
/// </summary>
internal static class HostWiring
{
    public static IServiceCollection AddMerchantsIdentity(this IServiceCollection services)
    {
        // Explicit factory (not plain AddScoped<IRoleRepository, HostMerchantRoleRepository>): constructor
        // injection would resolve its own "IRoleRepository partial" parameter back to itself (the latest
        // unkeyed registration) and recurse. The keyed "merchantUserPartial" registration is the real target.
        services.AddScoped<IRoleRepository>(sp => new HostMerchantRoleRepository(
            sp.GetRequiredKeyedService<IRoleRepository>("merchantUserPartial"),
            sp.GetRequiredService<IMerchantRoleReader>(),
            sp.GetRequiredService<IMerchantRoleAssignmentReader>()));

        // Per-request merchant-user scope (REQ-17.1): the session handler binds the concrete MerchantUserScope;
        // endpoints read IMerchantUserScope — the SAME scoped instance. RequirePermission +
        // /merchants/users/me consume it.
        services.AddScoped<UserScope>();
        services.AddScoped<IUserScope>(sp => sp.GetRequiredService<UserScope>());

        services.AddSingleton<UserSessionCookies>();

        // Photo store + ticket protector (host-only concerns).
        services.AddSingleton<IPhotoStore>(sp =>
        {
            var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<UserRegistrationOptions>>().Value;
            var env = sp.GetRequiredService<IHostEnvironment>();
            var root = Path.IsPathRooted(options.PhotoStoreRootPath)
                ? options.PhotoStoreRootPath
                : Path.Combine(env.ContentRootPath, options.PhotoStoreRootPath);
            return new LocalPhotoStore(root);
        });
        services.AddSingleton<UserRegistrationTickets>();

        return services;
    }
}
