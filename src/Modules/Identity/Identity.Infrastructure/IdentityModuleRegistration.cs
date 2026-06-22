using Microsoft.Extensions.DependencyInjection;

namespace Identity.Infrastructure;

/// <summary>
/// Identity module wiring hook. Calling it forces this assembly to load so its EF configurations
/// (TenantUsers, ExternalLogins, TenantUserProfiles, RegistrationTickets, RegistrationAudits) are
/// discovered at model-build time via <c>ModuleAssemblies.Producer</c> (the host also adds this assembly).
/// <para>NO repositories are registered here: every Identity seam binds to the pol_admin keyed DbContext
/// (registration/approval/resolve run before a tenant is bound), which only the host knows — so the host
/// wires them, reusing the same keyed "admin" scope built for Tenant provisioning.</para>
/// </summary>
public static class IdentityModuleRegistration
{
    public static IServiceCollection AddIdentityModule(this IServiceCollection services) => services;
}
