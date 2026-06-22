using Microsoft.Extensions.DependencyInjection;

namespace Tenant.Infrastructure;

/// <summary>
/// Tenant module wiring hook. Calling it forces this assembly to load so its EF configurations
/// (Tenants, ProvisioningAudits) are discovered at model-build time via <c>ModuleAssemblies.Producer</c>
/// (the host also adds this assembly to that list).
/// <para>NO repositories are registered here on purpose: every provisioning / read-back seam
/// (<c>ITenantRepository</c>, <c>IProvisioningAuditWriter</c>, <c>IAdminUnitOfWork</c>,
/// <c>IAdminVaultSecretStore</c>, <c>IAdminPspConnectionRepository</c>) MUST bind to the pol_admin keyed
/// DbContext, which only the composition root (Api host) knows — so the host wires them (REQ-4.4).</para>
/// </summary>
public static class TenantModuleRegistration
{
    public static IServiceCollection AddTenantModule(this IServiceCollection services) => services;
}
