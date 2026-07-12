using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Persistence;
using Merchants.Application;
using Merchants.Application.Users;
using Merchants.Application.Users.Roles;
using Merchants.Infrastructure;
using Merchants.Infrastructure.Persistence;
using Merchants.Infrastructure.Persistence.Users;
using Merchants.Infrastructure.Persistence.Users.Roles;

namespace Api.Merchants;

/// <summary>
/// Binds the Merchants identity seams. The registration/correction write runs cross-merchant / merchant-less on the
/// keyed pol_admin <see cref="PolDbContext"/> (a NULL-merchant Pending row the RLS BLOCK predicate would reject
/// under a merchant principal — REQ-19.2), so those seams resolve the SAME keyed "admin" context the Admin module
/// uses; they share one Scoped instance per request, hence one transaction. The notice writer binds the DEFAULT
/// context (the Admin-side outbox consumer that actually runs on the worker writes as pol_worker). Call AFTER
/// AddAdminScope.
/// </summary>
// ponytail: DUPLICATE-shaped of AdminHostWiring.AddAdminIdentity (same keyed-pol_admin pattern) — deliberate.
internal static class HostWiring
{
    public static IServiceCollection AddMerchantsIdentity(this IServiceCollection services)
    {
        static PolDbContext Admin(IServiceProvider sp) => sp.GetRequiredKeyedService<PolDbContext>("admin");

        // Registration realm on the keyed pol_admin context (REQ-19.2) — one shared Scoped instance = one tx.
        // No assignment repository: MerchantUser.MerchantId absorbs the former separate assignment edge (REQ-2.3).
        services.AddScoped<IUserRepository>(sp => new UserRepository(Admin(sp)));
        services.AddScoped<IExternalLoginRepository>(sp => new ExternalLoginRepository(Admin(sp)));
        services.AddScoped<IRegistrationAuditWriter>(sp => new RegistrationAuditWriter(Admin(sp)));
        services.AddScoped<IRegistrationOutboxWriter>(sp => new RegistrationOutboxWriter(Admin(sp), sp.GetRequiredService<IClock>()));
        services.AddScoped<IRegistrationUnitOfWork>(sp => new UserUnitOfWork(Admin(sp)));
        services.AddScoped<IUserUnitOfWork>(sp => new UserUnitOfWork(Admin(sp)));
        // (The notice writer + a default photo store are registered by AddMerchantsModule on the default context —
        // the Admin-side consumer never runs in the API. Here we only override the WRITE seams onto pol_admin.)

        // Merchant-user BFF session substrate (REQ-10/11/12) + the control-plane RBAC catalog, all on the keyed
        // pol_admin context: the login lookup reads PendingApproval/NULL-merchant rows (RLS bypass, REQ-19.2) and
        // the effective permission set; the session store + auth audit persist control-plane rows pol_app has no
        // grant on. The role repo is OVERRIDDEN here onto pol_admin (AddMerchantsModule binds the default context
        // for worker DI). The cookie service is stateless (singleton).
        services.AddScoped<IRoleRepository>(sp => new RoleRepository(Admin(sp)));
        services.AddScoped<ISessionStore>(sp => new SessionStore(Admin(sp)));
        services.AddScoped<IAuthAuditWriter>(sp => new AuthAuditWriter(Admin(sp)));
        services.AddSingleton<UserSessionCookies>();

        // Per-request merchant-user scope (REQ-17.1): the session handler binds the concrete MerchantUserScope;
        // endpoints read IMerchantUserScope — the SAME scoped instance. RequireMerchantUserPermission +
        // /merchants/users/me consume it.
        services.AddScoped<UserScope>();
        services.AddScoped<IUserScope>(sp => sp.GetRequiredService<UserScope>());

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
