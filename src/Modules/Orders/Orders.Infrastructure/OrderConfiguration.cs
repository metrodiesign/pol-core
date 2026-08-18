using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Orders.Domain;

namespace Orders.Infrastructure;

/// <summary>
/// EF mapping for the Order aggregate. Discovered at model-build time from the Orders Infrastructure
/// assembly by <c>PolDbContext</c> (schema <c>shop</c>). <see cref="Order.Amount"/> is
/// mapped as an EF complex type (AmountAmount decimal(19,4), AmountCurrency char(3)) per the
/// Money mapping rule (PLAN decision #2).
/// </summary>
public sealed class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> builder)
    {
        builder.ToTable("Orders", SchemaNames.Shop, table =>
        {
            table.HasCheckConstraint("CK_Orders_InitiatingAudience",
                "([InitiatingAudience] IS NULL AND [InitiatingMerchantUserId] IS NULL) OR " +
                "([InitiatingAudience] = 1 AND [InitiatingMerchantUserId] IS NOT NULL AND [OriginatorId] IS NULL) OR " +
                "([InitiatingAudience] = 2 AND [InitiatingMerchantUserId] IS NULL AND [OriginatorId] IS NOT NULL)");
            table.HasCheckConstraint("CK_Orders_PaymentChannel_Canonical",
                "[PaymentChannel] IS NULL OR [PaymentChannel] IN ('card', 'promptpay', 'installment')");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();

        builder.Property(x => x.MerchantId).IsRequired();
        builder.Property(x => x.OriginatorId);
        builder.Property(x => x.InitiatingAudience).HasConversion<int>();
        builder.Property(x => x.InitiatingMerchantUserId);
        builder.Property(x => x.SaleCode).HasMaxLength(20).IsUnicode(false);
        builder.Property(x => x.PaymentSessionId);

        builder.ComplexProperty(x => x.Amount, p =>
        {
            p.Property(m => m.Amount).HasColumnName("AmountAmount").HasPrecision(19, 4);
            p.Property(m => m.Currency).HasColumnName("AmountCurrency").HasMaxLength(3).IsFixedLength().IsUnicode(false);
        });

        builder.Property(x => x.Status).IsRequired();
        builder.Property(x => x.CreatedAt).IsRequired();
        builder.Property(x => x.UpdatedAt).HasDefaultValueSql("SYSUTCDATETIME()").IsRequired();
        builder.Property(x => x.PaidAt);
        builder.Property(x => x.Version).IsConcurrencyToken().IsRequired();

        builder.Property(x => x.SummaryToken).HasMaxLength(64).IsRequired();
        builder.Property(x => x.SummaryTokenExpiresAt).IsRequired();
        builder.Property(x => x.NotificationRecipient).HasMaxLength(320);

        // purchase-flow-completion REQ-7.1/7.2. PaymentChannel is nullable: orders that predate this spec
        // have no channel and cannot be paid (the customer pay endpoint answers 409).
        builder.Property(x => x.OrderNo).HasMaxLength(13).IsUnicode(false).IsRequired();
        builder.Property(x => x.PaymentChannel).HasMaxLength(20).IsUnicode(false);
        builder.Property(x => x.CustomerName).HasMaxLength(200).IsRequired();
        builder.Property(x => x.CustomerPhone).HasMaxLength(20).IsUnicode(false).IsRequired();
        builder.Property(x => x.CustomerEmail).HasMaxLength(320);
        builder.Ignore(x => x.Customer);   // a view over the three columns above, not a column of its own

        // Domain events are an in-memory concern; never persisted.
        builder.Ignore(x => x.DomainEvents);

        // Composite alternate key + owned Items collection (insurance-pivot REQ-6) — mirrors
        // Carts.Infrastructure.CartConfiguration's Cart/Item relationship exactly (same-cluster,
        // aggregate-internal, so NOT stripped to scalar-only in the runtime config either).
        builder.HasAlternateKey(x => new { x.Id, x.MerchantId });

        builder.HasMany(x => x.Items)
            .WithOne()
            .HasForeignKey(i => new { i.OrderId, i.MerchantId })
            .HasPrincipalKey(x => new { x.Id, x.MerchantId })
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(x => x.Items).UsePropertyAccessMode(PropertyAccessMode.Field);

        // The consumer loads by payment session; index it (filtered — it is nullable until checkout binds one).
        builder.HasIndex(x => x.PaymentSessionId)
            .HasFilter("[PaymentSessionId] IS NOT NULL");

        // The public summary read looks the order up by its opaque token; unique index supports it.
        builder.HasIndex(x => x.SummaryToken).IsUnique();

        // RLS predicate uses MerchantId; index supports the merchant-scoped reads.
        builder.HasIndex(x => x.MerchantId);

        builder.HasIndex(x => new { x.InitiatingMerchantUserId, x.MerchantId });

        // The order number is the platform-wide human key (REQ-7.1) and the filter GET /orders takes
        // (REQ-7.4) — unique, not filtered: every order has one.
        builder.HasIndex(x => x.OrderNo).IsUnique();
    }
}
