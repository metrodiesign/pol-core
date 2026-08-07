using BuildingBlocks.Infrastructure.Persistence;
using Merchants.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Merchants.Infrastructure.Persistence;

/// <summary>
/// Maps <see cref="Merchant"/> onto the merch schema. <c>Code</c> is unique (the idempotency key).
/// The PK <c>Id</c> is the merchant identity used by the RLS predicate. Discovered at model-build time by
/// <c>PolDbContext</c> via <c>ModuleAssemblies.Modules</c>.
/// </summary>
public sealed class MerchantConfiguration : IEntityTypeConfiguration<Merchant>
{
    public void Configure(EntityTypeBuilder<Merchant> builder)
    {
        builder.ToTable("Merchants", SchemaNames.Merch);
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Code).HasMaxLength(64).IsRequired();
        builder.Property(x => x.Name).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Note);
        builder.Property(x => x.Status).HasConversion<int>().IsRequired();
        builder.Property(x => x.Country).HasMaxLength(2).IsRequired();
        builder.Property(x => x.Currency).HasMaxLength(3).IsRequired();
        builder.Property(x => x.EnabledChannels).HasMaxLength(256).IsRequired();
        builder.Property(x => x.Metadata).HasColumnType("json").IsRequired();
        builder.Property(x => x.CreatedAt).IsRequired();

        builder.HasIndex(x => x.Code).IsUnique();
    }
}
