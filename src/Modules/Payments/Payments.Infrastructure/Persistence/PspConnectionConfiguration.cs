using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Payments.Domain;

namespace Payments.Infrastructure.Persistence;

/// <summary>
/// Maps <see cref="PspConnection"/> onto the producer schema. Only the vault lookup name is stored,
/// never the secret. One connection per (tenant, PSP) is enforced by a unique index.
/// </summary>
public sealed class PspConnectionConfiguration : IEntityTypeConfiguration<PspConnection>
{
    public void Configure(EntityTypeBuilder<PspConnection> builder)
    {
        builder.ToTable("PspConnections");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.TenantId).IsRequired();
        builder.Property(x => x.Psp).IsRequired();
        builder.Property(x => x.EnabledMethods).HasMaxLength(256).IsRequired();
        builder.Property(x => x.SecretRefName).HasMaxLength(128).IsRequired();
        builder.Property(x => x.Metadata).HasMaxLength(4000);
        builder.Property(x => x.IsEnabled).IsRequired();
        builder.Property(x => x.CreatedAtUtc).IsRequired();

        builder.HasIndex(x => new { x.TenantId, x.Psp }).IsUnique();
    }
}
