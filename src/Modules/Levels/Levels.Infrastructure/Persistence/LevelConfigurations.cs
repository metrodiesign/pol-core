using Levels.Domain;
using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Levels.Infrastructure.Persistence;

// EF mapping for the level master list onto the cfg schema (control-plane, no query filter). Standalone
// entity — no shared base / TPC since masterdata-split; the facet set mirrors the retired shared
// base exactly and must stay in lockstep with the runtime copy in
// Persistence.ControlPlane/Levels/LevelConfiguration.cs.
public sealed class LevelConfiguration : IEntityTypeConfiguration<Level>
{
    public void Configure(EntityTypeBuilder<Level> builder)
    {
        builder.ToTable("Levels", SchemaNames.Cfg);
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Code).HasMaxLength(64).IsRequired();
        builder.Property(x => x.Name).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Status).HasConversion<int>().IsRequired();
        builder.HasIndex(x => x.Code).IsUnique();
    }
}
