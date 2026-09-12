using Microsoft.Extensions.DependencyInjection;

namespace Iam.Infrastructure;

/// <summary>
/// Iam module wiring hook. Calling it forces this assembly to load so its EF configurations
/// (Permissions, PermissionGroups, Roles, RolePermissions) are discovered at model-build time via
/// <c>HostModuleAssemblies.All</c> / <c>WorkerModuleAssemblies.All</c> (the host/worker add this assembly). The
/// <c>iam</c> schema is the single catalog replacing the duplicated admin/merch RBAC tables (rf2).
/// <para>NO repositories are registered here: the role store binds to the pol_admin keyed DbContext the same way
/// every other identity/RBAC seam does, which only the host knows — so the host wires it.</para>
/// </summary>
public static class IamModuleRegistration
{
    public static IServiceCollection AddIamModule(this IServiceCollection services) => services;
}
