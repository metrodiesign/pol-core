using BuildingBlocks.Application;
using Merchants.Application.Users;
using Merchants.Application.Users.Roles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Persistence.ControlPlane;
using Persistence.MerchantUsers.Outbox;
using Persistence.MerchantUsers.Users;

namespace Persistence.MerchantUsers;

/// <summary>
/// The merchant-identity adapter seam onto <see cref="ControlPlaneDbContext"/> (Task10 context closure and
/// task 8.5.2's original seam, design.md "Assembly split + Api
/// host boundary" — no <c>InternalsVisibleTo(Api)</c>, so every adapter that names the internal context lives
/// in this assembly and is exposed here only through public application-layer ports; mirrors
/// <c>Persistence.ControlPlane.ControlPlanePersistenceRegistration</c>'s precedent). Registers the context
/// itself is registered by <c>AddControlPlanePersistence</c>; this extension only binds merchant-identity ports
/// over that scoped instance. There is no third runtime context registration.
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
        services.AddScoped<IUserRepository>(sp => new MerchantUserRepository(
            sp.GetRequiredService<ControlPlaneDbContext>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<MerchantUserRepository>>()));
        services.AddScoped<IInvitationRepository>(sp => new MerchantInvitationRepository(sp.GetRequiredService<ControlPlaneDbContext>()));
        services.AddScoped<IManagementAuditWriter>(sp => new MerchantManagementAuditWriter(sp.GetRequiredService<ControlPlaneDbContext>()));
        services.AddScoped<IAdminUserOperationStore>(sp => new AdminUserOperationStore(sp.GetRequiredService<ControlPlaneDbContext>()));
        services.AddScoped<IActiveManagerGuard>(sp => new ActiveManagerGuard(sp.GetRequiredService<ControlPlaneDbContext>()));
        // The pre-bind seams (bugfix-merchant-prebind-wiring): the task-5 escape-hatch ports, finally DI-wired.
        // ResolveLogin/ResolveById read through IAccountResolver; SubmitRegistration/Approve/Reject load their
        // target through IAccountStore — the filtered IUserRepository above serves BOUND in-session flows only.
        services.AddScoped<IAccountResolver>(sp => new MerchantAccountResolver(sp.GetRequiredService<ControlPlaneDbContext>()));
        services.AddScoped<IAccountStore>(sp => new MerchantAccountStore(sp.GetRequiredService<ControlPlaneDbContext>()));
        services.AddScoped<IExternalLoginRepository>(sp => new MerchantExternalLoginRepository(sp.GetRequiredService<ControlPlaneDbContext>()));
        services.AddScoped<IRegistrationAuditWriter>(sp => new MerchantRegistrationAuditWriter(sp.GetRequiredService<ControlPlaneDbContext>()));
        services.AddScoped<IRegistrationAttemptWriter>(sp => new MerchantRegistrationAttemptWriter(sp.GetRequiredService<ControlPlaneDbContext>()));
        services.AddScoped<IRegistrationHistoryReader>(sp => new MerchantRegistrationHistoryReader(sp.GetRequiredService<ControlPlaneDbContext>()));
        services.AddScoped<IRegistrationOutboxWriter>(sp =>
            new MerchantRegistrationOutboxWriter(sp.GetRequiredService<ControlPlaneDbContext>(), sp.GetRequiredService<IClock>()));
        services.AddScoped<IRegistrationUnitOfWork>(sp => new MerchantUserUnitOfWork(
            sp.GetRequiredService<ControlPlaneDbContext>(), sp.GetRequiredService<ISecurityTelemetry>()));
        services.AddScoped<IUserUnitOfWork>(sp => new MerchantUserUnitOfWork(
            sp.GetRequiredService<ControlPlaneDbContext>(), sp.GetRequiredService<ISecurityTelemetry>()));
        services.AddScoped<ISessionStore>(sp => new MerchantUserSessionStore(
            sp.GetRequiredService<ControlPlaneDbContext>(), sp.GetRequiredService<ISecurityTelemetry>()));
        services.AddScoped<IAuthAuditWriter>(sp => new MerchantUserAuthAuditWriter(sp.GetRequiredService<ControlPlaneDbContext>()));
        // Keyed so the host (task 8.5.7) can compose the 5 real members here with the 2 cross-context ports
        // (IMerchantRoleReader/IMerchantRoleAssignmentReader) into a full IRoleRepository, without this
        // assembly needing to expose the internal MerchantUserRoleRepository type itself.
        services.AddKeyedScoped<IRoleRepository>("merchantUserPartial",
            (sp, _) => new MerchantUserRoleRepository(sp.GetRequiredService<ControlPlaneDbContext>()));
        services.AddScoped<IRoleRepository>(sp => sp.GetRequiredKeyedService<IRoleRepository>("merchantUserPartial"));
        services.AddScoped<IRegistrationNoticeWriter>(sp => new MerchantRegistrationNoticeWriter(sp.GetRequiredService<ControlPlaneDbContext>()));

        services.AddScoped<IMerchantRoleAssignmentCountReader>(sp =>
            new MerchantRoleAssignmentCountReader(sp.GetRequiredService<ControlPlaneDbContext>()));

        services.AddScoped<IMerchantRoleAssignmentReader>(sp =>
            new MerchantRoleAssignmentReader(sp.GetRequiredService<ControlPlaneDbContext>()));

        // The dispatcher's lease-claim scan (task 8.5.6, AddMerchantUserOutboxDispatcher below in this
        // namespace) resolves this per-batch — internal, so it can only be registered from inside this
        // assembly.
        services.AddScoped<IMerchantUserOutboxDrain>(sp => new MerchantUserOutboxDrain(sp.GetRequiredService<ControlPlaneDbContext>()));

        return services;
    }
}
