using Checkout.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Checkout.Infrastructure;

/// <summary>
/// Maps <see cref="CheckoutSession"/> onto the producer schema. The <see cref="CheckoutSession.Amount"/>
/// projection is ignored; the underlying scalar columns are mapped instead (per the Money mapping rule).
/// Discovered at model-build time from the module's producer assembly.
/// </summary>
public sealed class CheckoutSessionConfiguration : IEntityTypeConfiguration<CheckoutSession>
{
    public void Configure(EntityTypeBuilder<CheckoutSession> builder)
    {
        builder.ToTable("CheckoutSessions");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.TenantId).IsRequired();
        builder.Property(x => x.CartId).IsRequired();
        builder.Property(x => x.AmountMinorUnits).IsRequired();
        builder.Property(x => x.AmountCurrency).HasMaxLength(3).IsRequired();
        builder.Property(x => x.Status).IsRequired();
        builder.Property(x => x.CreatedAtUtc).IsRequired();

        builder.Ignore(x => x.Amount);
        builder.Ignore(x => x.DomainEvents);
    }
}
