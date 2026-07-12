using MasterData.Domain;
using MasterData.Domain.Divisions;
using MasterData.Domain.Levels;
using MasterData.Domain.Offices;
using MasterData.Domain.Positions;
using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MasterData.Infrastructure.Persistence;

// EF mappings for the four admin-profile master lists onto the cfg schema (control-plane, no RLS, granted to
// pol_admin only — see the SecurityObjects migration). The shared MasterDataItem base uses TPC (table-per-concrete)
// so each concrete master gets its OWN table with the full column set and NO base table / discriminator — the
// User FKs stay type-safe. Shared columns/key/index live on the base.

public sealed class MasterDataConfiguration : IEntityTypeConfiguration<MasterDataItem>
{
    public void Configure(EntityTypeBuilder<MasterDataItem> builder)
    {
        builder.UseTpcMappingStrategy();   // one table per concrete type, no base table
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
