using Microsoft.Extensions.DependencyInjection;

namespace Admins.Infrastructure;

/// <summary>
/// Admin module wiring hook. Calling it forces this assembly to load so its EF configurations
/// (PlatformUsers, PlatformMerchantAccess, PlatformUserAudits) are discovered at model-build time via
/// <c>HostModuleAssemblies.All</c> (the host adds this assembly). The admin tables live in the same
/// <c>admin</c> schema but are control-plane (no merchant RLS predicate; pol_admin only).
/// <para>NO repositories are registered here: every admin seam binds to the pol_admin keyed DbContext
/// (resolution/provisioning run cross-merchant), which only the host knows — so the host wires them, reusing
/// the same keyed "admin" scope built for Merchant provisioning.</para>
/// </summary>
public static class AdminModuleRegistration
{
    public static IServiceCollection AddAdminModule(this IServiceCollection services) => services;
}
