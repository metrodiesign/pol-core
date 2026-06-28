using Microsoft.Extensions.DependencyInjection;

namespace Producer.Infrastructure;

/// <summary>
/// Producer module wiring hook. Calling it forces this assembly to load so its EF configurations (TenantUsers,
/// ExternalLogins, RegistrationTickets, TenantUserProfiles, RegistrationAudits) are discovered at model-build
/// time via <c>ModuleAssemblies.Producer</c> (the host adds this assembly). <c>TenantUsers</c> is the one
/// RLS-keyed producer table (FILTER+BLOCK on TenantId); every other producer table is control-plane (no tenant
/// predicate; pol_admin only) in the same <c>producer</c> schema.
/// <para>NO repositories are registered here: the producer identity seams bind the pol_admin keyed DbContext
/// (registration/approval/resolve run cross-tenant or tenant-less), which only the host knows — so the host
/// wires them, mirroring <c>AdminModuleRegistration</c>.</para>
/// </summary>
// ponytail: DUPLICATE of Admin.Infrastructure.AdminModuleRegistration — deliberate debt, do not refactor into a shared base.
public static class ProducerModuleRegistration
{
    public static IServiceCollection AddProducerModule(this IServiceCollection services) => services;
}
