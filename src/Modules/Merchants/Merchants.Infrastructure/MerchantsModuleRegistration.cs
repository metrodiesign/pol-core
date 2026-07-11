using System.IO;
using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Persistence;
using Merchants.Application;
using Merchants.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Merchants.Infrastructure;

/// <summary>
/// Merchants module wiring (merges the former Tenant + Producer module registrations, REQ-1.3). Loading this
/// assembly lets <c>PolDbContext</c> discover its EF configurations (Merchants, ProvisioningAudits, MerchantUsers,
/// ExternalLogins, RegistrationAudits, RegistrationNotices, + session/RBAC) at model-build time via
/// <c>ModuleAssemblies.Modules</c>. Every merchant-user table is control-plane (no merchant predicate; pol_admin
/// only) — like Admin; the merchant-user account carries its own merchant id directly (REQ-2.3). The <c>Merchant</c>
/// provisioning seams (<c>IMerchantRepository</c>, <c>IProvisioningAuditWriter</c>) are deliberately NOT registered
/// here — they, plus every other pol_app-consumer seam, MUST bind to the pol_admin keyed DbContext, which only the
/// composition root (Api host) knows, so the host wires them (REQ-4.4).
/// <para>
/// It also registers the registration seams on the DEFAULT context. That is what the WORKER needs: the outbox
/// dispatcher there discovers <c>MerchantUserRegistrationConsumer</c> (which writes the control-plane notice as
/// pol_worker) and — because Mediator scans the whole referenced assembly — <c>SubmitRegistrationHandler</c> too,
/// so its dependency graph must resolve even though the worker never sends that command. The API re-registers the
/// WRITE seams on the keyed pol_admin context via the host's identity wiring (last registration wins), because the
/// registration write needs the RLS-bypass connection (a NULL-merchant Pending row — REQ-19.2).
/// </para>
/// </summary>
public static class MerchantsModuleRegistration
{
    public static IServiceCollection AddMerchantsModule(this IServiceCollection services)
    {
        static PolDbContext Db(IServiceProvider sp) => sp.GetRequiredService<PolDbContext>();

        services.AddScoped<IMerchantUserRepository>(sp => new MerchantUserRepository(Db(sp)));
        services.AddScoped<IExternalLoginRepository>(sp => new ExternalLoginRepository(Db(sp)));
        services.AddScoped<IRegistrationAuditWriter>(sp => new RegistrationAuditWriter(Db(sp)));
        // The role repo backs ResolveLoginHandler (effective-permission resolution). The worker never SENDS that
        // query, but Mediator discovers the handler in this assembly, so its dependency graph must RESOLVE under
        // ValidateOnBuild — hence a default-context binding here. The API overrides it onto keyed pol_admin
        // (the host identity wiring) so the login lookup reads the control-plane catalog under RLS-bypass.
        services.AddScoped<IMerchantUserRoleRepository>(sp => new MerchantUserRoleRepository(Db(sp)));
        services.AddScoped<IMerchantsOutboxWriter>(sp => new MerchantsOutboxWriter(Db(sp), sp.GetRequiredService<IClock>()));
        services.AddScoped<IMerchantsRegistrationUnitOfWork>(sp => new MerchantsRegistrationUnitOfWork(Db(sp)));
        // Neutral control-plane commit seam (role/assignment + approve/reject handlers). Default context here so the
        // worker's Mediator-discovered handlers resolve under ValidateOnBuild; the API overrides it onto keyed pol_admin.
        services.AddScoped<IMerchantsUnitOfWork>(sp => new MerchantsRegistrationUnitOfWork(Db(sp)));
        // RejectMerchantUserHandler revokes the user's live sessions — default context here for worker ValidateOnBuild
        // (the worker never sends that command); the API overrides it onto keyed pol_admin (the host identity wiring).
        services.AddScoped<IMerchantUserSessionStore>(sp => new MerchantUserSessionStore(Db(sp)));
        services.AddScoped<IRegistrationNoticeWriter>(sp => new RegistrationNoticeWriter(Db(sp)));

        // Default photo store (worker/local). The API overrides with a config-rooted one in the host identity wiring.
        services.AddSingleton<IPhotoStore>(_ =>
            new LocalPhotoStore(Path.Combine(Path.GetTempPath(), "pol-merchants-photos")));

        return services;
    }
}
