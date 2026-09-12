using BuildingBlocks.Infrastructure.Persistence;
using Checkouts.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Orders.Domain;

namespace Orders.Infrastructure;

/// <summary>Migration-owner mapping for the Checkout capability row. Token material is never a column.</summary>
public sealed class PaymentLinkConfiguration : IEntityTypeConfiguration<PaymentLink>
{
    public void Configure(EntityTypeBuilder<PaymentLink> builder)
    {
        builder.ToTable("PaymentLinks", SchemaNames.Checkout, table =>
            table.HasCheckConstraint("CK_PaymentLinks_Status", "[Status] IN (1, 2, 3)"));
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.OrderId).IsRequired();
        builder.Property(x => x.MerchantId).IsRequired();
        TenantKeyDescriptor.Require(builder.Metadata, nameof(PaymentLink.MerchantId));
        builder.Property(x => x.TokenHash).HasColumnType("binary(32)").IsRequired();
        builder.Property(x => x.Status).HasConversion<int>().IsRequired();
        builder.Property(x => x.CreatedAt).IsRequired();
        builder.Property(x => x.ExpiresAt).IsRequired();
        builder.Property(x => x.RevokedAt);
        builder.Property(x => x.RotatedFromLinkId);
        builder.Property(x => x.Version).IsConcurrencyToken().IsRequired();
        builder.HasIndex(x => x.TokenHash).IsUnique();
        builder.HasIndex(x => new { x.OrderId, x.Status })
            .HasFilter("[Status] = 1")
            .IsUnique();
        builder.HasOne<Order>().WithMany()
            .HasForeignKey(x => new { x.OrderId, x.MerchantId })
            .HasPrincipalKey(x => new { x.Id, x.MerchantId })
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class PaymentLinkReplayConfiguration : IEntityTypeConfiguration<PaymentLinkReplay>
{
    public void Configure(EntityTypeBuilder<PaymentLinkReplay> builder)
    {
        builder.ToTable("PaymentLinkReplays", SchemaNames.Checkout);
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.MerchantId).IsRequired();
        TenantKeyDescriptor.Require(builder.Metadata, nameof(PaymentLinkReplay.MerchantId));
        builder.Property(x => x.OrderId).IsRequired();
        builder.Property(x => x.LinkId);
        builder.Property(x => x.Operation).HasMaxLength(64).IsRequired();
        builder.Property(x => x.IdempotencyKey).HasMaxLength(200).IsRequired();
        builder.Property(x => x.RequestHash).HasColumnType("binary(32)").IsRequired();
        builder.Property(x => x.ProtectedRawToken).IsUnicode();
        builder.Property(x => x.CreatedAt).IsRequired();
        builder.Property(x => x.ExpiresAt).IsRequired();
        builder.HasIndex(x => new { x.MerchantId, x.Operation, x.IdempotencyKey }).IsUnique();
        builder.HasIndex(x => x.ExpiresAt);
        builder.HasOne<Order>().WithMany()
            .HasForeignKey(x => new { x.OrderId, x.MerchantId })
            .HasPrincipalKey(x => new { x.Id, x.MerchantId })
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<PaymentLink>().WithMany()
            .HasForeignKey(x => new { x.LinkId, x.MerchantId })
            .HasPrincipalKey(x => new { x.Id, x.MerchantId })
            .OnDelete(DeleteBehavior.Restrict);
    }
}
