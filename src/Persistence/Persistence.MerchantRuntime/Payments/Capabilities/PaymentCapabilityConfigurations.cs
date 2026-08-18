using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Payments.Domain.Capabilities;
using Payments.Domain.Psp;

namespace Persistence.MerchantRuntime.Payments.Capabilities;

internal sealed class MerchantProviderAccountMethodConfiguration(MerchantRuntimeDbContext context)
    : IEntityTypeConfiguration<MerchantProviderAccountMethod>
{
    public void Configure(EntityTypeBuilder<MerchantProviderAccountMethod> builder)
    {
        builder.ToTable("MerchantProviderAccountMethods", SchemaNames.Txn);
        builder.HasKey(x => x.Id);
        ConfigurePolicy(builder);
        TenantKeyDescriptor.Require(builder.Metadata, nameof(MerchantProviderAccountMethod.MerchantId));
        builder.HasQueryFilter(x => x.MerchantId == context.CurrentMerchant);
        builder.HasIndex(x => new { x.PspConnectionId, x.PaymentMethodId }).IsUnique();
        builder.HasAlternateKey(x => new
        {
            x.Id,
            x.MerchantId,
            x.PspConnectionId,
            x.PaymentProviderId,
            x.PaymentProviderMethodId,
            x.PaymentMethodId,
        });
        builder.HasOne<Connection>().WithMany()
            .HasForeignKey(x => new { x.MerchantId, x.PspConnectionId })
            .HasPrincipalKey(x => new { x.MerchantId, x.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigurePolicy(EntityTypeBuilder<MerchantProviderAccountMethod> builder)
    {
        builder.Property(x => x.MerchantId).IsRequired();
        builder.Property(x => x.IsEnabled).IsRequired();
        builder.Property(x => x.CreatedBy).IsRequired();
        builder.Property(x => x.CreatedAt).IsRequired();
        builder.Property(x => x.Version).IsConcurrencyToken().IsRequired();
    }
}

internal sealed class MerchantProviderAccountMethodOptionConfiguration(MerchantRuntimeDbContext context)
    : IEntityTypeConfiguration<MerchantProviderAccountMethodOption>
{
    public void Configure(EntityTypeBuilder<MerchantProviderAccountMethodOption> builder)
    {
        builder.ToTable("MerchantProviderAccountMethodOptions", SchemaNames.Txn);
        builder.HasKey(x => x.Id);
        builder.Property(x => x.MerchantId).IsRequired();
        builder.Property(x => x.IsEnabled).IsRequired();
        builder.Property(x => x.CreatedBy).IsRequired();
        builder.Property(x => x.CreatedAt).IsRequired();
        builder.Property(x => x.Version).IsConcurrencyToken().IsRequired();
        TenantKeyDescriptor.Require(builder.Metadata, nameof(MerchantProviderAccountMethodOption.MerchantId));
        builder.HasQueryFilter(x => x.MerchantId == context.CurrentMerchant);
        builder.HasIndex(x => new { x.MerchantProviderAccountMethodId, x.PaymentMethodOptionId }).IsUnique();
        builder.HasOne<MerchantProviderAccountMethod>().WithMany()
            .HasForeignKey(x => new
            {
                x.MerchantProviderAccountMethodId,
                x.MerchantId,
                x.PspConnectionId,
                x.PaymentProviderId,
                x.PaymentProviderMethodId,
                x.PaymentMethodId,
            })
            .HasPrincipalKey(x => new
            {
                x.Id,
                x.MerchantId,
                x.PspConnectionId,
                x.PaymentProviderId,
                x.PaymentProviderMethodId,
                x.PaymentMethodId,
            })
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class MerchantPaymentMethodConfiguration(MerchantRuntimeDbContext context)
    : IEntityTypeConfiguration<MerchantPaymentMethod>
{
    public void Configure(EntityTypeBuilder<MerchantPaymentMethod> builder)
    {
        builder.ToTable("MerchantPaymentMethods", SchemaNames.Txn);
        builder.HasKey(x => x.Id);
        ConfigurePolicy(builder);
        TenantKeyDescriptor.Require(builder.Metadata, nameof(MerchantPaymentMethod.MerchantId));
        builder.HasQueryFilter(x => x.MerchantId == context.CurrentMerchant);
        builder.HasAlternateKey(x => new { x.MerchantId, x.PaymentMethodId });
    }

    private static void ConfigurePolicy(EntityTypeBuilder<MerchantPaymentMethod> builder)
    {
        builder.Property(x => x.MerchantId).IsRequired();
        builder.Property(x => x.IsEnabled).IsRequired();
        builder.Property(x => x.CreatedBy).IsRequired();
        builder.Property(x => x.CreatedAt).IsRequired();
        builder.Property(x => x.Version).IsConcurrencyToken().IsRequired();
    }
}

internal sealed class MerchantUserPaymentMethodConfiguration(MerchantRuntimeDbContext context)
    : IEntityTypeConfiguration<MerchantUserPaymentMethod>
{
    public void Configure(EntityTypeBuilder<MerchantUserPaymentMethod> builder)
    {
        builder.ToTable("MerchantUserPaymentMethods", SchemaNames.Txn);
        builder.HasKey(x => x.Id);
        builder.Property(x => x.MerchantId).IsRequired();
        builder.Property(x => x.IsEnabled).IsRequired();
        builder.Property(x => x.CreatedBy).IsRequired();
        builder.Property(x => x.CreatedAt).IsRequired();
        builder.Property(x => x.Version).IsConcurrencyToken().IsRequired();
        TenantKeyDescriptor.Require(builder.Metadata, nameof(MerchantUserPaymentMethod.MerchantId));
        builder.HasQueryFilter(x => x.MerchantId == context.CurrentMerchant);
        builder.HasIndex(x => new { x.MerchantUserId, x.PaymentMethodId }).IsUnique();
        builder.HasOne<MerchantPaymentMethod>().WithMany()
            .HasForeignKey(x => new { x.MerchantId, x.PaymentMethodId })
            .HasPrincipalKey(x => new { x.MerchantId, x.PaymentMethodId })
            .OnDelete(DeleteBehavior.Restrict);
    }
}
