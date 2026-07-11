using Admin.Domain;
using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Admin.Infrastructure.Persistence;

// EF mappings for the four admin-profile master lists onto the producer schema (control-plane, no RLS,
// granted to pol_admin only — see the AddAdminMasterDataAndProfileFks migration). The shared MasterData base
// uses TPC (table-per-concrete) so each concrete master gets its OWN table with the full column set and NO
// base table / discriminator — the PlatformUser FKs stay type-safe. Shared columns/key/index live on the base.

public sealed class MasterDataConfiguration : IEntityTypeConfiguration<MasterData>
{
    public void Configure(EntityTypeBuilder<MasterData> builder)
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
    public void Configure(EntityTypeBuilder<Position> builder) => builder.ToTable("Positions", SchemaNames.Admin);
}

public sealed class OfficeConfiguration : IEntityTypeConfiguration<Office>
{
    public void Configure(EntityTypeBuilder<Office> builder) => builder.ToTable("Offices", SchemaNames.Admin);
}

public sealed class LevelConfiguration : IEntityTypeConfiguration<Level>
{
    public void Configure(EntityTypeBuilder<Level> builder) => builder.ToTable("Levels", SchemaNames.Admin);
}

public sealed class DivisionConfiguration : IEntityTypeConfiguration<Division>
{
    public void Configure(EntityTypeBuilder<Division> builder) => builder.ToTable("Divisions", SchemaNames.Admin);
}
