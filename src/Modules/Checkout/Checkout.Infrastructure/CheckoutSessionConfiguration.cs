using Checkout.Domain;
using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Checkout.Infrastructure;

/// <summary>
/// Maps <see cref="CheckoutSession"/> onto the shop schema. <see cref="CheckoutSession.Amount"/>
/// is mapped as a complex type (AmountAmount decimal(19,4), AmountCurrency char(3)) per the Money
/// mapping rule. Discovered at model-build time from the module's Infrastructure assembly.
/// </summary>
public sealed class CheckoutSessionConfiguration : IEntityTypeConfiguration<CheckoutSession>
{
    public void Configure(EntityTypeBuilder<CheckoutSession> builder)
    {
        builder.ToTable("CheckoutSessions", SchemaNames.Shop);
        builder.HasKey(x => x.Id);

        builder.Property(x => x.MerchantId).IsRequired();
        builder.Property(x => x.CartId).IsRequired();

        builder.ComplexProperty(x => x.Amount, p =>
        {
            p.Property(m => m.Amount).HasColumnName("AmountAmount").HasPrecision(19, 4);
            p.Property(m => m.Currency).HasColumnName("AmountCurrency").HasMaxLength(3).IsFixedLength().IsUnicode(false);
        });

        builder.Property(x => x.Status).IsRequired();
        builder.Property(x => x.CreatedAt).IsRequired();
        builder.Property(x => x.NotificationRecipient).HasMaxLength(320);

        builder.Ignore(x => x.DomainEvents);
    }
}
