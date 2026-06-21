using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using CartAggregate = Cart.Domain.Cart;

namespace Cart.Infrastructure;

/// <summary>
/// Maps the cart aggregate into the shared <c>producer</c> schema. The items collection is owned by
/// the cart (a one-to-many to <see cref="Domain.CartItem"/>); the computed <c>Subtotal</c> and
/// <c>DomainEvents</c> are not persisted.
/// </summary>
public sealed class CartConfiguration : IEntityTypeConfiguration<CartAggregate>
{
    public void Configure(EntityTypeBuilder<CartAggregate> builder)
    {
        builder.ToTable("Carts");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.TenantId).IsRequired();
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(x => x.CreatedAtUtc).IsRequired();

        builder.Ignore(x => x.Subtotal);
        builder.Ignore(x => x.DomainEvents);

        builder.HasMany(x => x.Items)
            .WithOne()
            .HasForeignKey(i => i.CartId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(x => x.Items).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
