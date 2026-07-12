using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Payments.Domain;

namespace Payments.Infrastructure.Persistence;

/// <summary>
/// Maps <see cref="Session"/> onto the txn schema. Per the EF mapping rule for
/// <c>Money</c>, <see cref="Session.Amount"/> is mapped as a complex type (AmountAmount
/// decimal(19,4), AmountCurrency char(3)). A unique filtered index on (Psp, PspExternalChargeId)
/// enforces that one external charge maps to at most one session (no double-attach across sessions).
/// </summary>
public sealed class SessionConfiguration : IEntityTypeConfiguration<Session>
{
    public void Configure(EntityTypeBuilder<Session> builder)
    {
        builder.ToTable("PaymentSessions", SchemaNames.Txn);
        builder.HasKey(x => x.Id);

        builder.Property(x => x.MerchantId).IsRequired();
        builder.Property(x => x.OrderId).IsRequired();

        builder.ComplexProperty(x => x.Amount, p =>
        {
            p.Property(m => m.Amount).HasColumnName("AmountAmount").HasPrecision(19, 4);
            p.Property(m => m.Currency).HasColumnName("AmountCurrency").HasMaxLength(3).IsFixedLength().IsUnicode(false);
        });

        builder.Property(x => x.Method).HasMaxLength(32).IsRequired();
        builder.Property(x => x.Psp).IsRequired();
        builder.Property(x => x.Status).IsRequired();
        builder.Property(x => x.PspExternalChargeId).HasMaxLength(256);
        builder.Property(x => x.RedirectUrl).HasMaxLength(2048);
        builder.Property(x => x.CreatedAt).IsRequired();
        builder.Property(x => x.UpdatedAt).IsRequired();

        // Optimistic concurrency: serialises the redirect claim (PLAN #11).
        builder.Property(x => x.RowVersion).IsRowVersion();

        // Webhook lookup + no double-attach: one external charge maps to at most one session.
        builder.HasIndex(x => new { x.Psp, x.PspExternalChargeId })
            .IsUnique()
            .HasFilter("[PspExternalChargeId] IS NOT NULL");

        builder.HasIndex(x => x.OrderId);
    }
}
