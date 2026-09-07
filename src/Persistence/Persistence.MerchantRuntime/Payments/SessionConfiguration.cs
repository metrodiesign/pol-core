using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Payments.Domain;

namespace Persistence.MerchantRuntime.Payments;

// Runtime (scalar-only) mapping — mirrors Payments.Infrastructure.Persistence.SessionConfiguration
// exactly for column/index shape. OrderId stays a bare scalar (no HasOne — none existed in the source
// config).

internal sealed class SessionConfiguration(MerchantRuntimeDbContext context) : IEntityTypeConfiguration<Session>
{
    public void Configure(EntityTypeBuilder<Session> builder)
    {
        // Routing snapshot CHECK — MUST mirror Payments.Infrastructure.Persistence.SessionConfiguration
        // exactly: only the owner copy reaches the DDL, only this copy validates runtime saves, and a
        // divergence is invisible to the unit suite (REQ-2.9, design 567).
        builder.ToTable("PaymentSessions", SchemaNames.Txn, t => t.HasCheckConstraint(
            "CK_PaymentSessions_RoutingSnapshotV1",
            "[RoutingSnapshotVersion] <> 1 OR ([PspConnectionId] IS NOT NULL AND [SecretVersionId] IS NOT NULL AND [PspEnvironment] IS NOT NULL)"));
        builder.HasKey(x => x.Id);

        builder.Property(x => x.MerchantId).IsRequired();
        builder.Property(x => x.OrderId).IsRequired();

        TenantKeyDescriptor.Require(builder.Metadata, nameof(Session.MerchantId));
        // Second predicate mirrors OrderConfiguration: a bound merchant user (Tier 1 agent) reads only the
        // sessions of orders it initiated; a merchant-bound scope with no user keeps the merchant-wide read.
        builder.HasQueryFilter(x => x.MerchantId == context.CurrentMerchant
            && (context.CurrentMerchantUser == null
                || context.Orders.Any(o => o.Id == x.OrderId && o.InitiatingMerchantUserId == context.CurrentMerchantUser)));

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

        builder.Property(x => x.RowVersion).IsRowVersion();

        builder.HasIndex(x => new { x.Psp, x.PspExternalChargeId })
            .IsUnique()
            .HasFilter("[PspExternalChargeId] IS NOT NULL");

        builder.HasIndex(x => x.OrderId);

        // Mirrors the migration owner's one-open-session-per-order floor (REQ-2.4). Must stay identical in
        // both files: only the owner's copy reaches the DDL, only this copy is what runtime saves are
        // validated against, and a divergence is invisible to the unit suite (REQ-2.6 asserts both).
        builder.HasIndex(x => x.OrderId, "IX_PaymentSessions_OrderId_Open")
            .IsUnique()
            .HasFilter("[Status] IN (1, 2)");
    }
}
