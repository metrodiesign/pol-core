using BuildingBlocks.Infrastructure.Persistence;
using Merchants.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Merchants.Infrastructure.Persistence;

public sealed class SaleConfiguration : IEntityTypeConfiguration<Sale>
{
    public void Configure(EntityTypeBuilder<Sale> builder)
    {
        builder.ToTable("Sales", SchemaNames.Merch);
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.MerchantId).IsRequired();
        builder.Property(x => x.BranchId).IsRequired();
        builder.Property(x => x.Code).HasMaxLength(64).IsRequired();
        builder.Property(x => x.Name).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Status).HasConversion<int>().IsRequired();
        builder.Property(x => x.CreatedAt).IsRequired();
        builder.Property(x => x.UpdatedAt).IsRequired();
        builder.Property(x => x.Version).IsConcurrencyToken().IsRequired();
        builder.HasIndex(x => new { x.MerchantId, x.Code }).IsUnique();
        builder.HasAlternateKey(x => new { x.MerchantId, x.Id });
        builder.HasOne<Merchant>().WithMany().HasForeignKey(x => x.MerchantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Branch>().WithMany()
            .HasForeignKey(x => new { x.MerchantId, x.BranchId })
            .HasPrincipalKey(x => new { x.MerchantId, x.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}
