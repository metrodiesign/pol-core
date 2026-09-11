using Admins.Application.Roles;
using Admins.Application.Users;
using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Vault;
using Merchants.Application;
using Merchants.Application.AdminControlPlane;
using Iam.Application.Roles;
using Iam.Application.ApiClients;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Persistence.ControlPlane.Admins;
using Persistence.ControlPlane.DataProtection;
using Persistence.ControlPlane.Iam;
using Governance.Application;
using Persistence.ControlPlane.Governance;
using IamRoleStore = Persistence.ControlPlane.Iam.RoleStore;
using Notifications.Application;
using Persistence.ControlPlane.Notifications;
using Payments.Application.AdminControlPlane;
using Payments.Application.Capabilities;
using Payments.Application.Ports;
using Accounts.Application;
using Persistence.ControlPlane.IdentityAccess;
using Persistence.ControlPlane.Merchants;
using Persistence.ControlPlane.Payments;
using Persistence.ControlPlane.Payments.Capabilities;
using Persistence.ControlPlane.Vault;
using ControlPlanePaymentAuthorizationSqlLockManager = Persistence.ControlPlane.Payments.PaymentAuthorizationSqlLockManager;
using Persistence.MerchantRuntime;

namespace Persistence.ControlPlane;

/// <summary>
/// The ONE public seam onto <see cref="ControlPlaneDbContext"/> (task 8.5.1, design.md "Assembly split + Api
/// host boundary" — no <c>InternalsVisibleTo(Api)</c>, so every adapter that names the internal context lives
/// in this assembly and is exposed here only through public application-layer ports). Registers the context
/// itself (unkeyed — ControlPlane carries no RLS session-context interceptor, unlike the pre-cutover
/// <c>PolDbContext</c>) plus every adapter over it.
/// </summary>
public static class ControlPlanePersistenceRegistration
{
    public static IServiceCollection AddControlPlanePersistence(
        this IServiceCollection services,
        string connectionString,
        Func<IServiceProvider, IWriteAuthorizer> authorizerFactory)
    {
        services.AddScoped(sp =>
        {
            var options = new DbContextOptionsBuilder<ControlPlaneDbContext>()
                .UseSqlServer(connectionString, sql => sql.UseCompatibilityLevel(170))
                .UseApplicationServiceProvider(sp)
                .AddInterceptors(new UserUpdatedAtInterceptor(sp.GetRequiredService<IClock>()))
                .Options;
            return new ControlPlaneDbContext(
                options,
                authorizerFactory(sp),
                sp.GetRequiredService<ISecurityTelemetry>(),
                sp.GetRequiredService<IActorContext>());
        });

        services.AddScoped<IDbContextFactory<ControlPlaneDbContext>>(sp =>
            new RuntimeControlPlaneDbContextFactory(
                connectionString,
                sp,
                sp.GetRequiredService<ISecurityTelemetry>()));

        services.AddScoped<IUserRepository>(sp => new UserRepository(
            sp.GetRequiredService<ControlPlaneDbContext>(),
            sp.GetRequiredService<ILogger<UserRepository>>(),
            sp.GetRequiredService<ISecurityTelemetry>(),
            sp.GetRequiredService<GovernanceSqlLockManager>()));
        services.AddScoped<IAdminIdentityRecoveryReader, ControlPlaneIdentityRecoveryReader>();
        services.AddScoped<IEmployeeProfileReader>(sp => new EmployeeProfileReader(
            sp.GetRequiredService<ControlPlaneDbContext>(), sp.GetRequiredService<ILogger<EmployeeProfileReader>>()));
        services.AddScoped<IAuditWriter>(sp => new AuditWriter(sp.GetRequiredService<ControlPlaneDbContext>()));
        services.AddScoped<ISessionStore>(sp => new SessionStore(
            sp.GetRequiredService<ControlPlaneDbContext>(), sp.GetRequiredService<ISecurityTelemetry>()));
        services.AddScoped<IAuthAuditWriter>(sp => new AuthAuditWriter(sp.GetRequiredService<ControlPlaneDbContext>()));
        services.AddScoped<IdentityAccessStore>();
        services.AddScoped<AgentRegistrationStore>();
        services.AddScoped<AccountAuthorizationLease>();
        services.AddScoped<IEmployeeJitStore>(sp => sp.GetRequiredService<IdentityAccessStore>());
        services.AddScoped<IRegistrationSessionStore>(sp => sp.GetRequiredService<IdentityAccessStore>());
        services.AddScoped<IAssertionReplayStore>(sp => sp.GetRequiredService<IdentityAccessStore>());
        services.AddScoped<IBffSessionStore>(sp => sp.GetRequiredService<IdentityAccessStore>());
        services.AddScoped<IRegistrationSessionLookup>(sp => sp.GetRequiredService<IdentityAccessStore>());
        services.AddScoped<IIdentityAccessQuery>(sp => sp.GetRequiredService<IdentityAccessStore>());
        services.AddScoped<IIdentityAccessAdminStore>(sp => sp.GetRequiredService<IdentityAccessStore>());
        services.AddScoped<IAgentRegistrationStore>(sp => sp.GetRequiredService<AgentRegistrationStore>());
        services.AddScoped<AgentRegistrationService>();
        services.AddScoped<EmployeeJitService>();
        services.AddScoped<RegistrationSessionService>();
        services.AddScoped<SystemClientAssertionService>();
        services.AddScoped<IRoleRepository>(sp => new RoleRepository(sp.GetRequiredService<ControlPlaneDbContext>()));

        services.AddScoped<IamRoleStore>();
        services.AddScoped<IRoleStore>(sp => sp.GetRequiredService<IamRoleStore>());
        services.AddScoped<IRoleAssignmentValidator>(sp => sp.GetRequiredService<IamRoleStore>());
        services.AddScoped<IApiClientStore>(sp => new ApiClientStore(
            sp.GetRequiredService<ControlPlaneDbContext>(),
            sp.GetRequiredService<IClock>(),
            sp.GetRequiredService<BuildingBlocks.Infrastructure.Vault.VaultKeyring>(),
            sp.GetRequiredService<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>(),
            sp.GetRequiredKeyedService<IUnitOfWork>("admin"),
            sp.GetRequiredService<ControlPlaneOperationExecutor>()));
        services.AddScoped<IApprovalDecisionExecutor>(sp => new ApiClientApprovalExecutor(
            sp.GetRequiredService<ControlPlaneDbContext>(),
            sp.GetRequiredService<IClock>(),
            sp.GetRequiredKeyedService<IUnitOfWork>("admin"),
            sp.GetRequiredService<BuildingBlocks.Infrastructure.Vault.VaultKeyring>(),
            sp.GetRequiredService<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>()));
        services.AddScoped<ISafeDestinationValidator, SafeDestinationValidator>();
        services.AddScoped<IBusinessWebhookConfigurationReader, BusinessWebhookConfigurationReader>();
        services.AddScoped<IDeliveryControlStore>(sp => new DeliveryControlStore(
            sp.GetRequiredService<ControlPlaneDbContext>(),
            sp.GetRequiredService<IClock>(),
            sp.GetRequiredService<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>(),
            sp.GetRequiredService<ISafeDestinationValidator>(),
            sp.GetRequiredKeyedService<IUnitOfWork>("admin"),
            sp.GetRequiredService<ControlPlaneOperationExecutor>(),
            sp.GetRequiredService<GovernanceAuditAppender>()));
        services.AddScoped<IDeliveryEventSink>(sp =>
            (DeliveryControlStore)sp.GetRequiredService<IDeliveryControlStore>());
        services.AddHostedService<WebhookDeliveryDispatcher>();

        services.AddKeyedScoped<IUnitOfWork>("admin", (sp, _) =>
            new ControlPlaneUnitOfWork(
                sp.GetRequiredService<ControlPlaneDbContext>(), sp.GetRequiredService<ISecurityTelemetry>()));
        services.AddScoped<IWorkforceTenantBindingStore>(sp => new WorkforceTenantBindingStore(
            sp.GetRequiredService<ControlPlaneDbContext>(),
            sp.GetRequiredKeyedService<IUnitOfWork>("admin"),
            sp.GetRequiredService<GovernanceSqlLockManager>()));

        services.AddScoped<IAdminRoleAssignmentCountReader>(sp =>
            new AdminRoleAssignmentCountReader(sp.GetRequiredService<ControlPlaneDbContext>()));

        services.AddScoped<IMerchantRoleReader>(sp =>
            new MerchantRoleReader(sp.GetRequiredService<ControlPlaneDbContext>()));

        services.AddScoped<GovernanceSqlLockManager>();
        services.AddScoped<GovernanceAuditAppender>();
        services.AddScoped<ControlPlanePaymentAuthorizationSqlLockManager>();
        services.AddScoped<IPaymentAuthorizationLockManager>(sp =>
            sp.GetRequiredService<ControlPlanePaymentAuthorizationSqlLockManager>());
        services.AddScoped<IMerchantRuntimeAuthorizationLease, MerchantRuntimeAuthorizationLease>();
        services.AddScoped<IVaultSecretStore, LocalEnvelopeVaultStore>();
        services.AddScoped<IVaultMaintenance, VaultMaintenance>();
        services.AddScoped<IVaultRevealAuditVerifier, VaultRevealAuditVerifier>();
        services.AddScoped<IVaultAuditAppender, VaultAuditAppender>();
        services.AddScoped<IVaultRevealAuditWriter, VaultRevealAuditAppenderAdapter>();
        services.AddScoped<AdminPaymentsControlStore>();
        services.AddScoped<IAdminPaymentsControlStore>(sp => sp.GetRequiredService<AdminPaymentsControlStore>());
        services.AddScoped<IAccountPaymentCapabilityControlStore>(sp => sp.GetRequiredService<AdminPaymentsControlStore>());
        services.AddScoped<ISimpleRoutingControlStore>(sp => sp.GetRequiredService<AdminPaymentsControlStore>());
        services.AddScoped<IEffectivePaymentCapabilityResolver, EffectivePaymentCapabilityResolver>();
        services.AddScoped<IPaymentCapabilityMigration, PaymentCapabilityMigrationService>();
        services.AddScoped<ILegacyPaymentRemediation, LegacyPaymentRemediationService>();
        services.AddScoped<IApprovalDecisionExecutor>(sp => new AdminPaymentsApprovalExecutor(
            sp.GetRequiredService<ControlPlaneDbContext>(),
            sp.GetRequiredService<IClock>(),
            sp.GetRequiredKeyedService<IUnitOfWork>("admin"),
            sp.GetRequiredService<IVaultSecretStore>(),
            sp.GetRequiredService<IPspAdapterFactory>(),
            sp.GetRequiredService<ISecurityTelemetry>(),
            sp.GetRequiredService<ControlPlanePaymentAuthorizationSqlLockManager>()));
        services.AddScoped<MerchantRepository>();
        services.AddScoped<IMerchantRepository>(sp => sp.GetRequiredService<MerchantRepository>());
        services.AddScoped<IMerchantDirectoryReader>(sp => sp.GetRequiredService<MerchantRepository>());
        services.AddScoped<IAdminMerchantControlStore, AdminMerchantControlStore>();
        services.AddScoped(sp => new ControlPlaneOperationExecutor(
            sp.GetRequiredService<ControlPlaneDbContext>(),
            sp.GetRequiredService<IClock>(),
            sp.GetRequiredKeyedService<IUnitOfWork>("admin"),
            sp.GetRequiredService<GovernanceSqlLockManager>()));
        services.AddScoped<IGlobalPaymentCapabilityControlStore>(sp =>
            new GlobalPaymentCapabilityControlStore(
                sp.GetRequiredService<ControlPlaneDbContext>(),
                sp.GetRequiredService<IClock>(),
                sp.GetRequiredService<IPspAdapterFactory>(),
                sp.GetRequiredService<ControlPlaneOperationExecutor>(),
                sp.GetRequiredService<ControlPlanePaymentAuthorizationSqlLockManager>()));
        services.AddScoped<IAdminOperationStore, AdminOperationStore>();
        services.AddScoped<IGovernanceStore>(sp => new GovernanceStore(
            sp.GetRequiredService<ControlPlaneDbContext>(),
            sp.GetRequiredKeyedService<IUnitOfWork>("admin"),
            sp.GetRequiredService<IClock>(),
            sp.GetRequiredService<IAuditAnchorStore>(),
            sp.GetRequiredService<GovernanceSqlLockManager>(),
            sp.GetRequiredService<GovernanceAuditAppender>()));

        services.AddSingleton<EfCoreXmlRepository>();
        services.AddSingleton<IXmlRepository>(sp => sp.GetRequiredService<EfCoreXmlRepository>());
        services.AddSingleton<IPersistedXmlRepository>(sp => sp.GetRequiredService<EfCoreXmlRepository>());

        return services;
    }
}
