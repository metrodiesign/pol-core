using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Persistence;
using Producer.Application;
using Producer.Infrastructure;
using Producer.Infrastructure.Persistence;

namespace Api;

/// <summary>
/// Binds the Producer identity seams. The registration/correction write runs cross-tenant / tenant-less on the keyed
/// pol_admin <see cref="ProducerDbContext"/> (a NULL-tenant Pending row the RLS BLOCK predicate would reject under a
/// tenant principal — REQ-19.2), so those seams resolve the SAME keyed "admin" context the Admin module uses; they
/// share one Scoped instance per request, hence one transaction. The notice writer binds the DEFAULT context (the
/// Admin-side outbox consumer that actually runs on the worker writes as pol_worker). Call AFTER AddTenantAdminScope.
/// </summary>
// ponytail: DUPLICATE-shaped of AdminHostWiring.AddAdminIdentity (same keyed-pol_admin pattern) — deliberate.
internal static class ProducerHostWiring
{
    public static IServiceCollection AddProducerIdentity(this IServiceCollection services)
    {
        static ProducerDbContext Admin(IServiceProvider sp) => sp.GetRequiredKeyedService<ProducerDbContext>("admin");

        // Registration realm on the keyed pol_admin context (REQ-19.2) — one shared Scoped instance = one tx.
        services.AddScoped<IProducerAccountRepository>(sp => new ProducerAccountRepository(Admin(sp)));
        services.AddScoped<IProducerTenantAssignmentRepository>(sp => new ProducerTenantAssignmentRepository(Admin(sp)));
        services.AddScoped<IExternalLoginRepository>(sp => new ExternalLoginRepository(Admin(sp)));
        services.AddScoped<IRegistrationTicketRepository>(sp => new RegistrationTicketRepository(Admin(sp)));
        services.AddScoped<ITenantUserProfileRepository>(sp => new TenantUserProfileRepository(Admin(sp)));
        services.AddScoped<IRegistrationAuditWriter>(sp => new RegistrationAuditWriter(Admin(sp)));
        services.AddScoped<IProducerOutboxWriter>(sp => new ProducerOutboxWriter(Admin(sp), sp.GetRequiredService<IClock>()));
        services.AddScoped<IProducerRegistrationUnitOfWork>(sp => new ProducerRegistrationUnitOfWork(Admin(sp)));
        services.AddScoped<IProducerUnitOfWork>(sp => new ProducerRegistrationUnitOfWork(Admin(sp)));
        // (The notice writer + a default photo store are registered by AddProducerModule on the default context —
        // the Admin-side consumer never runs in the API. Here we only override the WRITE seams onto pol_admin.)

        // Producer BFF session substrate (REQ-10/11/12) + the control-plane RBAC catalog, all on the keyed pol_admin
        // context: the login lookup reads PendingApproval/NULL-tenant rows (RLS bypass, REQ-19.2) and the effective
        // permission set; the session store + auth audit persist control-plane rows pol_app has no grant on. The
        // role repo is OVERRIDDEN here onto pol_admin (AddProducerModule binds the default context for worker DI).
        // The cookie service is stateless (singleton).
        services.AddScoped<IProducerRoleRepository>(sp => new ProducerRoleRepository(Admin(sp)));
        services.AddScoped<IProducerSessionStore>(sp => new ProducerSessionStore(Admin(sp)));
        services.AddScoped<IProducerAuthAuditWriter>(sp => new ProducerAuthAuditWriter(Admin(sp)));
        services.AddSingleton<ProducerSessionCookies>();

        // Per-request producer scope (REQ-17.1): the session handler binds the concrete ProducerScope; endpoints read
        // IProducerScope — the SAME scoped instance. RequireProducerPermission + /producer/me consume it.
        services.AddScoped<ProducerScope>();
        services.AddScoped<IProducerScope>(sp => sp.GetRequiredService<ProducerScope>());

        // Photo store + ticket protector (host-only concerns).
        services.AddSingleton<IPhotoStore>(sp =>
        {
            var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<ProducerRegistrationOptions>>().Value;
            var env = sp.GetRequiredService<IHostEnvironment>();
            var root = Path.IsPathRooted(options.PhotoStoreRootPath)
                ? options.PhotoStoreRootPath
                : Path.Combine(env.ContentRootPath, options.PhotoStoreRootPath);
            return new LocalPhotoStore(root);
        });
        services.AddSingleton<ProducerRegistrationTickets>();

        return services;
    }
}
