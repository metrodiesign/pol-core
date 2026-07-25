using Checkouts.Domain;
using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Persistence.MerchantRuntime.Checkouts;

// Runtime (scalar-only) mapping — mirrors Checkouts.Infrastructure.SessionConfiguration exactly for
// column/index shape. CartId stays a bare scalar (no HasOne to Cart — none existed in the source config).

internal sealed class SessionConfiguration(MerchantRuntimeDbContext context) : IEntityTypeConfiguration<Session>
{
    public void Configure(EntityTypeBuilder<Session> builder)
    {
        builder.ToTable("CheckoutSessions", SchemaNames.Shop);
        builder.HasKey(x => x.Id);

        builder.Property(x => x.MerchantId).IsRequired();
        builder.Property(x => x.CartId).IsRequired();

        TenantKeyDescriptor.Require(builder.Metadata, nameof(Session.MerchantId));
        builder.HasQueryFilter(x => x.MerchantId == context.CurrentMerchant);

        builder.ComplexProperty(x => x.Amount, p =>
        {
            p.Property(m => m.Amount).HasColumnName("AmountAmount").HasPrecision(19, 4);
            p.Property(m => m.Currency).HasColumnName("AmountCurrency").HasMaxLength(3).IsFixedLength().IsUnicode(false);
        });

        builder.Property(x => x.Status).IsRequired();
        builder.Property(x => x.CreatedAt).IsRequired();
        builder.Property(x => x.NotificationRecipient).HasMaxLength(320);

        builder.Ignore(x => x.DomainEvents);

        // Composite alternate key + owned Items collection (insurance-pivot REQ-6.5), mirrors
        // Checkouts.Infrastructure.SessionConfiguration.
        builder.HasAlternateKey(x => new { x.Id, x.MerchantId });

        builder.HasMany(x => x.Items)
            .WithOne()
            .HasForeignKey(i => new { i.SessionId, i.MerchantId })
            .HasPrincipalKey(x => new { x.Id, x.MerchantId })
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(x => x.Items).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
