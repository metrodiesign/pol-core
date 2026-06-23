using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using DomainTenant = Tenant.Domain.Tenant;

namespace Tenant.Infrastructure.Persistence;

/// <summary>
/// Maps <see cref="DomainTenant"/> onto the producer schema. <c>Code</c> is unique (the idempotency key).
/// The PK <c>Id</c> is the tenant identity used by the RLS predicate. Discovered at model-build time by
/// <c>ProducerDbContext</c> via <c>ModuleAssemblies.Producer</c>.
/// </summary>
public sealed class TenantConfiguration : IEntityTypeConfiguration<DomainTenant>
{
    public void Configure(EntityTypeBuilder<DomainTenant> builder)
    {
        builder.ToTable("Tenants");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Code).HasMaxLength(64).IsRequired();
        builder.Property(x => x.DisplayName).HasMaxLength(200).IsRequired();
        builder.Property(x => x.LegalEntityId).HasMaxLength(64).IsRequired();
        builder.Property(x => x.Status).HasConversion<int>().IsRequired();
        builder.Property(x => x.Country).HasMaxLength(2).IsRequired();
        builder.Property(x => x.Currency).HasMaxLength(3).IsRequired();
        builder.Property(x => x.EnabledChannels).HasMaxLength(256).IsRequired();
        builder.Property(x => x.Metadata).HasColumnType("nvarchar(max)").IsRequired();
        builder.Property(x => x.CreatedAt).IsRequired();

        builder.HasIndex(x => x.Code).IsUnique();
    }
}
