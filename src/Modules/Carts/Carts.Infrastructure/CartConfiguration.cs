using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using CartAggregate = Carts.Domain.Cart;

namespace Carts.Infrastructure;

/// <summary>
/// Maps the cart aggregate into the shared <c>shop</c> schema. The items collection is owned by
/// the cart (a one-to-many to <see cref="Domain.Items.Item"/>); the computed <c>Subtotal</c> and
/// <c>DomainEvents</c> are not persisted.
/// </summary>
public sealed class CartConfiguration : IEntityTypeConfiguration<CartAggregate>
{
    public void Configure(EntityTypeBuilder<CartAggregate> builder)
    {
        builder.ToTable("Carts", SchemaNames.Shop);
        builder.HasKey(x => x.Id);

        builder.Property(x => x.MerchantId).IsRequired();
        builder.Property(x => x.OriginatorId);
        builder.Property(x => x.SaleCode).HasMaxLength(20).IsUnicode(false);
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(x => x.CreatedAt).IsRequired();

        // Application-managed token (the domain bumps it on every mutation, item edits included) — makes
        // a cart-edit racing the checkout freeze a DbUpdateConcurrencyException instead of a silent
        // interleave. Not IsRowVersion: item-only edits never write the Carts row, and SQLite (host
        // tests) has no rowversion.
        builder.Property(x => x.Version).IsConcurrencyToken().IsRequired();

        builder.Ignore(x => x.Subtotal);
        builder.Ignore(x => x.DomainEvents);

        // Composite FK (CartId, MerchantId) -> Cart(Id, MerchantId) closes denormalization drift: an Item
        // stamped with a different MerchantId than its parent Cart cannot even be inserted (rls-to-query-filter
        // REQ-6.5, Codex-R1 #8). Requires an explicit alternate key on (Id, MerchantId) since Id alone is
        // already the PK.
        builder.HasAlternateKey(x => new { x.Id, x.MerchantId });

        builder.HasMany(x => x.Items)
            .WithOne()
            .HasForeignKey(i => new { i.CartId, i.MerchantId })
            .HasPrincipalKey(x => new { x.Id, x.MerchantId })
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(x => x.Items).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
