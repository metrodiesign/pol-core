using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Orders.Domain;

namespace Orders.Infrastructure;

/// <summary>
/// EF mapping for the Order aggregate. Discovered at model-build time from the Orders producer
/// assembly by <c>ProducerDbContext</c> (schema <c>producer</c>). The money seam is stored as two
/// scalar columns and the computed <see cref="Order.Amount"/> is ignored — the validating
/// <c>Money</c> struct ctor is not EF-friendly as an owned/complex type (PLAN decision #2).
/// </summary>
public sealed class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> builder)
    {
        builder.ToTable("Orders");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();

        builder.Property(x => x.TenantId).IsRequired();
        builder.Property(x => x.PaymentSessionId);
        builder.Property(x => x.CheckoutSessionId);

        builder.Ignore(x => x.Amount);
        builder.Property(x => x.AmountMinorUnits).IsRequired();
        builder.Property(x => x.AmountCurrency).HasMaxLength(3).IsRequired();

        builder.Property(x => x.Status).IsRequired();
        builder.Property(x => x.CreatedAtUtc).IsRequired();
        builder.Property(x => x.PaidAtUtc);

        builder.Property(x => x.SummaryToken).HasMaxLength(64).IsRequired();
        builder.Property(x => x.SummaryTokenExpiresAtUtc).IsRequired();

        // Domain events are an in-memory concern; never persisted.
        builder.Ignore(x => x.DomainEvents);

        // The consumer loads by payment session; index it (filtered — it is nullable until checkout binds one).
        builder.HasIndex(x => x.PaymentSessionId)
            .HasFilter("[PaymentSessionId] IS NOT NULL");

        // The public summary read looks the order up by its opaque token; unique index supports it.
        builder.HasIndex(x => x.SummaryToken).IsUnique();

        // One order per checkout session — the idempotency key against a replayed CheckoutConfirmed event
        // (filtered, since it is null for orders not created via checkout).
        builder.HasIndex(x => x.CheckoutSessionId)
            .IsUnique()
            .HasFilter("[CheckoutSessionId] IS NOT NULL");

        // RLS predicate uses TenantId; index supports the tenant-scoped reads.
        builder.HasIndex(x => x.TenantId);
    }
}
