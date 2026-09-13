using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Idempotency;
using BuildingBlocks.Infrastructure.Outbox;
using BuildingBlocks.Infrastructure.Persistence;
using BuildingBlocks.Infrastructure.Vault;
using Accounts.Application;
using Microsoft.EntityFrameworkCore;
using Payments.Domain;
using Payments.Domain.Capabilities;
using Payments.Domain.Psp;
using Payments.Domain.Routing;
using CartAggregate = Carts.Domain.Cart;
using CartItem = Carts.Domain.Items.Item;
using PaymentSession = Payments.Domain.Session;
using InboundWebhookEvent = Payments.Domain.InboundWebhookEvent;
using OrderAggregate = Orders.Domain.Order;
using OrderItem = Orders.Domain.Items.Item;
using OrderItemRevealAudit = Orders.Domain.Items.RevealAudit;
using Orders.Application;
using PaymentLink = Checkouts.Domain.PaymentLink;
using PaymentLinkReplay = Checkouts.Domain.PaymentLinkReplay;
using AdminOperationRecord = BuildingBlocks.Infrastructure.Idempotency.AdminOperationRecord;

namespace Persistence.MerchantRuntime;

/// <summary>
/// Runtime context for the Commerce co-commit cluster — shop.*/txn.* (rls-to-query-filter design.md
/// "Context topology"; handlers 16, 21 of the transaction inventory are single-context here — this IS the
/// isolation floor: every entity here
/// carries a uniform <c>tenantKey==CurrentMerchant</c> global query filter, REQ-1.1). internal sealed: only
/// this assembly's host-registration extension may construct it. No migrations declared here — PolDbContext
/// stays the single migration owner.
/// </summary>
internal sealed class CommerceDbContext : GuardedRuntimeDbContext, IMerchantFilterContext
{
    private readonly IActorContext _actor;
    private readonly IOrderIdentityAccessScope? _identityScope;

    public CommerceDbContext(
        DbContextOptions options, IActorContext actor, IWriteAuthorizer authorizer,
        ISecurityTelemetry telemetry, IOrderIdentityAccessScope? identityScope = null)
        : base(options, authorizer, telemetry)
    {
        _actor = actor;
        _identityScope = identityScope;
    }

    /// <summary>The read floor's instance member (REQ-1.5): captured PER QUERY from THIS context instance
    /// inside each entity's <c>HasQueryFilter</c> lambda — never baked into the cached model — so a worker's
    /// late-bound actor and a per-request actor both re-evaluate correctly. Unbound (REQ-3.1) resolves to
    /// <see cref="Guid.Empty"/>, which the DB CHECK constraint (task 3/8) guarantees no real row ever carries,
    /// so an unbound actor sees zero rows everywhere in this context.</summary>
    public Guid CurrentMerchant => _actor.HasActor ? _actor.MerchantId : Guid.Empty;

    /// <summary>The bound merchant user (Tier 1 agent/broker), when the request carries one. Null for an
    /// admin-bound ambient scope, a webhook/worker binding, or an unbound actor — those keep the merchant-wide
    /// read. Read per query like <see cref="CurrentMerchant"/>, never snapshotted into the cached model.</summary>
    internal Guid? CurrentMerchantUser => _identityScope?.IsBound == true
        ? null
        : _actor.HasActor ? _actor.UserId : null;

    internal AuthorizationSnapshot? CurrentIdentityAuthorization =>
        _identityScope?.IsBound == true ? _identityScope.Snapshot : null;

    public DbSet<CartAggregate> Carts => Set<CartAggregate>();
    public DbSet<CartItem> CartItems => Set<CartItem>();
    public DbSet<OrderAggregate> Orders => Set<OrderAggregate>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();
    public DbSet<OrderItemRevealAudit> OrderItemRevealAudits => Set<OrderItemRevealAudit>();
    public DbSet<PaymentLink> PaymentLinks => Set<PaymentLink>();
    public DbSet<PaymentLinkReplay> PaymentLinkReplays => Set<PaymentLinkReplay>();

    public DbSet<PaymentSession> PaymentSessions => Set<PaymentSession>();
    public DbSet<Transaction> Transactions => Set<Transaction>();
    public DbSet<TransactionEvent> TransactionEvents => Set<TransactionEvent>();
    public DbSet<InboundWebhookEvent> InboundWebhookEvents => Set<InboundWebhookEvent>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();
    public DbSet<AdminOperationRecord> AdminOperationRecords => Set<AdminOperationRecord>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    public DbSet<global::Notifications.Domain.Notification> Notifications => Set<global::Notifications.Domain.Notification>();
    public DbSet<global::Notifications.Domain.TemplateVersion> TemplateVersions => Set<global::Notifications.Domain.TemplateVersion>();
    public DbSet<global::Notifications.Domain.Delivery> Deliveries => Set<global::Notifications.Domain.Delivery>();
    public DbSet<global::Notifications.Domain.DeliveryAttempt> DeliveryAttempts => Set<global::Notifications.Domain.DeliveryAttempt>();
    public DbSet<global::Notifications.Domain.NotificationInboxMessage> NotificationInboxMessages =>
        Set<global::Notifications.Domain.NotificationInboxMessage>();
    public DbSet<global::Notifications.Domain.NotificationReviewNote> NotificationReviewNotes =>
        Set<global::Notifications.Domain.NotificationReviewNote>();




    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Physical schema/column/index ownership is canonical in the module configurations. Commerce adds
        // only its actor-dependent query filters below.
        modelBuilder.ApplyConfiguration(new global::Carts.Infrastructure.CartConfiguration());
        modelBuilder.ApplyConfiguration(new global::Carts.Infrastructure.Items.ItemConfiguration());
        modelBuilder.ApplyConfiguration(new global::Orders.Infrastructure.OrderConfiguration());
        modelBuilder.ApplyConfiguration(new global::Orders.Infrastructure.Items.ItemConfiguration());
        // Runtime order patches replace aggregate lines with client-minted IDs. Keep this EF tracking policy
        // local to Commerce; the migration owner's historical snapshot retains its Task9 generation metadata.
        modelBuilder.Entity<OrderItem>().Property(x => x.Id).ValueGeneratedNever();
        modelBuilder.ApplyConfiguration(new global::Orders.Infrastructure.Items.RevealAuditConfiguration());
        modelBuilder.ApplyConfiguration(new global::Orders.Infrastructure.PaymentLinkConfiguration());
        modelBuilder.ApplyConfiguration(new global::Orders.Infrastructure.PaymentLinkReplayConfiguration());

        modelBuilder.ApplyConfiguration(new global::Payments.Infrastructure.Persistence.SessionConfiguration());
        modelBuilder.ApplyConfiguration(new global::Payments.Infrastructure.Persistence.TransactionConfiguration());
        modelBuilder.ApplyConfiguration(new global::Payments.Infrastructure.Persistence.TransactionEventConfiguration());
        modelBuilder.ApplyConfiguration(new global::Payments.Infrastructure.Persistence.InboundWebhookEventConfiguration());
        modelBuilder.ApplyConfiguration(new BuildingBlocks.Infrastructure.Idempotency.IdempotencyRecordConfiguration());
        modelBuilder.ApplyConfiguration(new BuildingBlocks.Infrastructure.Persistence.AdminOperationRecordConfiguration());
        modelBuilder.ApplyConfiguration(new BuildingBlocks.Infrastructure.Outbox.OutboxMessageConfiguration());
        modelBuilder.Entity<Transaction>()
            .HasOne<OrderAggregate>().WithMany()
            .HasForeignKey(x => new { x.OrderId, x.MerchantId })
            .HasPrincipalKey(x => new { x.Id, x.MerchantId })
            .OnDelete(DeleteBehavior.Restrict);


        modelBuilder.ApplyConfiguration(new global::Notifications.Infrastructure.NotificationConfiguration());
        modelBuilder.ApplyConfiguration(new global::Notifications.Infrastructure.TemplateVersionConfiguration());
        modelBuilder.ApplyConfiguration(new global::Notifications.Infrastructure.DeliveryConfiguration());
        modelBuilder.ApplyConfiguration(new global::Notifications.Infrastructure.DeliveryAttemptConfiguration());
        modelBuilder.ApplyConfiguration(new global::Notifications.Infrastructure.NotificationInboxMessageConfiguration());
        modelBuilder.ApplyConfiguration(new global::Notifications.Infrastructure.NotificationReviewNoteConfiguration());
        modelBuilder.Entity<global::Notifications.Domain.Notification>()
            .HasQueryFilter(x => x.MerchantId == CurrentMerchant);
        modelBuilder.Entity<global::Notifications.Domain.Delivery>()
            .HasQueryFilter(x => x.MerchantId == CurrentMerchant);
        modelBuilder.Entity<global::Notifications.Domain.DeliveryAttempt>()
            .HasQueryFilter(x => x.MerchantId == CurrentMerchant);
        modelBuilder.Entity<global::Notifications.Domain.NotificationInboxMessage>()
            .HasQueryFilter(x => x.MerchantId == CurrentMerchant);
        modelBuilder.Entity<global::Notifications.Domain.NotificationReviewNote>()
            .HasQueryFilter(x => x.MerchantId == CurrentMerchant);

        modelBuilder.Entity<CartAggregate>().HasQueryFilter(x => x.MerchantId == CurrentMerchant);
        modelBuilder.Entity<CartItem>().HasQueryFilter(x => x.MerchantId == CurrentMerchant);
        modelBuilder.Entity<OrderAggregate>().HasQueryFilter(x => x.MerchantId == CurrentMerchant
            && (CurrentMerchantUser == null
                || x.InitiatingMerchantUserId == CurrentMerchantUser
                || x.CreatedByAccountId == CurrentMerchantUser));
        modelBuilder.Entity<OrderItem>().HasQueryFilter(x => x.MerchantId == CurrentMerchant);
        modelBuilder.Entity<OrderItemRevealAudit>().HasQueryFilter(x => x.MerchantId == CurrentMerchant);
        modelBuilder.Entity<PaymentLink>().HasQueryFilter(x => x.MerchantId == CurrentMerchant);
        modelBuilder.Entity<PaymentLinkReplay>().HasQueryFilter(x => x.MerchantId == CurrentMerchant);
        modelBuilder.Entity<PaymentSession>().HasQueryFilter(x => x.MerchantId == CurrentMerchant
            && (CurrentMerchantUser == null
                || Orders.Any(o => o.Id == x.OrderId && o.InitiatingMerchantUserId == CurrentMerchantUser)));
        modelBuilder.Entity<Transaction>().HasQueryFilter(x => x.MerchantId == CurrentMerchant);
        modelBuilder.Entity<TransactionEvent>().HasQueryFilter(x => x.MerchantId == CurrentMerchant);
        modelBuilder.Entity<InboundWebhookEvent>().HasQueryFilter(x => x.MerchantId == CurrentMerchant);
        modelBuilder.Entity<IdempotencyRecord>().HasQueryFilter(x => x.MerchantId == CurrentMerchant);
        modelBuilder.Entity<AdminOperationRecord>().HasQueryFilter(x => x.MerchantId == CurrentMerchant);
        modelBuilder.Entity<OutboxMessage>().HasQueryFilter(x => x.MerchantId == CurrentMerchant);

        // Context-specific write-floor metadata is separate from the canonical physical mappings above.
        RequireTenant<CartAggregate>(nameof(CartAggregate.MerchantId));
        RequireTenant<CartItem>(nameof(CartItem.MerchantId));
        RequireTenant<OrderAggregate>(nameof(OrderAggregate.MerchantId));
        RequireTenant<OrderItem>(nameof(OrderItem.MerchantId));
        RequireTenant<OrderItemRevealAudit>(nameof(OrderItemRevealAudit.MerchantId));
        RequireTenant<PaymentLink>(nameof(PaymentLink.MerchantId));
        RequireTenant<PaymentLinkReplay>(nameof(PaymentLinkReplay.MerchantId));
        RequireTenant<PaymentSession>(nameof(PaymentSession.MerchantId));
        RequireTenant<Transaction>(nameof(Transaction.MerchantId));
        RequireTenant<TransactionEvent>(nameof(TransactionEvent.MerchantId));
        RequireTenant<InboundWebhookEvent>(nameof(InboundWebhookEvent.MerchantId));
        RequireTenant<IdempotencyRecord>(nameof(IdempotencyRecord.MerchantId));
        RequireTenant<AdminOperationRecord>(nameof(AdminOperationRecord.MerchantId));
        RequireTenant<OutboxMessage>(nameof(OutboxMessage.MerchantId));
        AppendOnlyDescriptor.Mark(modelBuilder.Entity<OrderItemRevealAudit>().Metadata);

        base.OnModelCreating(modelBuilder);

        void RequireTenant<TEntity>(string propertyName) where TEntity : class =>
            TenantKeyDescriptor.Require(modelBuilder.Entity<TEntity>().Metadata, propertyName);
    }
}
