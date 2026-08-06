using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Idempotency;
using BuildingBlocks.Infrastructure.Outbox;
using BuildingBlocks.Infrastructure.Persistence;
using BuildingBlocks.Infrastructure.Vault;
using Merchants.Domain;
using Microsoft.EntityFrameworkCore;
using Payments.Domain.Psp;
using Persistence.MerchantRuntime.Carts;
using Persistence.MerchantRuntime.Carts.Items;
using Persistence.MerchantRuntime.Merchants;
using Persistence.MerchantRuntime.Orders;
using Persistence.MerchantRuntime.Payments.Psp;
using CartAggregate = Carts.Domain.Cart;
using CartItem = Carts.Domain.Items.Item;
using CheckoutSession = Checkouts.Domain.Session;
using PaymentSession = Payments.Domain.Session;
using OrderAggregate = Orders.Domain.Order;
using OrderItem = Orders.Domain.Items.Item;
using OrderItemRevealAudit = Orders.Domain.Items.RevealAudit;
using OrderItemPolicy = Orders.Domain.Items.ItemPolicy;
using OrderItemPolicyAudit = Orders.Domain.Items.ItemPolicyAudit;
using CheckoutSessionItem = Checkouts.Domain.Items.Item;
using CheckoutSessionConfiguration = Persistence.MerchantRuntime.Checkouts.SessionConfiguration;
using PaymentSessionConfiguration = Persistence.MerchantRuntime.Payments.SessionConfiguration;
using OrderItemConfiguration = Persistence.MerchantRuntime.Orders.Items.ItemConfiguration;
using OrderItemRevealAuditConfiguration = Persistence.MerchantRuntime.Orders.Items.RevealAuditConfiguration;
using OrderItemPolicyConfiguration = Persistence.MerchantRuntime.Orders.Items.ItemPolicyConfiguration;
using OrderItemPolicyAuditConfiguration = Persistence.MerchantRuntime.Orders.Items.ItemPolicyAuditConfiguration;
using CheckoutSessionItemConfiguration = Persistence.MerchantRuntime.Checkouts.Items.ItemConfiguration;
// Fully-qualified (not `using`-imported) to avoid colliding with the same-named entity-OWNING namespaces
// above (BuildingBlocks.Infrastructure.Idempotency/Outbox/Vault) — this context uses its OWN filtered
// configs for these four BuildingBlocks-owned entities, not the migration-owner's unfiltered ones.
using IdempotencyRecordConfiguration = Persistence.MerchantRuntime.Idempotency.IdempotencyRecordConfiguration;
using OutboxMessageConfiguration = Persistence.MerchantRuntime.Outbox.OutboxMessageConfiguration;
using VaultSecretBlobConfiguration = Persistence.MerchantRuntime.Vault.VaultSecretBlobConfiguration;
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
    public DbSet<CheckoutSession> CheckoutSessions => Set<CheckoutSession>();
    public DbSet<CheckoutSessionItem> CheckoutSessionItems => Set<CheckoutSessionItem>();
    public DbSet<OrderAggregate> Orders => Set<OrderAggregate>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();
    public DbSet<OrderItemRevealAudit> OrderItemRevealAudits => Set<OrderItemRevealAudit>();
    public DbSet<OrderItemPolicy> OrderItemPolicies => Set<OrderItemPolicy>();
    public DbSet<OrderItemPolicyAudit> OrderItemPolicyAudits => Set<OrderItemPolicyAudit>();

    public DbSet<PaymentSession> PaymentSessions => Set<PaymentSession>();
    public DbSet<Connection> PspConnections => Set<Connection>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    public DbSet<Merchant> Merchants => Set<Merchant>();
    public DbSet<VaultSecretBlob> VaultSecrets => Set<VaultSecretBlob>();
    public DbSet<VaultRevealAudit> VaultRevealAudits => Set<VaultRevealAudit>();
    public DbSet<ProvisioningAudit> ProvisioningAudits => Set<ProvisioningAudit>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new CartConfiguration(this));
        modelBuilder.ApplyConfiguration(new ItemConfiguration(this));
        modelBuilder.ApplyConfiguration(new CheckoutSessionConfiguration(this));
        modelBuilder.ApplyConfiguration(new CheckoutSessionItemConfiguration(this));
        modelBuilder.ApplyConfiguration(new OrderConfiguration(this));
        modelBuilder.ApplyConfiguration(new OrderItemConfiguration(this));
        modelBuilder.ApplyConfiguration(new OrderItemRevealAuditConfiguration(this));
        modelBuilder.ApplyConfiguration(new OrderItemPolicyConfiguration(this));
        modelBuilder.ApplyConfiguration(new OrderItemPolicyAuditConfiguration(this));

        modelBuilder.ApplyConfiguration(new PaymentSessionConfiguration(this));
        modelBuilder.ApplyConfiguration(new ConnectionConfiguration(this));
        modelBuilder.ApplyConfiguration(new IdempotencyRecordConfiguration(this));
        modelBuilder.ApplyConfiguration(new OutboxMessageConfiguration(this));

        modelBuilder.ApplyConfiguration(new MerchantConfiguration(this));
        modelBuilder.ApplyConfiguration(new VaultSecretBlobConfiguration(this));
        modelBuilder.ApplyConfiguration(new VaultRevealAuditConfiguration(this));
        modelBuilder.ApplyConfiguration(new ProvisioningAuditConfiguration(this));

        base.OnModelCreating(modelBuilder);
    }
}
