using MasterData.Domain;
using MasterData.Domain.Divisions;
using MasterData.Domain.Levels;
using MasterData.Domain.Offices;
using MasterData.Domain.Positions;
using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Persistence.ControlPlane.MasterData;

// Runtime mapping — mirrors MasterData.Infrastructure.Persistence.MasterDataConfigurations exactly (TPC,
// no relationships to strip).

public sealed class MasterDataConfiguration : IEntityTypeConfiguration<MasterDataItem>
{
    public void Configure(EntityTypeBuilder<MasterDataItem> builder)
    {
        builder.UseTpcMappingStrategy();
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Code).HasMaxLength(64).IsRequired();
        builder.Property(x => x.Name).HasMaxLength(200).IsRequired();
        builder.Property(x => x.IsActive).IsRequired();
        builder.HasIndex(x => x.Code).IsUnique();
    }
}

public sealed class PositionConfiguration : IEntityTypeConfiguration<Position>
{
    public void Configure(EntityTypeBuilder<Position> builder) => builder.ToTable("Positions", SchemaNames.Cfg);
}

public sealed class OfficeConfiguration : IEntityTypeConfiguration<Office>
{
    public void Configure(EntityTypeBuilder<Office> builder) => builder.ToTable("Offices", SchemaNames.Cfg);
}

public sealed class LevelConfiguration : IEntityTypeConfiguration<Level>
{
    public void Configure(EntityTypeBuilder<Level> builder) => builder.ToTable("Levels", SchemaNames.Cfg);
}

public sealed class DivisionConfiguration : IEntityTypeConfiguration<Division>
{
    public void Configure(EntityTypeBuilder<Division> builder) => builder.ToTable("Divisions", SchemaNames.Cfg);
}
