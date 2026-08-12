using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Idempotency;
using BuildingBlocks.Infrastructure.Outbox;
using BuildingBlocks.Infrastructure.Persistence;
using BuildingBlocks.Infrastructure.Vault;
using Merchants.Domain;
using Microsoft.EntityFrameworkCore;
using Payments.Domain.Psp;
using Payments.Domain.Routing;
using Persistence.MerchantRuntime.Carts;
using Persistence.MerchantRuntime.Carts.Items;
using Persistence.MerchantRuntime.Merchants;
using Persistence.MerchantRuntime.Orders;
using Persistence.MerchantRuntime.Payments.Psp;
using CartAggregate = Carts.Domain.Cart;
using CartItem = Carts.Domain.Items.Item;
using PaymentSession = Payments.Domain.Session;
using InboundWebhookEvent = Payments.Domain.InboundWebhookEvent;
using OrderAggregate = Orders.Domain.Order;
using OrderItem = Orders.Domain.Items.Item;
using OrderItemRevealAudit = Orders.Domain.Items.RevealAudit;
using PaymentSessionConfiguration = Persistence.MerchantRuntime.Payments.SessionConfiguration;
using OrderItemConfiguration = Persistence.MerchantRuntime.Orders.Items.ItemConfiguration;
using OrderItemRevealAuditConfiguration = Persistence.MerchantRuntime.Orders.Items.RevealAuditConfiguration;
// Fully-qualified (not `using`-imported) to avoid colliding with the same-named entity-OWNING namespaces
// above (BuildingBlocks.Infrastructure.Idempotency/Outbox/Vault) — this context uses its OWN filtered
// configs for these four BuildingBlocks-owned entities, not the migration-owner's unfiltered ones.
using IdempotencyRecordConfiguration = Persistence.MerchantRuntime.Idempotency.IdempotencyRecordConfiguration;
using AdminOperationRecordConfiguration = Persistence.MerchantRuntime.Idempotency.AdminOperationRecordConfiguration;
using OutboxMessageConfiguration = Persistence.MerchantRuntime.Outbox.OutboxMessageConfiguration;
using VaultSecretBlobConfiguration = Persistence.MerchantRuntime.Vault.VaultSecretBlobConfiguration;
using VaultSecretVersionConfiguration = Persistence.MerchantRuntime.Vault.VaultSecretVersionConfiguration;
using VaultRevealAuditConfiguration = Persistence.MerchantRuntime.Vault.VaultRevealAuditConfiguration;

namespace Persistence.MerchantRuntime;

/// <summary>
/// Runtime context for the MerchantRuntime co-commit cluster — shop.*/txn.* + merch.Merchants/VaultSecrets/
/// VaultRevealAudits/ProvisioningAudits (rls-to-query-filter design.md "Context topology"; handlers 16, 21
/// of the transaction inventory are single-context here — this IS the isolation floor: every entity here
/// carries a uniform <c>tenantKey==CurrentMerchant</c> global query filter, REQ-1.1). internal sealed: only
/// this assembly's host-registration extension may construct it. No migrations declared here — PolDbContext
/// stays the single migration owner.
/// </summary>
internal sealed class MerchantRuntimeDbContext : GuardedRuntimeDbContext
{
    private readonly IActorContext _actor;

    public MerchantRuntimeDbContext(
        DbContextOptions<MerchantRuntimeDbContext> options, IActorContext actor, IWriteAuthorizer authorizer,
        ISecurityTelemetry telemetry)
        : base(options, authorizer, telemetry)
        => _actor = actor;

    /// <summary>The read floor's instance member (REQ-1.5): captured PER QUERY from THIS context instance
    /// inside each entity's <c>HasQueryFilter</c> lambda — never baked into the cached model — so a worker's
    /// late-bound actor and a per-request actor both re-evaluate correctly. Unbound (REQ-3.1) resolves to
    /// <see cref="Guid.Empty"/>, which the DB CHECK constraint (task 3/8) guarantees no real row ever carries,
    /// so an unbound actor sees zero rows everywhere in this context.</summary>
    internal Guid CurrentMerchant => _actor.HasActor ? _actor.MerchantId : Guid.Empty;

    public DbSet<CartAggregate> Carts => Set<CartAggregate>();
    public DbSet<CartItem> CartItems => Set<CartItem>();
    public DbSet<OrderAggregate> Orders => Set<OrderAggregate>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();
    public DbSet<OrderItemRevealAudit> OrderItemRevealAudits => Set<OrderItemRevealAudit>();

    public DbSet<PaymentSession> PaymentSessions => Set<PaymentSession>();
    public DbSet<InboundWebhookEvent> InboundWebhookEvents => Set<InboundWebhookEvent>();
    public DbSet<Connection> PspConnections => Set<Connection>();
    public DbSet<RoutingRuleset> RoutingRulesets => Set<RoutingRuleset>();
    public DbSet<RoutingRule> RoutingRules => Set<RoutingRule>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();
    public DbSet<AdminOperationRecord> AdminOperationRecords => Set<AdminOperationRecord>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    public DbSet<Merchant> Merchants => Set<Merchant>();
    public DbSet<Originator> Originators => Set<Originator>();
    public DbSet<VaultSecretBlob> VaultSecrets => Set<VaultSecretBlob>();
    public DbSet<VaultSecretVersion> VaultSecretVersions => Set<VaultSecretVersion>();
    public DbSet<VaultRevealAudit> VaultRevealAudits => Set<VaultRevealAudit>();
    public DbSet<ProvisioningAudit> ProvisioningAudits => Set<ProvisioningAudit>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new CartConfiguration(this));
        modelBuilder.ApplyConfiguration(new ItemConfiguration(this));
        modelBuilder.ApplyConfiguration(new OrderConfiguration(this));
        modelBuilder.ApplyConfiguration(new OrderItemConfiguration(this));
        modelBuilder.ApplyConfiguration(new OrderItemRevealAuditConfiguration(this));

        modelBuilder.ApplyConfiguration(new PaymentSessionConfiguration(this));
        modelBuilder.ApplyConfiguration(new Payments.InboundWebhookEventConfiguration(this));
        modelBuilder.ApplyConfiguration(new ConnectionConfiguration(this));
        modelBuilder.ApplyConfiguration(new Payments.Routing.RoutingRulesetConfiguration(this));
        modelBuilder.ApplyConfiguration(new Payments.Routing.RoutingRuleConfiguration(this));
        modelBuilder.ApplyConfiguration(new IdempotencyRecordConfiguration(this));
        modelBuilder.ApplyConfiguration(new AdminOperationRecordConfiguration(this));
        modelBuilder.ApplyConfiguration(new OutboxMessageConfiguration(this));

        modelBuilder.ApplyConfiguration(new MerchantConfiguration(this));
        modelBuilder.ApplyConfiguration(new OriginatorConfiguration(this));
        modelBuilder.ApplyConfiguration(new VaultSecretBlobConfiguration(this));
        modelBuilder.ApplyConfiguration(new VaultSecretVersionConfiguration(this));
        modelBuilder.ApplyConfiguration(new VaultRevealAuditConfiguration(this));
        modelBuilder.ApplyConfiguration(new ProvisioningAuditConfiguration(this));

        base.OnModelCreating(modelBuilder);
    }
}
