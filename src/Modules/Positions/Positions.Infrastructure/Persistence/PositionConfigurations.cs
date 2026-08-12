using Positions.Domain;
using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Positions.Infrastructure.Persistence;

// EF mapping for the position master list onto the cfg schema (control-plane, no query filter). Standalone
// entity — no shared base / TPC since masterdata-split; the facet set mirrors the retired shared
// base exactly and must stay in lockstep with the runtime copy in
// Persistence.ControlPlane/Positions/PositionConfiguration.cs.
public sealed class PositionConfiguration : IEntityTypeConfiguration<Position>
{
    public void Configure(EntityTypeBuilder<Position> builder)
    {
        builder.ToTable("Positions", SchemaNames.Cfg);
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Code).HasMaxLength(64).IsRequired();
        builder.Property(x => x.Name).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Status).HasConversion<int>().IsRequired();
        builder.Property(x => x.Version).HasDefaultValue(1L).IsConcurrencyToken();
        builder.HasIndex(x => x.Code).IsUnique();
    }
}
