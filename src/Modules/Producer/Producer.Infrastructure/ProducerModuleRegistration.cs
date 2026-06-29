using System.IO;
using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Producer.Application;
using Producer.Infrastructure.Persistence;

namespace Producer.Infrastructure;

/// <summary>
/// Producer module wiring. Loading this assembly lets <c>ProducerDbContext</c> discover its EF configurations
/// (TenantUsers, ExternalLogins, RegistrationTickets, TenantUserProfiles, RegistrationAudits, ProducerRegistrationNotices,
/// + session/RBAC) at model-build time via <c>ModuleAssemblies.Producer</c>. <c>TenantUsers</c> is the one RLS-keyed
/// producer table; every other producer table is control-plane (no tenant predicate; pol_admin only).
/// <para>
/// It also registers the registration seams on the DEFAULT context. That is what the WORKER needs: the outbox
/// dispatcher there discovers <c>TenantUserRegistrationConsumer</c> (which writes the control-plane notice as
/// pol_worker) and — because Mediator scans the whole referenced assembly — <c>SubmitRegistrationHandler</c> too,
/// so its dependency graph must resolve even though the worker never sends that command. The API re-registers the
/// WRITE seams on the keyed pol_admin context via <c>AddProducerIdentity</c> (last registration wins), because the
/// registration write needs the RLS-bypass connection (a NULL-tenant Pending row — REQ-19.2).
/// </para>
/// </summary>
public static class ProducerModuleRegistration
{
    public static IServiceCollection AddProducerModule(this IServiceCollection services)
    {
        static ProducerDbContext Db(IServiceProvider sp) => sp.GetRequiredService<ProducerDbContext>();

        services.AddScoped<ITenantUserRepository>(sp => new TenantUserRepository(Db(sp)));
        services.AddScoped<IExternalLoginRepository>(sp => new ExternalLoginRepository(Db(sp)));
        services.AddScoped<IRegistrationTicketRepository>(sp => new RegistrationTicketRepository(Db(sp)));
        services.AddScoped<ITenantUserProfileRepository>(sp => new TenantUserProfileRepository(Db(sp)));
        services.AddScoped<IRegistrationAuditWriter>(sp => new RegistrationAuditWriter(Db(sp)));
        // The role repo backs ResolveLoginHandler (effective-permission resolution). The worker never SENDS that
        // query, but Mediator discovers the handler in this assembly, so its dependency graph must RESOLVE under
        // ValidateOnBuild — hence a default-context binding here. The API overrides it onto keyed pol_admin
        // (AddProducerIdentity) so the login lookup reads the control-plane catalog under RLS-bypass.
        services.AddScoped<IProducerRoleRepository>(sp => new ProducerRoleRepository(Db(sp)));
        services.AddScoped<IProducerOutboxWriter>(sp => new ProducerOutboxWriter(Db(sp), sp.GetRequiredService<IClock>()));
        services.AddScoped<IProducerRegistrationUnitOfWork>(sp => new ProducerRegistrationUnitOfWork(Db(sp)));
        // Neutral control-plane commit seam (role/assignment + approve/reject handlers). Default context here so the
        // worker's Mediator-discovered handlers resolve under ValidateOnBuild; the API overrides it onto keyed pol_admin.
        services.AddScoped<IProducerUnitOfWork>(sp => new ProducerRegistrationUnitOfWork(Db(sp)));
        // RejectTenantUserHandler revokes the user's live sessions — default context here for worker ValidateOnBuild
        // (the worker never sends that command); the API overrides it onto keyed pol_admin (AddProducerIdentity).
        services.AddScoped<IProducerSessionStore>(sp => new ProducerSessionStore(Db(sp)));
        services.AddScoped<IProducerRegistrationNoticeWriter>(sp => new ProducerRegistrationNoticeWriter(Db(sp)));

        // Default photo store (worker/local). The API overrides with a config-rooted one in AddProducerIdentity.
        services.AddSingleton<IPhotoStore>(_ =>
            new LocalPhotoStore(Path.Combine(Path.GetTempPath(), "pol-producer-photos")));

        return services;
    }
}
