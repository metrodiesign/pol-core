using Checkouts.Domain;
using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Checkouts.Infrastructure;

/// <summary>
/// Maps <see cref="Session"/> onto the shop schema. <see cref="Session.Amount"/>
/// is mapped as a complex type (AmountAmount decimal(19,4), AmountCurrency char(3)) per the Money
/// mapping rule. Discovered at model-build time from the module's Infrastructure assembly.
/// </summary>
public sealed class SessionConfiguration : IEntityTypeConfiguration<Session>
{
    public void Configure(EntityTypeBuilder<Session> builder)
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

        // Composite alternate key + owned Items collection (insurance-pivot REQ-6.5), mirrors
        // Carts.Infrastructure.CartConfiguration's Cart/Item relationship.
        builder.HasAlternateKey(x => new { x.Id, x.MerchantId });

        builder.HasMany(x => x.Items)
            .WithOne()
            .HasForeignKey(i => new { i.SessionId, i.MerchantId })
            .HasPrincipalKey(x => new { x.Id, x.MerchantId })
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(x => x.Items).UsePropertyAccessMode(PropertyAccessMode.Field);

        // One live checkout per cart, enforced at the DB floor (purchase-flow-completion REQ-2.4): the
        // handler's GetOpenForCartAsync pre-check can lose a race, this cannot. Status 0/1 = Started/Confirmed;
        // Abandoned (2) falls outside the filter, so an abandoned checkout lets the same cart start a fresh one.
        // NAMED overload deliberately — EF keys UNNAMED indexes by property set, so an unnamed second
        // HasIndex(x => x.CartId) would MUTATE an existing CartId index instead of adding this one.
        builder.HasIndex(x => x.CartId, "IX_CheckoutSessions_CartId_Open")
            .IsUnique()
            .HasFilter("[Status] IN (0, 1)");
    }
}
