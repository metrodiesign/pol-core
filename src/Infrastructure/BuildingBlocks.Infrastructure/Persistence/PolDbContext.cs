using BuildingBlocks.Infrastructure.DataProtection;
using BuildingBlocks.Infrastructure.Idempotency;
using BuildingBlocks.Infrastructure.Outbox;
using BuildingBlocks.Infrastructure.Vault;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Persistence.ControlPlane;

namespace BuildingBlocks.Infrastructure.Persistence;

/// <summary>
/// The one DbContext for the whole platform (T1 — one connection = one SESSION_CONTEXT stamp = one
/// transaction = one migration history). Every entity maps its own schema explicitly via
/// <c>ToTable(name, schema)</c> — there is no <c>HasDefaultSchema</c> (rf1 multi-schema layout,
/// REQ-1.2). Module entity mappings are discovered at model-build time from
/// <see cref="ModuleAssemblies.ModuleNamespaces"/>, so this context keeps no compile-time dependency on any
/// module. The DB catalog itself stays named <c>VCentralPay</c> (T2) — only the connection string
/// names it; nothing in code references the catalog name.
/// </summary>
public sealed class PolDbContext : DbContext, IMerchantFilterContext
{
    private readonly ModuleAssemblies _modules;

    public PolDbContext(DbContextOptions<PolDbContext> options, ModuleAssemblies modules)
        : base(options)
        => _modules = modules;

    internal string ModuleCacheKey => string.Join("|", _modules.ModuleNamespaces.Order(StringComparer.Ordinal));

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
        optionsBuilder.ReplaceService<IModelCacheKeyFactory, PolModuleModelCacheKeyFactory>();

    // Migration composition never serves a tenant request. Returning the deny-default sentinel keeps the
    // shared filtered configuration deterministic while the context remains design-time only.
    public Guid CurrentMerchant => Guid.Empty;

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<MerchantUserOutbox> MerchantUserOutbox => Set<MerchantUserOutbox>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();
    public DbSet<VaultSecretBlob> VaultSecrets => Set<VaultSecretBlob>();
    public DbSet<VaultSecretVersion> VaultSecretVersions => Set<VaultSecretVersion>();
    public DbSet<VaultRevealAudit> VaultRevealAudits => Set<VaultRevealAudit>();
    public DbSet<AdminOperationRecord> AdminOperationRecords => Set<AdminOperationRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new OutboxMessageConfiguration());
        modelBuilder.ApplyConfiguration(new MerchantUserOutboxConfiguration());
        modelBuilder.ApplyConfiguration(new IdempotencyRecordConfiguration());
        modelBuilder.ApplyConfiguration(new VaultSecretBlobConfiguration());
        modelBuilder.ApplyConfiguration(new VaultSecretVersionConfiguration());
        modelBuilder.ApplyConfiguration(new VaultRevealAuditConfiguration());
        modelBuilder.ApplyConfiguration(new AdminOperationRecordConfiguration());
        modelBuilder.ApplyConfiguration(new global::Persistence.MerchantUsers.Users.UserConfiguration(this));
        modelBuilder.ApplyConfiguration(new global::Persistence.MerchantUsers.Users.ExternalLoginConfiguration());
        modelBuilder.ApplyConfiguration(new global::Persistence.MerchantUsers.Users.RegistrationAuditConfiguration());
        modelBuilder.ApplyConfiguration(new global::Persistence.MerchantUsers.Users.RegistrationAttemptConfiguration());
        modelBuilder.ApplyConfiguration(new global::Persistence.MerchantUsers.Users.RegistrationNoticeConfiguration());
        modelBuilder.ApplyConfiguration(new global::Persistence.MerchantUsers.Users.SessionConfiguration());
        modelBuilder.ApplyConfiguration(new global::Persistence.MerchantUsers.Users.AuthAuditConfiguration());
        modelBuilder.ApplyConfiguration(new global::Persistence.MerchantUsers.Users.RoleAssignmentConfiguration(this));
        modelBuilder.ApplyConfiguration(new global::Persistence.MerchantUsers.Users.MerchantUserInvitationConfiguration(this));
        modelBuilder.ApplyConfiguration(new global::Persistence.MerchantUsers.Users.AdminUserOperationRecordConfiguration(this));
        modelBuilder.ApplyConfiguration(new global::Persistence.MerchantUsers.Users.MerchantUserManagementAuditConfiguration(this));
        modelBuilder.ApplyConfiguration(new BuildingBlocks.Infrastructure.Outbox.MerchantUserOutboxConfiguration());
        modelBuilder.ApplyConfiguration(new global::Persistence.ControlPlane.Admins.WorkforceTenantBindingConfiguration());
        modelBuilder.ApplyConfiguration(new global::Persistence.ControlPlane.Admins.UserConfiguration());
        modelBuilder.ApplyConfiguration(new global::Persistence.ControlPlane.Admins.MerchantAccessConfiguration());
        modelBuilder.ApplyConfiguration(new global::Persistence.ControlPlane.Admins.AuditConfiguration());
        modelBuilder.ApplyConfiguration(new global::Persistence.ControlPlane.Admins.SessionConfiguration());
        modelBuilder.ApplyConfiguration(new global::Persistence.ControlPlane.Admins.AuthAuditConfiguration());
        modelBuilder.ApplyConfiguration(new global::Persistence.ControlPlane.Admins.RoleAssignmentConfiguration());
        modelBuilder.ApplyConfiguration(new global::Persistence.ControlPlane.Admins.ProvisioningOperationConfiguration());
        modelBuilder.ApplyConfiguration(new global::Persistence.ControlPlane.Governance.ApprovalRequestConfiguration());
        modelBuilder.ApplyConfiguration(new global::Persistence.ControlPlane.Governance.ApprovalEventConfiguration());
        modelBuilder.ApplyConfiguration(new global::Persistence.ControlPlane.Governance.OperationRecordConfiguration());
        modelBuilder.ApplyConfiguration(new global::Persistence.ControlPlane.Governance.AuditHeadConfiguration());
        modelBuilder.ApplyConfiguration(new global::Persistence.ControlPlane.Governance.AuditRecordConfiguration());
        modelBuilder.ApplyConfiguration(new global::Persistence.ControlPlane.Governance.GovernanceOutboxMessageConfiguration());
        modelBuilder.ApplyConfiguration(new global::Persistence.ControlPlane.Iam.ApiClientConfiguration());
        modelBuilder.ApplyConfiguration(new global::Persistence.ControlPlane.Iam.OneTimeSecretTicketConfiguration());
        modelBuilder.ApplyConfiguration(new global::Persistence.ControlPlane.Iam.PermissionGroupConfiguration());
        modelBuilder.ApplyConfiguration(new global::Persistence.ControlPlane.Iam.PermissionConfiguration());
        modelBuilder.ApplyConfiguration(new global::Persistence.ControlPlane.Iam.RoleConfiguration());
        modelBuilder.ApplyConfiguration(new global::Persistence.ControlPlane.Iam.RolePermissionConfiguration());
        modelBuilder.ApplyConfiguration(new global::Persistence.ControlPlane.Notifications.WebhookEndpointConfiguration());
        modelBuilder.ApplyConfiguration(new global::Persistence.ControlPlane.Notifications.WebhookDeliveryConfiguration());
        modelBuilder.ApplyConfiguration(new global::Persistence.ControlPlane.Notifications.NotificationRuleConfiguration());
        modelBuilder.ApplyConfiguration(new global::Persistence.ControlPlane.Notifications.NotificationDeliveryConfiguration());
        modelBuilder.ApplyConfiguration(new global::Persistence.ControlPlane.Notifications.DeliverySecretVersionConfiguration());
        modelBuilder.ApplyConfiguration(new global::Accounts.Infrastructure.Persistence.AgentConfiguration());
        modelBuilder.ApplyConfiguration(new global::Payments.Infrastructure.Persistence.Capabilities.PaymentAuthorizationStateConfiguration(Database.ProviderName));
        modelBuilder.ApplyConfiguration(new Payments.Infrastructure.Persistence.TransactionConfiguration());
        modelBuilder.ApplyConfiguration(new Payments.Infrastructure.Persistence.TransactionEventConfiguration());
        modelBuilder.ApplyConfiguration(new DataProtectionKeyConfiguration());
        OpenIddictRegistration.ApplyModel(modelBuilder);

        modelBuilder.ApplyConfigurationsFromAssembly(
            typeof(PolDbContext).Assembly,
            type => _modules.ModuleNamespaces.Any(moduleNamespace =>
                string.Equals(type.Namespace, moduleNamespace, StringComparison.Ordinal)
                || type.Namespace?.StartsWith(moduleNamespace + ".", StringComparison.Ordinal) == true));

        // Migration-only relational overlay. Runtime contexts reuse the same scalar/table mappings but do not
        // own cross-context business FKs; this overlay preserves the Task9 relational model exactly without a
        // second physical schema configuration owner.
        modelBuilder.Entity<global::Accounts.Domain.Agent>()
            .HasOne<global::Merchants.Domain.Sale>().WithMany()
            .HasForeignKey(x => new { x.MerchantId, x.SaleId })
            .HasPrincipalKey(x => new { x.MerchantId, x.Id })
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<global::Payments.Domain.Psp.Connection>()
            .HasOne<global::Payments.Domain.Capabilities.PaymentProvider>().WithMany()
            .HasForeignKey(x => new { x.PaymentProviderId, x.Psp })
            .HasPrincipalKey(x => new { x.Id, x.AdapterCode })
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<global::Payments.Domain.Psp.Connection>()
            .HasOne<global::Merchants.Domain.Merchant>().WithMany()
            .HasForeignKey(x => x.MerchantId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<global::Payments.Domain.Psp.Connection>()
            .HasOne<global::BuildingBlocks.Infrastructure.Vault.VaultSecretVersion>().WithMany()
            .HasForeignKey(x => new { x.MerchantId, x.ActiveSecretVersionId })
            .HasPrincipalKey(x => new { x.MerchantId, x.Id })
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<global::Payments.Domain.Psp.Connection>()
            .HasOne<global::BuildingBlocks.Infrastructure.Vault.VaultSecretVersion>().WithMany()
            .HasForeignKey(x => new { x.MerchantId, x.PendingSecretVersionId })
            .HasPrincipalKey(x => new { x.MerchantId, x.Id })
            .OnDelete(DeleteBehavior.Restrict);
        if (_modules.ModuleNamespaces.Contains("Orders.Infrastructure", StringComparer.Ordinal))
        {
            modelBuilder.Entity<global::Payments.Domain.Transaction>()
                .HasOne<global::Orders.Domain.Order>().WithMany()
                .HasForeignKey(x => new { x.OrderId, x.MerchantId })
                .HasPrincipalKey(x => new { x.Id, x.MerchantId })
                .OnDelete(DeleteBehavior.Restrict);
        }
        if (_modules.ModuleNamespaces.Contains("Payments.Infrastructure", StringComparer.Ordinal))
        {
            modelBuilder.Entity<global::Payments.Domain.InboundWebhookEvent>()
                .HasOne<global::Payments.Domain.Psp.Connection>().WithMany()
                .HasForeignKey(x => new { x.MerchantId, x.PspConnectionId })
                .HasPrincipalKey(x => new { x.MerchantId, x.Id })
                .OnDelete(DeleteBehavior.Restrict);
        }
        modelBuilder.Entity<global::Iam.Domain.Permissions.Permission>()
            .HasOne<global::Iam.Domain.Permissions.PermissionGroup>().WithMany()
            .HasForeignKey(x => x.GroupKey).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<global::Iam.Domain.Roles.RolePermission>()
            .HasOne<global::Iam.Domain.Permissions.Permission>().WithMany()
            .HasForeignKey(x => x.PermissionKey).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<global::Admins.Domain.Roles.RoleAssignment>()
            .HasOne<global::Iam.Domain.Roles.Role>().WithMany()
            .HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<global::Merchants.Domain.Users.Roles.RoleAssignment>()
            .HasOne<global::Iam.Domain.Roles.Role>().WithMany()
            .HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<global::Iam.Domain.Roles.RolePermission>()
            .HasIndex(x => x.PermissionKey);
        modelBuilder.Entity<global::Admins.Domain.Roles.RoleAssignment>()
            .HasIndex(x => x.RoleId);
        modelBuilder.Entity<global::Merchants.Domain.Users.Roles.RoleAssignment>()
            .HasIndex(x => x.RoleId);
        modelBuilder.Entity<global::Payments.Domain.Psp.Connection>()
            .HasIndex(x => new { x.PaymentProviderId, x.Psp });
        modelBuilder.Entity<global::Admins.Domain.Users.User>().ToTable("Users", SchemaNames.Admin, table =>
            table.HasCheckConstraint("CK_Users_TenantId_MicrosoftProvider",
                "[TenantId] IS NULL OR [Provider] COLLATE Latin1_General_100_BIN2 = N'microsoft'"));

        // SQLite ignores SQL schemas and would otherwise collapse the admin and merchant identity tables
        // into duplicate physical names during architecture fixtures. Keep SQL Server names/schema intact;
        // these aliases are provider-specific test-model plumbing only.
        if (Database.ProviderName?.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) == true)
        {
            modelBuilder.Entity<global::Merchants.Domain.Users.User>().ToTable("MerchantUsers", SchemaNames.Merch);
            modelBuilder.Entity<global::Merchants.Domain.Users.Session>().ToTable("MerchantUserSessions", SchemaNames.Merch);
            modelBuilder.Entity<global::Merchants.Domain.Users.AuthAudit>().ToTable("MerchantAuthAudits", SchemaNames.Merch);
            modelBuilder.Entity<global::Merchants.Domain.Users.Roles.RoleAssignment>()
                .ToTable("MerchantRoleAssignments", SchemaNames.Merch);
            modelBuilder.Entity<global::Admins.Domain.Users.MerchantAccess>()
                .ToTable("AdminMerchantAccess", SchemaNames.Admin);
            modelBuilder.Entity<global::Access.Domain.MerchantAccess>()
                .ToTable("AccountMerchantAccess", SchemaNames.Access);
        }

        base.OnModelCreating(modelBuilder);
    }
}
