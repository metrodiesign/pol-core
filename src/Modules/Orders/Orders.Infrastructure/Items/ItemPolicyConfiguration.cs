using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Orders.Domain.Items;

namespace Orders.Infrastructure.Items;

/// <summary>
/// Migration-owner mapping for <see cref="ItemPolicy"/> — columns/indexes only (the tenant-key/query-filter
/// wiring lives in the runtime twin, <c>Persistence.MerchantRuntime.Orders.Items.ItemPolicyConfiguration</c>,
/// per the dual-config pattern this codebase already uses everywhere else). Premium is 2 nullable scalar
/// pairs (Amount + Currency), not <c>ComplexProperty&lt;Money?&gt;</c> — sidesteps the EF Core 10
/// optional-complex-type bug (efcore#38043/#37249), design.md Technology Decision #7. The computed
/// <see cref="ItemPolicy.NetPremium"/>/<see cref="ItemPolicy.GrossPremium"/> properties are explicitly
/// ignored — they derive from the mapped scalar pairs, never mapped themselves.
/// </summary>
public sealed class ItemPolicyConfiguration : IEntityTypeConfiguration<ItemPolicy>
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
    }
}
