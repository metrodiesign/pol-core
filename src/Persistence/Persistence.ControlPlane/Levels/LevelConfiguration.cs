using Levels.Domain;
using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Persistence.ControlPlane.Levels;

// Runtime mapping — mirrors Levels.Infrastructure.Persistence.LevelConfiguration exactly (standalone entity since
// masterdata-split, no relationships to strip).
public sealed class LevelConfiguration : IEntityTypeConfiguration<Level>
{
    public void Configure(EntityTypeBuilder<Level> builder)
    {
        builder.ToTable("Levels", SchemaNames.Cfg);
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Code).HasMaxLength(64).IsRequired();
        builder.Property(x => x.Name).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Status).HasConversion<int>().IsRequired();
        builder.Property(x => x.Version).HasDefaultValue(1L).IsConcurrencyToken();
        builder.HasIndex(x => x.Code).IsUnique();
    }
}
