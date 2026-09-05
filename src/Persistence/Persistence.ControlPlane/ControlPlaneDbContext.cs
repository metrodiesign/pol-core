using Admins.Domain.Roles;
using Admins.Domain.Users;
using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.DataProtection;
using BuildingBlocks.Infrastructure.Persistence;
using BuildingBlocks.Infrastructure.Provisioning;
using Iam.Domain.Permissions;
using Iam.Domain.Roles;
using Microsoft.EntityFrameworkCore;
using Persistence.ControlPlane.Admins;
using Persistence.ControlPlane.Iam;
using Governance.Domain;
using Persistence.ControlPlane.Governance;
using Iam.Domain.ApiClients;
using Notifications.Domain;
using Payments.Domain.Capabilities;
using Persistence.ControlPlane.Payments;

namespace Persistence.ControlPlane;

/// <summary>
/// Runtime context for the ControlPlane co-commit cluster — admin.* + iam.* + cfg.* + dbo.DataProtectionKeys
/// (rls-to-query-filter design.md "Context topology"; handlers 1-15 of the transaction inventory are
/// single-context here). <c>internal sealed</c>: only this assembly's host-registration extension may
/// construct it, so merchant-side code cannot even name this type (REQ-11.8). No migrations declared
/// here — <c>BuildingBlocks.Infrastructure.Persistence.PolDbContext</c> stays the single migration owner
/// and keeps the full relational model (real cross-context FKs); this context's entity configurations are
/// deliberately narrower (see each Configure() for what was dropped and why).
/// </summary>
internal sealed class ControlPlaneDbContext : GuardedRuntimeDbContext
{
    public ControlPlaneDbContext(
        DbContextOptions<ControlPlaneDbContext> options, IWriteAuthorizer authorizer, ISecurityTelemetry telemetry)
        : base(options, authorizer, telemetry) { }

    public DbSet<User> Users => Set<User>();
    public DbSet<WorkforceTenantBinding> WorkforceTenantBindings => Set<WorkforceTenantBinding>();
    public DbSet<MerchantAccess> MerchantAccess => Set<MerchantAccess>();
    public DbSet<Audit> UserAudits => Set<Audit>();
    public DbSet<Session> Sessions => Set<Session>();
    public DbSet<AuthAudit> AuthAudits => Set<AuthAudit>();
    public DbSet<RoleAssignment> RoleAssignments => Set<RoleAssignment>();

    public DbSet<Role> Roles => Set<Role>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<PermissionGroup> PermissionGroups => Set<PermissionGroup>();
    public DbSet<Permission> Permissions => Set<Permission>();


    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    public DbSet<ProvisioningOperation> ProvisioningOperations => Set<ProvisioningOperation>();

    public DbSet<ApprovalRequest> ApprovalRequests => Set<ApprovalRequest>();
    public DbSet<ApprovalEvent> ApprovalEvents => Set<ApprovalEvent>();
    public DbSet<OperationRecord> OperationRecords => Set<OperationRecord>();
    public DbSet<AuditHead> AuditHeads => Set<AuditHead>();
    public DbSet<AuditRecord> AuditRecords => Set<AuditRecord>();
    public DbSet<GovernanceOutboxMessage> GovernanceOutboxMessages => Set<GovernanceOutboxMessage>();
    public DbSet<ApiClient> ApiClients => Set<ApiClient>();
    public DbSet<OneTimeSecretTicket> OneTimeSecretTickets => Set<OneTimeSecretTicket>();
    public DbSet<WebhookEndpoint> WebhookEndpoints => Set<WebhookEndpoint>();
    public DbSet<WebhookDelivery> WebhookDeliveries => Set<WebhookDelivery>();
    public DbSet<NotificationRule> NotificationRules => Set<NotificationRule>();
    public DbSet<NotificationDelivery> NotificationDeliveries => Set<NotificationDelivery>();
    public DbSet<DeliverySecretVersion> DeliverySecretVersions => Set<DeliverySecretVersion>();

    public DbSet<PaymentMethod> PaymentMethods => Set<PaymentMethod>();
    public DbSet<PaymentMethodOptionGroup> PaymentMethodOptionGroups => Set<PaymentMethodOptionGroup>();
    public DbSet<PaymentMethodOption> PaymentMethodOptions => Set<PaymentMethodOption>();
    public DbSet<PaymentProvider> PaymentProviders => Set<PaymentProvider>();
    public DbSet<PaymentProviderMethod> PaymentProviderMethods => Set<PaymentProviderMethod>();
    public DbSet<PaymentProviderMethodOption> PaymentProviderMethodOptions => Set<PaymentProviderMethodOption>();
    public DbSet<PaymentAuthorizationState> PaymentAuthorizationStates => Set<PaymentAuthorizationState>();
    public DbSet<PaymentCapabilityMigrationConflict> PaymentCapabilityMigrationConflicts =>
        Set<PaymentCapabilityMigrationConflict>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new WorkforceTenantBindingConfiguration());
        modelBuilder.ApplyConfiguration(new UserConfiguration());
        modelBuilder.ApplyConfiguration(new MerchantAccessConfiguration());
        modelBuilder.ApplyConfiguration(new AuditConfiguration());
        modelBuilder.ApplyConfiguration(new SessionConfiguration());
        modelBuilder.ApplyConfiguration(new AuthAuditConfiguration());
        modelBuilder.ApplyConfiguration(new RoleAssignmentConfiguration());

        modelBuilder.ApplyConfiguration(new RoleConfiguration());
        modelBuilder.ApplyConfiguration(new RolePermissionConfiguration());
        modelBuilder.ApplyConfiguration(new PermissionGroupConfiguration());
        modelBuilder.ApplyConfiguration(new PermissionConfiguration());

        modelBuilder.ApplyConfiguration(new DataProtectionKeyConfiguration());
        modelBuilder.ApplyConfiguration(new ProvisioningOperationConfiguration());

        modelBuilder.ApplyConfiguration(new ApprovalRequestConfiguration());
        modelBuilder.ApplyConfiguration(new ApprovalEventConfiguration());
        modelBuilder.ApplyConfiguration(new OperationRecordConfiguration());
        modelBuilder.ApplyConfiguration(new AuditHeadConfiguration());
        modelBuilder.ApplyConfiguration(new AuditRecordConfiguration());
        modelBuilder.ApplyConfiguration(new GovernanceOutboxMessageConfiguration());
        modelBuilder.ApplyConfiguration(new Iam.ApiClientConfiguration());
        modelBuilder.ApplyConfiguration(new Iam.OneTimeSecretTicketConfiguration());
        modelBuilder.ApplyConfiguration(new Notifications.WebhookEndpointConfiguration());
        modelBuilder.ApplyConfiguration(new Notifications.WebhookDeliveryConfiguration());
        modelBuilder.ApplyConfiguration(new Notifications.NotificationRuleConfiguration());
        modelBuilder.ApplyConfiguration(new Notifications.NotificationDeliveryConfiguration());
        modelBuilder.ApplyConfiguration(new Notifications.DeliverySecretVersionConfiguration());
        modelBuilder.ApplyConfiguration(new PaymentMethodConfiguration());
        modelBuilder.ApplyConfiguration(new PaymentMethodOptionGroupConfiguration());
        modelBuilder.ApplyConfiguration(new PaymentMethodOptionConfiguration());
        modelBuilder.ApplyConfiguration(new PaymentProviderConfiguration());
        modelBuilder.ApplyConfiguration(new PaymentProviderMethodConfiguration());
        modelBuilder.ApplyConfiguration(new PaymentProviderMethodOptionConfiguration());
        modelBuilder.ApplyConfiguration(new PaymentAuthorizationStateConfiguration(this));
        modelBuilder.ApplyConfiguration(new PaymentCapabilityMigrationConflictConfiguration());

        base.OnModelCreating(modelBuilder);
    }
}
