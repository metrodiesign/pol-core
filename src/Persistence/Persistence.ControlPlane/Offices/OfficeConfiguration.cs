using Offices.Domain;
using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Persistence.ControlPlane.Offices;

// Runtime mapping — mirrors Offices.Infrastructure.Persistence.OfficeConfiguration exactly (standalone entity since
// masterdata-split, no relationships to strip).
public sealed class OfficeConfiguration : IEntityTypeConfiguration<Office>
{
    public void Configure(EntityTypeBuilder<Office> builder)
    {
        builder.ToTable("Offices", SchemaNames.Cfg);
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Code).HasMaxLength(64).IsRequired();
        builder.Property(x => x.Name).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Status).HasConversion<int>().IsRequired();
        builder.Property(x => x.Version).HasDefaultValue(1L).IsConcurrencyToken();
        builder.HasIndex(x => x.Code).IsUnique();
        // tier0-graph-employee-profile REQ-6.1/6.2: operator-maintained legacy key, filtered-unique.
        builder.Property(x => x.LegacyKey).HasMaxLength(100);
        builder.HasIndex(x => x.LegacyKey).IsUnique().HasFilter("[LegacyKey] IS NOT NULL");
    }
}
