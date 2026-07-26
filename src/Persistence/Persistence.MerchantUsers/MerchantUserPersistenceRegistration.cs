using BuildingBlocks.Application;
using Merchants.Application.Users;
using Merchants.Application.Users.Roles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Persistence.MerchantUsers.Outbox;
using Persistence.MerchantUsers.Users;

namespace Persistence.MerchantUsers;

/// <summary>
/// The ONE public seam onto <see cref="MerchantUserDbContext"/> (task 8.5.2, design.md "Assembly split + Api
/// host boundary" — no <c>InternalsVisibleTo(Api)</c>, so every adapter that names the internal context lives
/// in this assembly and is exposed here only through public application-layer ports; mirrors
/// <c>Persistence.ControlPlane.ControlPlanePersistenceRegistration</c>'s precedent). Registers the context
/// itself (unkeyed — this cluster has exactly one production capability, <c>MerchantRequestWriteAuthorizer</c>,
/// unlike <c>ControlPlaneDbContext</c>'s admin-vs-provisioning split) plus every adapter over it.
/// <para>
/// <see cref="IRoleRepository"/> is registered here too for completeness (Step 2 of task 8.5.2's brief asked
/// for all 9 ports), but 4 of its 9 members throw <see cref="NotSupportedException"/> — see
/// <see cref="MerchantUserRoleRepository"/>'s doc comment for why (a genuine cross-context read this assembly
/// cannot reach). A caller that needs those 4 members must wait for a host-composed replacement.
/// </para>
/// </summary>
public static class MerchantUserPersistenceRegistration
{
    public static IServiceCollection AddMerchantUserPersistence(
        this IServiceCollection services,
        string connectionString,
        Func<IServiceProvider, IWriteAuthorizer> authorizerFactory)
    {
        services.AddScoped(sp =>
        {
            var options = new DbContextOptionsBuilder<MerchantUserDbContext>()
                .UseSqlServer(connectionString)
                .Options;
            return new MerchantUserDbContext(
                options, sp.GetRequiredService<IActorContext>(), authorizerFactory(sp),
                sp.GetRequiredService<ISecurityTelemetry>());
        });

        services.AddScoped<IUserRepository>(sp => new MerchantUserRepository(sp.GetRequiredService<MerchantUserDbContext>()));
        // The pre-bind seams (bugfix-merchant-prebind-wiring): the task-5 escape-hatch ports, finally DI-wired.
        // ResolveLogin/ResolveById read through IAccountResolver; SubmitRegistration/Approve/Reject load their
        // target through IAccountStore — the filtered IUserRepository above serves BOUND in-session flows only.
        services.AddScoped<IAccountResolver>(sp => new MerchantAccountResolver(sp.GetRequiredService<MerchantUserDbContext>()));
        services.AddScoped<IAccountStore>(sp => new MerchantAccountStore(sp.GetRequiredService<MerchantUserDbContext>()));
        services.AddScoped<IExternalLoginRepository>(sp => new MerchantExternalLoginRepository(sp.GetRequiredService<MerchantUserDbContext>()));
        services.AddScoped<IRegistrationAuditWriter>(sp => new MerchantRegistrationAuditWriter(sp.GetRequiredService<MerchantUserDbContext>()));
        services.AddScoped<IRegistrationOutboxWriter>(sp =>
            new MerchantRegistrationOutboxWriter(sp.GetRequiredService<MerchantUserDbContext>(), sp.GetRequiredService<IClock>()));
        services.AddScoped<IRegistrationUnitOfWork>(sp => new MerchantUserUnitOfWork(
            sp.GetRequiredService<MerchantUserDbContext>(), sp.GetRequiredService<ISecurityTelemetry>()));
        services.AddScoped<IUserUnitOfWork>(sp => new MerchantUserUnitOfWork(
            sp.GetRequiredService<MerchantUserDbContext>(), sp.GetRequiredService<ISecurityTelemetry>()));
        services.AddScoped<ISessionStore>(sp => new MerchantUserSessionStore(
            sp.GetRequiredService<MerchantUserDbContext>(), sp.GetRequiredService<ISecurityTelemetry>()));
        services.AddScoped<IAuthAuditWriter>(sp => new MerchantUserAuthAuditWriter(sp.GetRequiredService<MerchantUserDbContext>()));
        // Keyed so the host (task 8.5.7) can compose the 5 real members here with the 2 cross-context ports
        // (IMerchantRoleReader/IMerchantRoleAssignmentReader) into a full IRoleRepository, without this
        // assembly needing to expose the internal MerchantUserRoleRepository type itself.
        services.AddKeyedScoped<IRoleRepository>("merchantUserPartial",
            (sp, _) => new MerchantUserRoleRepository(sp.GetRequiredService<MerchantUserDbContext>()));
        services.AddScoped<IRoleRepository>(sp => sp.GetRequiredKeyedService<IRoleRepository>("merchantUserPartial"));
        services.AddScoped<IRegistrationNoticeWriter>(sp => new MerchantRegistrationNoticeWriter(sp.GetRequiredService<MerchantUserDbContext>()));

        services.AddScoped<IMerchantRoleAssignmentCountReader>(sp =>
            new MerchantRoleAssignmentCountReader(sp.GetRequiredService<MerchantUserDbContext>()));

        services.AddScoped<IMerchantRoleAssignmentReader>(sp =>
            new MerchantRoleAssignmentReader(sp.GetRequiredService<MerchantUserDbContext>()));

        // The dispatcher's lease-claim scan (task 8.5.6, AddMerchantUserOutboxDispatcher below in this
        // namespace) resolves this per-batch — internal, so it can only be registered from inside this
        // assembly.
        services.AddScoped<IMerchantUserOutboxDrain>(sp => new MerchantUserOutboxDrain(sp.GetRequiredService<MerchantUserDbContext>()));

        return services;
    }
}
