using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Orders.Domain;

namespace Persistence.MerchantRuntime.Orders;

// Runtime (scalar-only) mapping — mirrors Orders.Infrastructure.OrderConfiguration exactly for
// column/index shape. PaymentSessionId stays a bare scalar; the relationship is intentionally not a FK.

internal sealed class OrderConfiguration(MerchantRuntimeDbContext context) : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> builder)
    {
        builder.ToTable("Orders", SchemaNames.Shop);
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();

        builder.Property(x => x.MerchantId).IsRequired();
        builder.Property(x => x.SaleCode).HasMaxLength(20).IsUnicode(false);
        builder.Property(x => x.PaymentSessionId);

        TenantKeyDescriptor.Require(builder.Metadata, nameof(Order.MerchantId));
        builder.HasQueryFilter(x => x.MerchantId == context.CurrentMerchant);

        builder.ComplexProperty(x => x.Amount, p =>
        {
            p.Property(m => m.Amount).HasColumnName("AmountAmount").HasPrecision(19, 4);
            p.Property(m => m.Currency).HasColumnName("AmountCurrency").HasMaxLength(3).IsFixedLength().IsUnicode(false);
        });

        builder.Property(x => x.Status).IsRequired();
        builder.Property(x => x.CreatedAt).IsRequired();
        builder.Property(x => x.PaidAt);

        builder.Property(x => x.SummaryToken).HasMaxLength(64).IsRequired();
        builder.Property(x => x.SummaryTokenExpiresAt).IsRequired();
        builder.Property(x => x.NotificationRecipient).HasMaxLength(320);

        // Mirrors Orders.Infrastructure.OrderConfiguration (purchase-flow-completion REQ-7.1/7.2).
        builder.Property(x => x.OrderNo).HasMaxLength(13).IsUnicode(false).IsRequired();
        builder.Property(x => x.PaymentChannel).HasMaxLength(20).IsUnicode(false);
        builder.Property(x => x.CustomerName).HasMaxLength(200).IsRequired();
        builder.Property(x => x.CustomerPhone).HasMaxLength(20).IsUnicode(false).IsRequired();
        builder.Property(x => x.CustomerEmail).HasMaxLength(320);
        builder.Ignore(x => x.Customer);

        builder.Ignore(x => x.DomainEvents);

        builder.HasIndex(x => x.PaymentSessionId)
            .HasFilter("[PaymentSessionId] IS NOT NULL");

        builder.HasIndex(x => x.SummaryToken).IsUnique();

        builder.HasIndex(x => x.MerchantId);

        builder.HasIndex(x => x.OrderNo).IsUnique();

        // Composite alternate key + owned Items collection (insurance-pivot REQ-6), mirrors
        // Orders.Infrastructure.OrderConfiguration and Persistence.MerchantRuntime.Carts.CartConfiguration's
        // Cart/Item relationship — same-cluster, aggregate-internal, so KEPT here (not scalar-only).
        builder.HasAlternateKey(x => new { x.Id, x.MerchantId });

        builder.HasMany(x => x.Items)
            .WithOne()
            .HasForeignKey(i => new { i.OrderId, i.MerchantId })
            .HasPrincipalKey(x => new { x.Id, x.MerchantId })
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(x => x.Items).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
