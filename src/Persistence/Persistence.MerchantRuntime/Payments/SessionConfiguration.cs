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
        builder.ToTable("PaymentSessions", SchemaNames.Txn);
        builder.HasKey(x => x.Id);

        builder.Property(x => x.MerchantId).IsRequired();
        builder.Property(x => x.OrderId).IsRequired();

        TenantKeyDescriptor.Require(builder.Metadata, nameof(Session.MerchantId));
        builder.HasQueryFilter(x => x.MerchantId == context.CurrentMerchant);

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

        builder.Property(x => x.RowVersion).IsRowVersion();

        builder.HasIndex(x => new { x.Psp, x.PspExternalChargeId })
            .IsUnique()
            .HasFilter("[PspExternalChargeId] IS NOT NULL");

        builder.HasIndex(x => x.OrderId);
    }
}
