using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Orders.Domain.Items;

namespace Persistence.MerchantRuntime.Orders.Items;

// Runtime (scalar-only) mapping — mirrors Orders.Infrastructure.Items.ItemPolicyConfiguration exactly for
// column/index shape (dual-config pattern), plus the tenant-key/query-filter wiring. ItemPolicy is mutable
// (unlike OrderItem, which is INSERT-only) — this is why it needed its own aggregate rather than fields on
// Item (design.md "Architecture Overview").

internal sealed class ItemPolicyConfiguration(MerchantRuntimeDbContext context) : IEntityTypeConfiguration<ItemPolicy>
{
    public void Configure(EntityTypeBuilder<ItemPolicy> builder)
    {
        builder.ToTable("OrderItemPolicies", SchemaNames.Shop);
        builder.HasKey(x => x.Id);

        builder.Property(x => x.OrderItemId).IsRequired();
        builder.Property(x => x.MerchantId).IsRequired();

        builder.Property(x => x.InsuranceCategory);
        builder.Property(x => x.ReferenceNumberType);
        builder.Property(x => x.ReferenceNumber).HasMaxLength(100);
        builder.Property(x => x.EndorsementNumber).HasMaxLength(100);
        builder.Property(x => x.RenewalReminderNumber).HasMaxLength(100);
        builder.Property(x => x.InsuredObjectReference).HasMaxLength(100);

        builder.Property(x => x.NetPremiumAmount).HasPrecision(19, 4);
        builder.Property(x => x.NetPremiumCurrency).HasMaxLength(3).IsFixedLength().IsUnicode(false);
        builder.Property(x => x.GrossPremiumAmount).HasPrecision(19, 4);
        builder.Property(x => x.GrossPremiumCurrency).HasMaxLength(3).IsFixedLength().IsUnicode(false);
        builder.Ignore(x => x.NetPremium);
        builder.Ignore(x => x.GrossPremium);

        builder.Property(x => x.PremiumRemittanceStatus).IsRequired();
        builder.Property(x => x.DeductedAt);
        builder.Property(x => x.CreatedAt).IsRequired();
        builder.Property(x => x.UpdatedAt).IsRequired();

        builder.HasIndex(x => x.OrderItemId).IsUnique();
        builder.HasIndex(x => x.MerchantId);

        TenantKeyDescriptor.Require(builder.Metadata, nameof(ItemPolicy.MerchantId));
        builder.HasQueryFilter(x => x.MerchantId == context.CurrentMerchant);
    }
}
