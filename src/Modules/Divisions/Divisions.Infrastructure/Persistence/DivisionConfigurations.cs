using Divisions.Domain;
using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Divisions.Infrastructure.Persistence;

// EF mapping for the division master list onto the cfg schema (control-plane, no query filter). Standalone
// entity — no shared base / TPC since masterdata-split; the facet set mirrors the retired shared
// base exactly and must stay in lockstep with the runtime copy in
// Persistence.ControlPlane/Divisions/DivisionConfiguration.cs.
public sealed class DivisionConfiguration : IEntityTypeConfiguration<Division>
{
    public void Configure(EntityTypeBuilder<Division> builder)
    {
        builder.ToTable("Divisions", SchemaNames.Cfg);
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Code).HasMaxLength(64).IsRequired();
        builder.Property(x => x.Name).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Status).HasConversion<int>().IsRequired();
        builder.Property(x => x.Version).HasDefaultValue(1L).IsConcurrencyToken();
        builder.HasIndex(x => x.Code).IsUnique();
    }
}
