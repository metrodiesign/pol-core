using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Payments.Domain;

namespace Payments.Infrastructure.Persistence;

/// <summary>
/// Maps <see cref="PaymentSession"/> onto the producer schema. Per the EF mapping rule for
/// <c>Money</c>, the computed <see cref="PaymentSession.Amount"/> is ignored and the two backing
/// scalar columns are mapped instead. A unique filtered index on (Psp, PspExternalChargeId) enforces
/// that one external charge maps to at most one session (no double-attach across sessions).
/// </summary>
public sealed class PaymentSessionConfiguration : IEntityTypeConfiguration<PaymentSession>
{
    public void Configure(EntityTypeBuilder<PaymentSession> builder)
    {
        builder.ToTable("PaymentSessions");
        builder.HasKey(x => x.Id);

        builder.Ignore(x => x.Amount);

        builder.Property(x => x.TenantId).IsRequired();
        builder.Property(x => x.OrderId).IsRequired();
        builder.Property(x => x.AmountMinorUnits).IsRequired();
        builder.Property(x => x.AmountCurrency).HasMaxLength(3).IsRequired();
        builder.Property(x => x.Method).HasMaxLength(32).IsRequired();
        builder.Property(x => x.Psp).IsRequired();
        builder.Property(x => x.Status).IsRequired();
        builder.Property(x => x.PspExternalChargeId).HasMaxLength(256);
        builder.Property(x => x.RedirectUrl).HasMaxLength(2048);
        builder.Property(x => x.CreatedAtUtc).IsRequired();
        builder.Property(x => x.UpdatedAtUtc).IsRequired();

        // Webhook lookup + no double-attach: one external charge maps to at most one session.
        builder.HasIndex(x => new { x.Psp, x.PspExternalChargeId })
            .IsUnique()
            .HasFilter("[PspExternalChargeId] IS NOT NULL");

        builder.HasIndex(x => x.OrderId);
    }
}
