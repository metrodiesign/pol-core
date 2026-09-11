using Admins.Domain.Roles;
using Admins.Domain.Users;
using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.DataProtection;
using BuildingBlocks.Infrastructure.Outbox;
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
using Accounts.Domain;
using Access.Domain;
using Merchants.Domain.Users;
using Merchants.Domain.Users.Roles;
using AdminMerchantAccess = Admins.Domain.Users.MerchantAccess;
using AccountMerchantAccess = Access.Domain.MerchantAccess;
using MerchantAccount = Merchants.Domain.Users.User;
using MerchantSession = Merchants.Domain.Users.Session;
using MerchantExternalLogin = Merchants.Domain.Users.ExternalLogin;
using MerchantAuthAudit = Merchants.Domain.Users.AuthAudit;
using MerchantRegistrationAudit = Merchants.Domain.Users.RegistrationAudit;
using MerchantRegistrationAttempt = Merchants.Domain.Users.RegistrationAttempt;
using MerchantRegistrationNotice = Merchants.Domain.Users.RegistrationNotice;
using MerchantRoleAssignment = Merchants.Domain.Users.Roles.RoleAssignment;
using MerchantUserInvitation = Merchants.Domain.Users.MerchantUserInvitation;
using MerchantUserManagementAudit = Merchants.Domain.Users.MerchantUserManagementAudit;
using MerchantAdminUserOperationRecord = Merchants.Domain.Users.AdminUserOperationRecord;
using MerchantUserOutbox = BuildingBlocks.Infrastructure.Outbox.MerchantUserOutbox;
using Merchant = Merchants.Domain.Merchant;
using Branch = Merchants.Domain.Branch;
using Sale = Merchants.Domain.Sale;
using Originator = Merchants.Domain.Originator;
using Connection = Payments.Domain.Psp.Connection;
using RoutingRuleset = Payments.Domain.Routing.RoutingRuleset;
using RoutingRule = Payments.Domain.Routing.RoutingRule;
using MerchantProviderAccountMethod = Payments.Domain.Capabilities.MerchantProviderAccountMethod;
using MerchantProviderAccountMethodOption = Payments.Domain.Capabilities.MerchantProviderAccountMethodOption;
using MerchantPaymentMethod = Payments.Domain.Capabilities.MerchantPaymentMethod;
using MerchantUserPaymentMethod = Payments.Domain.Capabilities.MerchantUserPaymentMethod;
using VaultSecretBlob = BuildingBlocks.Infrastructure.Vault.VaultSecretBlob;
using VaultSecretVersion = BuildingBlocks.Infrastructure.Vault.VaultSecretVersion;
using VaultRevealAudit = BuildingBlocks.Infrastructure.Vault.VaultRevealAudit;
using ProvisioningAudit = Merchants.Domain.ProvisioningAudit;
using ApprovalExecutionRecord = Payments.Domain.ApprovalExecutionRecord;

namespace Persistence.ControlPlane;

/// <summary>
/// Runtime context for the ControlPlane co-commit cluster — admin.* + iam.* + cfg.* + merchant identity + dbo.DataProtectionKeys
/// (rls-to-query-filter design.md "Context topology"; handlers 1-15 of the transaction inventory are
/// single-context here). <c>internal sealed</c>: only this assembly's host-registration extension may
/// construct it, so merchant-side code cannot even name this type (REQ-11.8). No migrations declared
/// here — <c>BuildingBlocks.Infrastructure.Persistence.PolDbContext</c> stays the single migration owner
/// and keeps the full relational model (real cross-context FKs); this context's entity configurations are
/// deliberately narrower (see each Configure() for what was dropped and why). Merchant identity is part of
/// this context so runtime persistence has exactly two contexts: Control Plane and Commerce. The old
/// <c>MerchantUserDbContext</c> is retained only as a source-compatibility test wrapper and is never registered.
/// </summary>
internal sealed class ControlPlaneDbContext : GuardedRuntimeDbContext, IMerchantFilterContext
{
    private readonly IActorContext? _actor;

    public ControlPlaneDbContext(
        DbContextOptions options, IWriteAuthorizer authorizer, ISecurityTelemetry telemetry,
        IActorContext? actor = null)
        : base(options, authorizer, telemetry)
        => _actor = actor;

    // Keeps the former merchant-identity fixture argument order source-compatible while the runtime
    // model has a single sealed Control Plane owner for both admin and merchant identity entities.
    public ControlPlaneDbContext(
        DbContextOptions<ControlPlaneDbContext> options, IActorContext actor,
        IWriteAuthorizer authorizer, ISecurityTelemetry telemetry)
        : this((DbContextOptions)options, authorizer, telemetry, actor) { }

    /// <summary>Merchant identity filters are evaluated from the current scoped actor. A context created
    /// without an actor is deny-by-default.</summary>
    public Guid CurrentMerchant => _actor is { HasActor: true } actor ? actor.MerchantId : Guid.Empty;

    public DbSet<global::Admins.Domain.Users.User> Users => Set<global::Admins.Domain.Users.User>();
    public DbSet<WorkforceTenantBinding> WorkforceTenantBindings => Set<WorkforceTenantBinding>();
    public DbSet<AdminMerchantAccess> MerchantAccess => Set<AdminMerchantAccess>();
    public DbSet<Audit> UserAudits => Set<Audit>();
    public DbSet<global::Admins.Domain.Users.Session> Sessions => Set<global::Admins.Domain.Users.Session>();
    public DbSet<global::Admins.Domain.Users.AuthAudit> AuthAudits => Set<global::Admins.Domain.Users.AuthAudit>();
    public DbSet<global::Admins.Domain.Roles.RoleAssignment> RoleAssignments => Set<global::Admins.Domain.Roles.RoleAssignment>();

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

    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<LoginAccount> LoginAccounts => Set<LoginAccount>();
    public DbSet<Employee> Employees => Set<Employee>();
    public DbSet<Agent> Agents => Set<Agent>();
    public DbSet<SystemClient> SystemClients => Set<SystemClient>();
    public DbSet<ClientKeyPolicy> ClientKeyPolicies => Set<ClientKeyPolicy>();
    public DbSet<AssertionReplay> AssertionReplays => Set<AssertionReplay>();
    public DbSet<BffSessionTicket> BffSessionTickets => Set<BffSessionTicket>();
    public DbSet<RegistrationSession> RegistrationSessions => Set<RegistrationSession>();
    public DbSet<AgentRegistration> AgentRegistrations => Set<AgentRegistration>();
    public DbSet<AgentRegistrationAttempt> AgentRegistrationAttempts => Set<AgentRegistrationAttempt>();

    public DbSet<AccountMerchantAccess> AccountMerchantAccess => Set<AccountMerchantAccess>();
    public DbSet<AccessRole> AccessRoles => Set<AccessRole>();
    public DbSet<BranchAccess> BranchAccess => Set<BranchAccess>();
    public DbSet<PlatformAccess> PlatformAccess => Set<PlatformAccess>();
    public DbSet<PlatformAccessRole> PlatformAccessRoles => Set<PlatformAccessRole>();
    public DbSet<SystemClientScope> SystemClientScopes => Set<SystemClientScope>();
    public DbSet<MerchantAccessMethod> MerchantAccessMethods => Set<MerchantAccessMethod>();

    // Merchant identity is owned by the Control Plane runtime context. Distinct property names keep the
    // admin and merchant-user CLR types explicit while the physical tables remain single-owner mappings.
    public DbSet<MerchantAccount> MerchantUsers => Set<MerchantAccount>();
    public DbSet<MerchantSession> MerchantUserSessions => Set<MerchantSession>();
    public DbSet<MerchantExternalLogin> MerchantExternalLogins => Set<MerchantExternalLogin>();
    public DbSet<MerchantAuthAudit> MerchantAuthAudits => Set<MerchantAuthAudit>();
    public DbSet<MerchantRegistrationAudit> MerchantRegistrationAudits => Set<MerchantRegistrationAudit>();
    public DbSet<MerchantRegistrationAttempt> MerchantRegistrationAttempts => Set<MerchantRegistrationAttempt>();
    public DbSet<MerchantRegistrationNotice> MerchantRegistrationNotices => Set<MerchantRegistrationNotice>();
    public DbSet<MerchantRoleAssignment> MerchantRoleAssignments => Set<MerchantRoleAssignment>();
    public DbSet<MerchantUserInvitation> MerchantUserInvitations => Set<MerchantUserInvitation>();
    public DbSet<MerchantUserManagementAudit> MerchantUserManagementAudits => Set<MerchantUserManagementAudit>();
    public DbSet<MerchantAdminUserOperationRecord> MerchantAdminUserOperationRecords =>
        Set<MerchantAdminUserOperationRecord>();
    public DbSet<MerchantUserOutbox> MerchantUserOutbox => Set<MerchantUserOutbox>();

    public DbSet<Merchant> Merchants => Set<Merchant>();
    public DbSet<Branch> Branches => Set<Branch>();
    public DbSet<Sale> Sales => Set<Sale>();
    public DbSet<Originator> Originators => Set<Originator>();
    public DbSet<Connection> PspConnections => Set<Connection>();
    public DbSet<RoutingRuleset> RoutingRulesets => Set<RoutingRuleset>();
    public DbSet<RoutingRule> RoutingRules => Set<RoutingRule>();
    public DbSet<MerchantProviderAccountMethod> MerchantProviderAccountMethods => Set<MerchantProviderAccountMethod>();
    public DbSet<MerchantProviderAccountMethodOption> MerchantProviderAccountMethodOptions => Set<MerchantProviderAccountMethodOption>();
    public DbSet<MerchantPaymentMethod> MerchantPaymentMethods => Set<MerchantPaymentMethod>();
    public DbSet<MerchantUserPaymentMethod> MerchantUserPaymentMethods => Set<MerchantUserPaymentMethod>();
    public DbSet<VaultSecretBlob> VaultSecrets => Set<VaultSecretBlob>();
    public DbSet<VaultSecretVersion> VaultSecretVersions => Set<VaultSecretVersion>();
    public DbSet<VaultRevealAudit> VaultRevealAudits => Set<VaultRevealAudit>();
    public DbSet<ProvisioningAudit> ProvisioningAudits => Set<ProvisioningAudit>();
    public DbSet<ApprovalExecutionRecord> ApprovalExecutionRecords => Set<ApprovalExecutionRecord>();


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
        modelBuilder.ApplyConfiguration(new global::Payments.Infrastructure.Persistence.Capabilities.PaymentMethodConfiguration());
        modelBuilder.ApplyConfiguration(new global::Payments.Infrastructure.Persistence.Capabilities.PaymentMethodOptionGroupConfiguration());
        modelBuilder.ApplyConfiguration(new global::Payments.Infrastructure.Persistence.Capabilities.PaymentMethodOptionConfiguration());
        modelBuilder.ApplyConfiguration(new global::Payments.Infrastructure.Persistence.Capabilities.PaymentProviderConfiguration());
        modelBuilder.ApplyConfiguration(new global::Payments.Infrastructure.Persistence.Capabilities.PaymentProviderMethodConfiguration());
        modelBuilder.ApplyConfiguration(new global::Payments.Infrastructure.Persistence.Capabilities.PaymentProviderMethodOptionConfiguration());
        modelBuilder.ApplyConfiguration(new global::Payments.Infrastructure.Persistence.Capabilities.PaymentAuthorizationStateConfiguration(Database.ProviderName));
        modelBuilder.ApplyConfiguration(new global::Payments.Infrastructure.Persistence.Capabilities.PaymentCapabilityMigrationConflictConfiguration());

        modelBuilder.ApplyConfiguration(new Accounts.Infrastructure.Persistence.AccountConfiguration());
        modelBuilder.ApplyConfiguration(new Accounts.Infrastructure.Persistence.LoginAccountConfiguration());
        modelBuilder.ApplyConfiguration(new Accounts.Infrastructure.Persistence.EmployeeConfiguration());
        modelBuilder.ApplyConfiguration(new Accounts.Infrastructure.Persistence.AgentConfiguration());
        modelBuilder.ApplyConfiguration(new Accounts.Infrastructure.Persistence.SystemClientConfiguration());
        modelBuilder.ApplyConfiguration(new Accounts.Infrastructure.Persistence.ClientKeyPolicyConfiguration());
        modelBuilder.ApplyConfiguration(new Accounts.Infrastructure.Persistence.AssertionReplayConfiguration());
        modelBuilder.ApplyConfiguration(new Accounts.Infrastructure.Persistence.BffSessionTicketConfiguration());
        modelBuilder.ApplyConfiguration(new Accounts.Infrastructure.Persistence.RegistrationSessionConfiguration());
        modelBuilder.ApplyConfiguration(new Accounts.Infrastructure.Persistence.AgentRegistrationConfiguration());
        modelBuilder.ApplyConfiguration(new Accounts.Infrastructure.Persistence.AgentRegistrationAttemptConfiguration());
        modelBuilder.ApplyConfiguration(new Access.Infrastructure.Persistence.MerchantAccessConfiguration());
        modelBuilder.ApplyConfiguration(new Access.Infrastructure.Persistence.AccessRoleConfiguration());
        modelBuilder.ApplyConfiguration(new Access.Infrastructure.Persistence.BranchAccessConfiguration());
        modelBuilder.ApplyConfiguration(new Access.Infrastructure.Persistence.PlatformAccessConfiguration());
        modelBuilder.ApplyConfiguration(new Access.Infrastructure.Persistence.PlatformAccessRoleConfiguration());
        modelBuilder.ApplyConfiguration(new Access.Infrastructure.Persistence.SystemClientScopeConfiguration());
        modelBuilder.ApplyConfiguration(new Access.Infrastructure.Persistence.MerchantAccessMethodConfiguration());

        modelBuilder.ApplyConfiguration(new global::Merchants.Infrastructure.Persistence.MerchantConfiguration());
        modelBuilder.ApplyConfiguration(new global::Merchants.Infrastructure.Persistence.BranchConfiguration());
        modelBuilder.ApplyConfiguration(new global::Merchants.Infrastructure.Persistence.SaleConfiguration());
        modelBuilder.ApplyConfiguration(new global::Merchants.Infrastructure.Persistence.OriginatorConfiguration());
        modelBuilder.ApplyConfiguration(new global::Merchants.Infrastructure.Persistence.ProvisioningAuditConfiguration());
        modelBuilder.ApplyConfiguration(new global::Payments.Infrastructure.Persistence.Psp.ConnectionConfiguration());
        modelBuilder.ApplyConfiguration(new global::Payments.Infrastructure.Persistence.Routing.RoutingRulesetConfiguration());
        modelBuilder.ApplyConfiguration(new global::Payments.Infrastructure.Persistence.Routing.RoutingRuleConfiguration());
        modelBuilder.ApplyConfiguration(new global::Payments.Infrastructure.Persistence.Capabilities.MerchantProviderAccountMethodConfiguration());
        modelBuilder.ApplyConfiguration(new global::Payments.Infrastructure.Persistence.Capabilities.MerchantProviderAccountMethodOptionConfiguration());
        modelBuilder.ApplyConfiguration(new global::Payments.Infrastructure.Persistence.Capabilities.MerchantPaymentMethodConfiguration());
        modelBuilder.ApplyConfiguration(new global::Payments.Infrastructure.Persistence.Capabilities.MerchantUserPaymentMethodConfiguration());
        modelBuilder.ApplyConfiguration(new BuildingBlocks.Infrastructure.Vault.VaultSecretBlobConfiguration());
        modelBuilder.ApplyConfiguration(new BuildingBlocks.Infrastructure.Persistence.VaultSecretVersionConfiguration());
        modelBuilder.ApplyConfiguration(new BuildingBlocks.Infrastructure.Vault.VaultRevealAuditConfiguration());
        modelBuilder.ApplyConfiguration(new global::Payments.Infrastructure.Persistence.ApprovalExecutionRecordConfiguration());
        modelBuilder.Entity<Connection>()
            .HasOne<Merchant>().WithMany().HasForeignKey(x => x.MerchantId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<Connection>()
            .HasOne<VaultSecretVersion>().WithMany()
            .HasForeignKey(x => new { x.MerchantId, x.ActiveSecretVersionId })
            .HasPrincipalKey(x => new { x.MerchantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<Connection>()
            .HasOne<VaultSecretVersion>().WithMany()
            .HasForeignKey(x => new { x.MerchantId, x.PendingSecretVersionId })
            .HasPrincipalKey(x => new { x.MerchantId, x.Id }).OnDelete(DeleteBehavior.Restrict);


        // One configuration owner for merchant identity. The same types are reused by PolDbContext for
        // design-time migrations, so columns/indexes/constraints are not duplicated across contexts.
        modelBuilder.ApplyConfiguration(new Persistence.MerchantUsers.Users.UserConfiguration(this));
        modelBuilder.ApplyConfiguration(new Persistence.MerchantUsers.Users.ExternalLoginConfiguration());
        modelBuilder.ApplyConfiguration(new Persistence.MerchantUsers.Users.RegistrationAuditConfiguration());
        modelBuilder.ApplyConfiguration(new Persistence.MerchantUsers.Users.RegistrationAttemptConfiguration());
        modelBuilder.ApplyConfiguration(new Persistence.MerchantUsers.Users.RegistrationNoticeConfiguration());
        modelBuilder.ApplyConfiguration(new Persistence.MerchantUsers.Users.SessionConfiguration());
        modelBuilder.ApplyConfiguration(new Persistence.MerchantUsers.Users.AuthAuditConfiguration());
        modelBuilder.ApplyConfiguration(new Persistence.MerchantUsers.Users.RoleAssignmentConfiguration(this));
        modelBuilder.ApplyConfiguration(new Persistence.MerchantUsers.Users.MerchantUserInvitationConfiguration(this));
        modelBuilder.ApplyConfiguration(new Persistence.MerchantUsers.Users.AdminUserOperationRecordConfiguration(this));
        modelBuilder.ApplyConfiguration(new Persistence.MerchantUsers.Users.MerchantUserManagementAuditConfiguration(this));
        modelBuilder.ApplyConfiguration(new BuildingBlocks.Infrastructure.Outbox.MerchantUserOutboxConfiguration());

        // SQLite has no schema namespace and is used only by the architecture/unit harness. Keep its
        // control-plane and merchant identity tables distinct there; SQL Server retains the canonical names
        // under admin/merch schemas.
        if (Database.ProviderName?.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) == true)
        {
            modelBuilder.Entity<MerchantAccount>().ToTable("MerchantUsers", SchemaNames.Merch);
            modelBuilder.Entity<MerchantSession>().ToTable("MerchantUserSessions", SchemaNames.Merch);
            modelBuilder.Entity<MerchantAuthAudit>().ToTable("MerchantAuthAudits", SchemaNames.Merch);
            modelBuilder.Entity<MerchantRoleAssignment>().ToTable("MerchantRoleAssignments", SchemaNames.Merch);
            modelBuilder.Entity<AdminMerchantAccess>().ToTable("AdminMerchantAccess", SchemaNames.Admin);
            modelBuilder.Entity<AccountMerchantAccess>().ToTable("AccountMerchantAccess", SchemaNames.Access);

            foreach (var property in modelBuilder.Model.GetEntityTypes().SelectMany(entity => entity.GetProperties()))
            {
                if (property.GetColumnType()?.Contains("(max)", StringComparison.OrdinalIgnoreCase) == true)
                    property.SetColumnType("TEXT");
            }
        }

        modelBuilder.Entity<MerchantUserOutbox>().HasQueryFilter(x => x.MerchantId == CurrentMerchant);
        modelBuilder.Entity<MerchantProviderAccountMethod>().HasQueryFilter(x => x.MerchantId == CurrentMerchant);
        modelBuilder.Entity<MerchantProviderAccountMethodOption>().HasQueryFilter(x => x.MerchantId == CurrentMerchant);
        modelBuilder.Entity<MerchantPaymentMethod>().HasQueryFilter(x => x.MerchantId == CurrentMerchant);
        modelBuilder.Entity<MerchantUserPaymentMethod>().HasQueryFilter(x => x.MerchantId == CurrentMerchant);
        modelBuilder.Entity<ApprovalExecutionRecord>().HasQueryFilter(x => x.MerchantId == CurrentMerchant);
        modelBuilder.Entity<Merchant>().HasQueryFilter(x => x.Id == CurrentMerchant);
        modelBuilder.Entity<Branch>().HasQueryFilter(x => x.MerchantId == CurrentMerchant);
        modelBuilder.Entity<Sale>().HasQueryFilter(x => x.MerchantId == CurrentMerchant);
        modelBuilder.Entity<Originator>().HasQueryFilter(x => x.MerchantId == CurrentMerchant);
        modelBuilder.Entity<VaultSecretBlob>().HasQueryFilter(x => x.MerchantId == CurrentMerchant);
        modelBuilder.Entity<VaultSecretVersion>().HasQueryFilter(x => x.MerchantId == CurrentMerchant);
        modelBuilder.Entity<VaultRevealAudit>().HasQueryFilter(x => x.MerchantId == CurrentMerchant);
        modelBuilder.Entity<ProvisioningAudit>().HasQueryFilter(x => x.MerchantId == CurrentMerchant);

        TenantKeyDescriptor.Require(modelBuilder.Entity<MerchantProviderAccountMethod>().Metadata, nameof(MerchantProviderAccountMethod.MerchantId));
        TenantKeyDescriptor.Require(modelBuilder.Entity<MerchantProviderAccountMethodOption>().Metadata, nameof(MerchantProviderAccountMethodOption.MerchantId));
        TenantKeyDescriptor.Require(modelBuilder.Entity<MerchantPaymentMethod>().Metadata, nameof(MerchantPaymentMethod.MerchantId));
        TenantKeyDescriptor.Require(modelBuilder.Entity<MerchantUserPaymentMethod>().Metadata, nameof(MerchantUserPaymentMethod.MerchantId));
        TenantKeyDescriptor.Require(modelBuilder.Entity<ApprovalExecutionRecord>().Metadata, nameof(ApprovalExecutionRecord.MerchantId));
        TenantKeyDescriptor.Require(modelBuilder.Entity<Merchant>().Metadata, nameof(Merchant.Id));
        TenantKeyDescriptor.Require(modelBuilder.Entity<Branch>().Metadata, nameof(Branch.MerchantId));
        TenantKeyDescriptor.Require(modelBuilder.Entity<Sale>().Metadata, nameof(Sale.MerchantId));
        TenantKeyDescriptor.Require(modelBuilder.Entity<Originator>().Metadata, nameof(Originator.MerchantId));
        TenantKeyDescriptor.Require(modelBuilder.Entity<VaultSecretBlob>().Metadata, nameof(VaultSecretBlob.MerchantId));
        TenantKeyDescriptor.Require(modelBuilder.Entity<VaultSecretVersion>().Metadata, nameof(VaultSecretVersion.MerchantId));
        TenantKeyDescriptor.Require(modelBuilder.Entity<VaultRevealAudit>().Metadata, nameof(VaultRevealAudit.MerchantId));
        TenantKeyDescriptor.Require(modelBuilder.Entity<ProvisioningAudit>().Metadata, nameof(ProvisioningAudit.MerchantId));

        OpenIddictRegistration.ApplyModel(modelBuilder);

        base.OnModelCreating(modelBuilder);
    }
}
