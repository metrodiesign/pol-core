using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BuildingBlocks.Infrastructure.DataProtection;

/// <summary>
/// One row of the ASP.NET Core Data Protection key ring, persisted as a control-plane table so the OIDC
/// correlation/state cookies survive restarts and stay shared across instances (REQ-8). Plumbing, not a
/// domain entity: the value is an opaque, already-encrypted key-ring XML element produced by the framework.
/// Read/written only through the keyed pol_admin context via <c>EfCoreXmlRepository</c>.
/// </summary>
public sealed class DataProtectionKey
{
    public int Id { get; set; }
    public string? SecretKey { get; set; }
    public string Xml { get; set; } = default!;
}

public sealed class DataProtectionKeyConfiguration : IEntityTypeConfiguration<DataProtectionKey>
{
    public void Configure(EntityTypeBuilder<DataProtectionKey> builder)
    {
        builder.ToTable("DataProtectionKeys", SchemaNames.Dbo);
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();
        builder.Property(x => x.SecretKey).HasMaxLength(256);
        builder.Property(x => x.Xml).IsRequired(); // nvarchar(max) — the framework key-ring element
    }
}
