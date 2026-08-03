using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using CartAggregate = Carts.Domain.Cart;

namespace Persistence.MerchantRuntime.Carts;

// Runtime (scalar-only) mapping — mirrors Carts.Infrastructure.CartConfiguration exactly for
// column/index shape (rls-to-query-filter design.md "Runtime EF config is scalar-only, separate from
// the migration-owner's relationship config"). The Items HasMany is KEPT (not stripped to scalar): Cart
// has a real CLR nav (`_items` field -> `Items` readonly property) that domain logic (AddItem/RemoveItem/
// Clear) depends on, and shop.CartItems is mapped by this SAME context — so this is a same-cluster,
// aggregate-internal relationship, not a cross-module one.

internal sealed class CartConfiguration(MerchantRuntimeDbContext context) : IEntityTypeConfiguration<CartAggregate>
{
    public void Configure(EntityTypeBuilder<CartAggregate> builder)
    {
        builder.ToTable("Carts", SchemaNames.Shop);
        builder.HasKey(x => x.Id);

        builder.Property(x => x.MerchantId).IsRequired();
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(x => x.CreatedAt).IsRequired();

        // Mirrors Carts.Infrastructure: the concurrency token must exist on BOTH contexts or the runtime
        // path would silently skip the WHERE Version = @original check that closes the freeze race.
        builder.Property(x => x.Version).IsConcurrencyToken().IsRequired();

        builder.Ignore(x => x.Subtotal);
        builder.Ignore(x => x.DomainEvents);

        TenantKeyDescriptor.Require(builder.Metadata, nameof(CartAggregate.MerchantId));
        builder.HasQueryFilter(x => x.MerchantId == context.CurrentMerchant);

        // Composite FK (CartId, MerchantId) -> Cart(Id, MerchantId), mirrors Carts.Infrastructure.CartConfiguration
        // (rls-to-query-filter REQ-6.5, Codex-R1 #8).
        builder.HasAlternateKey(x => new { x.Id, x.MerchantId });

        builder.HasMany(x => x.Items)
            .WithOne()
            .HasForeignKey(i => new { i.CartId, i.MerchantId })
            .HasPrincipalKey(x => new { x.Id, x.MerchantId })
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(x => x.Items).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
