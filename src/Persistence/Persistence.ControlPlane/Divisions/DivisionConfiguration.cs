using Divisions.Domain;
using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Persistence.ControlPlane.Divisions;

// Runtime mapping — mirrors Divisions.Infrastructure.Persistence.DivisionConfiguration exactly (standalone entity since
// masterdata-split, no relationships to strip).
public sealed class DivisionConfiguration : IEntityTypeConfiguration<Division>
{
    public void Configure(EntityTypeBuilder<Division> builder)
    {
        builder.ToTable("Divisions", SchemaNames.Cfg);
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Code).HasMaxLength(64).IsRequired();
        builder.Property(x => x.Name).HasMaxLength(200).IsRequired();
        builder.Property(x => x.IsActive).IsRequired();
        builder.HasIndex(x => x.Code).IsUnique();
    }
}
