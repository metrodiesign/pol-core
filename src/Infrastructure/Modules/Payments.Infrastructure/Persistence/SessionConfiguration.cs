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
        // The routing snapshot CHECK is declared on the table: a version-1 row must carry every pinned field,
        // and version 0 exists only for legacy rows (REQ-2.9, design 567). Kept identical in the runtime
        // mirror; this owner copy is the one the migration emits.
        builder.ToTable("PaymentSessions", SchemaNames.Txn, t => t.HasCheckConstraint(
            "CK_PaymentSessions_RoutingSnapshotV1",
            "[RoutingSnapshotVersion] <> 1 OR ([PspConnectionId] IS NOT NULL AND [SecretVersionId] IS NOT NULL AND [PspEnvironment] IS NOT NULL)"));
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
        builder.Property(x => x.PspConnectionId);
        builder.Property(x => x.SecretVersionId);
        builder.Property(x => x.PspEnvironment);
        builder.Property(x => x.RoutingSnapshotVersion).IsRequired();
        builder.Property(x => x.Status).IsRequired();
        builder.Property(x => x.PspExternalChargeId).HasMaxLength(256);
        builder.Property(x => x.RedirectUrl).HasMaxLength(2048);
        builder.Property(x => x.CreatedAt).IsRequired();
        builder.Property(x => x.UpdatedAt).IsRequired();
        builder.Property(x => x.Version).IsConcurrencyToken().IsRequired();

        // Optimistic concurrency: serialises the redirect claim (PLAN #11).
        builder.Property(x => x.RowVersion).IsRowVersion();

        // Webhook lookup + no double-attach: one external charge maps to at most one session.
        builder.HasIndex(x => new { x.Psp, x.PspExternalChargeId })
            .IsUnique()
            .HasFilter("[PspExternalChargeId] IS NOT NULL");

        builder.HasIndex(x => x.OrderId);

        // One chargeable session per order, enforced at the DB floor (captive-payment-alignment REQ-2.4):
        // the handler's open-session pre-check loses a race, this does not. Status 1/2 = Created/Redirected;
        // Failed/Expired/Paid fall outside the filter so a failed attempt can still be retried (REQ-7.4).
        // NAMED overload deliberately — HasIndex(x => x.OrderId) a second time would MUTATE the plain
        // lookup index above (EF keys unnamed indexes by property set), not add a second one.
        builder.HasIndex(x => x.OrderId, "IX_PaymentSessions_OrderId_Open")
            .IsUnique()
            .HasFilter("[Status] IN (1, 2)");
    }
}
